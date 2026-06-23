// ============================================================================
//  ZFTP — MountSession
//  ---------------------------------------------------------------------------
//  Wraps the whole "connect to SFTP + present a drive letter" lifecycle for a
//  single server, so the GUI can just call MountAsync() / Unmount() and watch
//  the State change. One MountSession == one drive.
// ============================================================================

using System.Diagnostics;
using System.IO;
using Fsp;
using Renci.SshNet;

namespace ZFTP.Core;

public enum MountState
{
    Disconnected,
    Connecting,
    Mounted,
    Error,
    Reconnecting,
}

public sealed class MountSession : IDisposable
{
    public ConnectionProfile Profile { get; }
    public MountState State { get; private set; } = MountState.Disconnected;
    public string? LastError { get; private set; }
    public string MountPoint { get; private set; } = "";

    private SftpClient? _client;
    private FileSystemHost? _host;
    private SftpFileSystem? _fs;
    private Process? _rcloneProc;   // used for non-SFTP (rclone-backed) drives

    private readonly object _sync = new();
    private volatile bool _shouldBeMounted;   // user wants this mounted → watchdog keeps it alive
    private volatile bool _busy;              // a mount/reconnect is in progress
    private readonly System.Threading.Timer _watchdog;

    /// <summary>Fires whenever State changes, so the UI can refresh.</summary>
    public event Action<MountSession>? StateChanged;

    public MountSession(ConnectionProfile profile)
    {
        Profile = profile;
        // Check the connection every 15s and silently remount if it dropped.
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public bool IsMounted => State == MountState.Mounted;

    /// <summary>Total bytes downloaded/uploaded this session (for the speed display).</summary>
    public long BytesRead => _fs?.BytesRead ?? 0;
    public long BytesWritten => _fs?.BytesWritten ?? 0;

    /// <summary>Connect and mount on a background thread (keeps the UI responsive).</summary>
    public Task<bool> MountAsync() => Task.Run(Mount);

    public bool Mount()
    {
        lock (_sync)
        {
            _busy = true;
            try
            {
                // Make sure WinFsp's native DLL is loaded before any Fsp type is used.
                WinFspNative.EnsureLoaded();
                if (!WinFspNative.Available)
                    throw new InvalidOperationException(
                        "WinFsp is not installed. ZFTP needs WinFsp to create drive letters. " +
                        "Reinstall ZFTP (the installer includes WinFsp) or install it from winfsp.dev.");

                MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";

                // SFTP uses our native engine; every other provider goes through rclone.
                return Profile.Provider == ProviderType.Sftp ? MountSftp() : MountRclone();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Cleanup();
                SetState(MountState.Error);
                return false;
            }
            finally { _busy = false; }
        }
    }

    /// <summary>
    /// Runs every 15s. If a previously-mounted drive's connection dropped (SFTP
    /// session lost, or the rclone process died), silently tear it down and
    /// remount — so drives survive sleep, Wi-Fi drops, and brief outages.
    /// </summary>
    private void Watchdog()
    {
        // This runs on a background timer thread — it must NEVER let an exception
        // escape, or it would take down the whole app.
        try
        {
            if (!_shouldBeMounted || _busy) return;

            bool dead;
            try
            {
                dead = Profile.Provider == ProviderType.Sftp
                    ? (_client == null || !_client.IsConnected)
                    : (_rcloneProc == null || _rcloneProc.HasExited);
            }
            catch { dead = true; }
            if (!dead) return;

            lock (_sync)
            {
                if (!_shouldBeMounted || _busy) return;
                _busy = true;
                try
                {
                    SetState(MountState.Reconnecting);
                    Cleanup();   // free the drive letter from the dead mount
                    MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
                    bool ok = Profile.Provider == ProviderType.Sftp ? MountSftp() : MountRclone();
                    if (!ok && _shouldBeMounted) SetState(MountState.Reconnecting);
                }
                catch { if (_shouldBeMounted) SetState(MountState.Reconnecting); }
                finally { _busy = false; }
            }
        }
        catch { /* swallow — never crash the timer thread */ }
    }

    // ---- rclone-backed providers (FTP/FTPS/WebDAV/S3/cloud drives) ----------

    private bool MountRclone()
    {
        if (!RcloneService.Available)
            throw new InvalidOperationException("The rclone engine wasn't found. Reinstall ZFTP.");

        SetState(MountState.Connecting);

        // Credential backends are configured here; OAuth ones must already be
        // authorized (the Edit dialog runs the browser sign-in once).
        if (!RcloneService.RequiresOAuth(Profile.Provider))
        {
            if (!RcloneService.CreateCredentialRemote(Profile))
                throw new InvalidOperationException("Couldn't configure the connection. Check the details and try again.");
        }

        _rcloneProc = RcloneService.Mount(Profile, MountPoint);

        // Wait for the drive to actually appear (rclone takes a second or two).
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (_rcloneProc.HasExited)
                throw new InvalidOperationException("The connection failed before the drive could mount. Double-check the address and credentials.");
            if (Directory.Exists(MountPoint))
            {
                LastError = null;
                _shouldBeMounted = true;
                SetState(MountState.Mounted);
                return true;
            }
            System.Threading.Thread.Sleep(400);
        }
        throw new InvalidOperationException("Timed out waiting for the drive to appear.");
    }

