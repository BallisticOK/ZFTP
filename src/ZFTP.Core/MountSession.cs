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
    private AdbFileSystem? _adb;     // used for the Android (adb) provider
    private AfcFileSystem? _afc;     // used for the iPhone/iPad (Apple AFC) provider
    private Process? _rcloneProc;   // used for non-SFTP (rclone-backed) drives

    private SshClient? _statsClient;        // separate SSH channel used only to run `df`
    private volatile bool _statsUnsupported; // server blocked exec / df missing → stop probing

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
    public long BytesRead => _fs?.BytesRead ?? _adb?.BytesRead ?? _afc?.BytesRead ?? 0;
    public long BytesWritten => _fs?.BytesWritten ?? _adb?.BytesWritten ?? _afc?.BytesWritten ?? 0;

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

                // SFTP and Android use our native engines; every other provider goes through rclone.
                return Profile.Provider switch
                {
                    ProviderType.Sftp => MountSftp(),
                    ProviderType.Android => MountAndroid(),
                    ProviderType.IPhone => MountApple(),
                    _ => MountRclone(),
                };
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

            // While healthily mounted, keep the SFTP drive's reported size current.
            if (State == MountState.Mounted && Profile.Provider == ProviderType.Sftp && !_statsUnsupported)
                Task.Run(RefreshDiskSpace);

            bool dead;
            try
            {
                dead = Profile.Provider switch
                {
                    ProviderType.Sftp => _client == null || !_client.IsConnected,
                    ProviderType.Android => _adb == null || !AdbService.DeviceConnected(ResolveSerial()),
                    ProviderType.IPhone => _afc == null || !AppleDeviceService.DeviceConnected(ResolveSerial()),
                    _ => _rcloneProc == null || _rcloneProc.HasExited,
                };
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
                    bool ok = Profile.Provider switch
                    {
                        ProviderType.Sftp => MountSftp(),
                        ProviderType.Android => MountAndroid(),
                        ProviderType.IPhone => MountApple(),
                        _ => MountRclone(),
                    };
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

    // ---- native Android (adb) engine ---------------------------------------

    /// <summary>
    /// Which device serial to mount: the one saved on the profile if it's plugged
    /// in, otherwise the only connected device (so "just plug in your phone" works
    /// when the profile doesn't pin a specific device).
    /// </summary>
    private string ResolveSerial()
    {
        var saved = Profile.DeviceSerial?.Trim() ?? "";
        if (!string.IsNullOrEmpty(saved)) return saved;
        var devices = Profile.Provider == ProviderType.IPhone
            ? AppleDeviceService.ListUdids()
            : AdbService.ListDeviceSerials();
        return devices.Length == 1 ? devices[0] : "";
    }

    private bool MountAndroid()
    {
        try
        {
            SetState(MountState.Connecting);

            if (!AdbService.Available)
                throw new InvalidOperationException(
                    "The Android engine (adb) wasn't found. Reinstall ZFTP.");

            AdbService.EnsureServer();

            var devices = AdbService.ListDeviceSerials();
            if (devices.Length == 0)
                throw new InvalidOperationException(
                    "No Android device detected. Connect your phone via USB, turn on " +
                    "\"USB debugging\" in Developer options, and tap \"Allow\" on the phone.");

            var saved = Profile.DeviceSerial?.Trim() ?? "";
            string serial;
            if (!string.IsNullOrEmpty(saved))
            {
                if (!devices.Contains(saved, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The Android device saved for this drive isn't connected. Plug it in " +
                        "and unlock it, or open Edit and pick a connected device.");
                serial = saved;
            }
            else if (devices.Length == 1)
            {
                serial = devices[0];
            }
            else
            {
                throw new InvalidOperationException(
                    "More than one Android device is connected. Open this drive's Edit dialog " +
                    "and choose which device to mount.");
            }

            string root = string.IsNullOrWhiteSpace(Profile.RemoteRoot) || Profile.RemoteRoot == "/"
                ? "/sdcard" : Profile.RemoteRoot;

            _adb = new AdbFileSystem(serial, root, Profile.Name, Profile.Access == AccessMode.ReadOnly);
            _host = new FileSystemHost(_adb)
            {
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                MaxComponentLength = 255,
                FileInfoTimeout = 5000,
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                FileSystemName = "ZFTP",
            };

            MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
            int status = _host.Mount(MountPoint, null, true, 0);
            if (status != 0)
            {
                _host = null;
                throw new InvalidOperationException(
                    $"WinFsp could not mount {MountPoint} (status 0x{status:X8}). " +
                    "Is the drive letter already in use?");
            }

            LastError = null;
            _shouldBeMounted = true;
            SetState(MountState.Mounted);

            // Learn the device's real storage size in the background (best-effort).
            var fs = _adb;
            Task.Run(() =>
            {
                try
                {
                    if (AdbService.TryGetDiskSpace(serial, root, out long total, out long free))
                        fs.UpdateVolumeSpace(total, free);
                }
                catch { /* keep the placeholder size */ }
            });
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

    // ---- native iPhone/iPad (Apple AFC) engine -----------------------------

    private bool MountApple()
    {
        try
        {
            SetState(MountState.Connecting);

            if (!AppleDeviceService.Available)
                throw new InvalidOperationException(
                    "The Apple device engine couldn't load. Reinstall ZFTP, and install " +
                    "Apple Devices (or iTunes) so the iPhone USB driver is present.");

            var devices = AppleDeviceService.ListUdids();
            if (devices.Length == 0)
                throw new InvalidOperationException(
                    "No iPhone or iPad detected. Connect it via USB, unlock it, and tap " +
                    "\"Trust This Computer\" if asked. (Installing Apple Devices / iTunes " +
                    "provides the required USB driver.)");

            var saved = Profile.DeviceSerial?.Trim() ?? "";
            string udid;
            if (!string.IsNullOrEmpty(saved))
            {
                if (!devices.Contains(saved, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The iPhone/iPad saved for this drive isn't connected. Plug it in and " +
                        "unlock it, or open Edit and pick a connected device.");
                udid = saved;
            }
            else if (devices.Length == 1)
            {
                udid = devices[0];
            }
            else
            {
                throw new InvalidOperationException(
                    "More than one Apple device is connected. Open this drive's Edit dialog " +
                    "and choose which device to mount.");
            }

            // "/" is the AFC media root (photos, app File-Sharing folders, etc.).
            string root = string.IsNullOrWhiteSpace(Profile.RemoteRoot) ? "/" : Profile.RemoteRoot;

            _afc = new AfcFileSystem(udid, root, Profile.Name, Profile.Access == AccessMode.ReadOnly);
            _host = new FileSystemHost(_afc)
            {
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                MaxComponentLength = 255,
                FileInfoTimeout = 5000,
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                FileSystemName = "ZFTP",
            };

            MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
            int status = _host.Mount(MountPoint, null, true, 0);
            if (status != 0)
            {
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

    // ---- native SFTP engine ------------------------------------------------

    private bool MountSftp()
    {
        try
        {
            SetState(MountState.Connecting);

            // Common mix-up: someone points a *plain FTP* server at the SFTP engine.
            // SFTP (over SSH, usually port 22) and FTP (usually port 21) are totally
            // different protocols. SSH.NET would just sit there waiting for an SSH
            // banner the FTP server never sends, then fail after 30s with a baffling
            // "socket read operation has timed out". Catch it up front and say what's
            // actually wrong, instantly.
            if (LooksLikeFtpServer(Profile.Host, Profile.Port))
                throw new InvalidOperationException(
                    "This looks like an FTP server, not an SFTP (SSH) server — they're " +
                    "different protocols. Open this drive's Edit dialog, change its Type " +
                    "to \"FTP\" (or \"FTPS\" if it uses TLS), and reconnect.");

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
                // Let WinFsp trust the file info we hand back for a few seconds, so
                // Explorer stops re-asking for every file's details while browsing.
                FileInfoTimeout = 5000,
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

            // Learn the drive's real size in the background (best-effort; many
            // SFTP-only servers block shell exec, in which case we keep the
            // placeholder size and never try again).
            _statsUnsupported = false;
            Task.Run(RefreshDiskSpace);
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

    // ---- real drive size (SFTP) -------------------------------------------

    /// <summary>
    /// Ask the server for the real size/free space of the mounted path by running
    /// `df` over a second SSH channel, and feed the numbers to the filesystem so
    /// Explorer shows the true drive size instead of a placeholder. Best-effort:
    /// if the server doesn't allow running commands, we give up quietly.
    /// </summary>
    private void RefreshDiskSpace()
    {
        if (_statsUnsupported) return;
        var fs = _fs;
        if (fs == null) return;

        try
        {
            if (_statsClient is not { IsConnected: true })
            {
                try { _statsClient?.Dispose(); } catch { }
                _statsClient = new SshClient(BuildConnectionInfo());
                _statsClient.Connect();
            }

            string path = string.IsNullOrWhiteSpace(Profile.RemoteRoot) ? "/" : Profile.RemoteRoot;
            // -P = POSIX one-line output, -k = 1024-byte blocks.
            var cmd = _statsClient.RunCommand("df -Pk " + ShellQuote(path));

            if (cmd.ExitStatus == 0 && TryParseDf(cmd.Result, out long total, out long free))
            {
                fs.UpdateVolumeSpace(total, free);
            }
            else
            {
                // df missing or exec blocked — stop probing and drop the channel.
                _statsUnsupported = true;
                try { _statsClient?.Dispose(); } catch { }
                _statsClient = null;
            }
        }
        catch
        {
            _statsUnsupported = true;
            try { _statsClient?.Dispose(); } catch { }
            _statsClient = null;
        }
    }

    /// <summary>Single-quote a path for a POSIX shell (handles embedded quotes).</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Parse `df -Pk` output. The data row is:
    /// Filesystem  1024-blocks  Used  Available  Capacity  Mounted-on
    /// We take total = blocks*1024 and free = available*1024.
    /// </summary>
    private static bool TryParseDf(string output, out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;
        if (string.IsNullOrWhiteSpace(output)) return false;

        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
                continue;

            var cols = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 4
                && long.TryParse(cols[1], out long blocks)
                && long.TryParse(cols[3], out long avail)
                && blocks > 0)
            {
                totalBytes = blocks * 1024L;
                freeBytes = avail * 1024L;
                return true;
            }
        }
        return false;
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
        // native SFTP / Android share the WinFsp host
        try { _host?.Unmount(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _statsClient?.Disconnect(); } catch { /* ignore */ }
        try { _statsClient?.Dispose(); } catch { /* ignore */ }
        try { _adb?.Dispose(); } catch { /* ignore */ }
        try { _afc?.Dispose(); } catch { /* ignore */ }
        _host = null;
        _client = null;
        _fs = null;
        _adb = null;
        _afc = null;
        _statsClient = null;

        // rclone-backed: killing rclone unmounts its drive (WinFsp tears down on exit)
        try { if (_rcloneProc is { HasExited: false }) _rcloneProc.Kill(); } catch { /* ignore */ }
        try { _rcloneProc?.Dispose(); } catch { /* ignore */ }
        _rcloneProc = null;
    }

    /// <summary>
    /// Quick, best-effort check for "is the thing on the other end actually an FTP
    /// server?" — used to give a clear error instead of a 30s SSH timeout. An SSH
    /// server greets with "SSH-..."; an FTP server greets with a 3-digit reply code
    /// (e.g. "220 Welcome"). We connect, read the first few bytes, and decide. Any
    /// hiccup (no greeting, refused, timeout) returns false so the normal SFTP
    /// connect still runs and reports its own error.
    /// </summary>
    private static bool LooksLikeFtpServer(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.BeginConnect(host, port <= 0 ? 22 : port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(4))) return false;
            tcp.EndConnect(connect);

            using var ns = tcp.GetStream();
            ns.ReadTimeout = 3000;
            var buf = new byte[8];
            int n = ns.Read(buf, 0, buf.Length);
            if (n <= 0) return false;

            var greeting = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            // SSH server → starts with "SSH-". FTP server → starts with a numeric
            // status code like "220".
            if (greeting.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase)) return false;
            return greeting.Length >= 3
                && char.IsDigit(greeting[0]) && char.IsDigit(greeting[1]) && char.IsDigit(greeting[2]);
        }
        catch { return false; }
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