    // ---- native SFTP engine ------------------------------------------------

    private bool MountSftp()
    {
        try
        {
            SetState(MountState.Connecting);

            _client = new SftpClient(BuildConnectionInfo())
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            };

            // Host-key verification: remember the server's key on first connect,
            // and refuse to connect if it ever changes (possible impersonation).
            bool hostKeyMismatch = false;
            _client.HostKeyReceived += (_, e) =>
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var fp = Convert.ToBase64String(sha.ComputeHash(e.HostKey));
                if (string.IsNullOrEmpty(Profile.KnownHostKey))
                {
                    Profile.KnownHostKey = fp;     // trust on first use
                    e.CanTrust = true;
                }
                else if (Profile.KnownHostKey == fp)
                {
                    e.CanTrust = true;
                }
                else
                {
                    hostKeyMismatch = true;
                    e.CanTrust = false;
                }
            };

            try { _client.Connect(); }
            catch when (hostKeyMismatch)
            {
                throw new InvalidOperationException(
                    "The server's identity (host key) has CHANGED since you last connected. " +
                    "That can mean the server was rebuilt — or that something is impersonating it. " +
                    "If you trust this change, tick \"Forget saved server key\" in this drive's Edit dialog, then reconnect.");
            }

            // "/" (or blank) means "use my SFTP home directory".
            string root = Profile.RemoteRoot;
            if (string.IsNullOrWhiteSpace(root) || root == "/")
                root = string.IsNullOrEmpty(_client.WorkingDirectory) ? "/" : _client.WorkingDirectory;

            _fs = new SftpFileSystem(_client, root, Profile.Name, Profile.Access == AccessMode.ReadOnly);
            _host = new FileSystemHost(_fs)
            {
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                MaxComponentLength = 255,
                FileInfoTimeout = 1000,
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                FileSystemName = "ZFTP",
                // No UNC prefix → Windows treats it as a regular LOCAL drive, so it
                // shows under "Devices and drives" in This PC (not Network locations).
                // Local drives don't leave the disconnected "ghost" cache entries.
            };

            MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
            int status = _host.Mount(MountPoint, null, true, 0);
            if (status != 0)
            {
                // The mount failed, so this host owns nothing — drop it WITHOUT
                // calling Unmount (which could tear down another app's drive on
                // the same letter). Then report the error.
                _host = null;
                throw new InvalidOperationException(
                    $"WinFsp could not mount {MountPoint} (status 0x{status:X8}). " +
                    "Is the drive letter already in use?");
            }

            LastError = null;
            _shouldBeMounted = true;
            SetState(MountState.Mounted);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Cleanup();
            SetState(MountState.Error);
            return false;
        }
    }

    public void Unmount()
    {
        lock (_sync)
        {
            _shouldBeMounted = false;   // stop the watchdog from remounting
            Cleanup();
            SetState(MountState.Disconnected);
        }
    }

    private void Cleanup()
    {
        // native SFTP
        try { _host?.Unmount(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _host = null;
        _client = null;
        _fs = null;

        // rclone-backed: killing rclone unmounts its drive (WinFsp tears down on exit)
        try { if (_rcloneProc is { HasExited: false }) _rcloneProc.Kill(); } catch { /* ignore */ }
        try { _rcloneProc?.Dispose(); } catch { /* ignore */ }
        _rcloneProc = null;
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        if (Profile.Auth == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(Profile.KeyPath))
        {
            var keyFile = string.IsNullOrEmpty(Profile.KeyPassphrase)
                ? new PrivateKeyFile(Profile.KeyPath)
                : new PrivateKeyFile(Profile.KeyPath, Profile.KeyPassphrase);
            return new ConnectionInfo(Profile.Host, Profile.Port, Profile.Username,
                new PrivateKeyAuthenticationMethod(Profile.Username, keyFile));
        }

        return new ConnectionInfo(Profile.Host, Profile.Port, Profile.Username,
            new PasswordAuthenticationMethod(Profile.Username, Profile.Password));
    }

    private void SetState(MountState state)
    {
        State = state;
        StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        _shouldBeMounted = false;
        try { _watchdog.Dispose(); } catch { }
        Cleanup();
    }
}
