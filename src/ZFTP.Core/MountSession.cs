// ============================================================================
//  ZFTP — MountSession
//  ---------------------------------------------------------------------------
//  Wraps the whole "connect to SFTP + present a drive letter" lifecycle for a
//  single server, so the GUI can just call MountAsync() / Unmount() and watch
//  the State change. One MountSession == one drive.
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

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
            // Repeated clicks (or a tray/startup mount racing a UI click) should
            // not tear down and recreate a drive that is already healthy.
            if (State == MountState.Mounted)
                return true;

            _busy = true;
            try
            {
                AppLog.Info("Mount", $"Mount requested: profile='{Profile.Name}', provider={Profile.Provider}, drive={Profile.DriveLetter}.");

                // Make sure WinFsp's native DLL is loaded before any Fsp type is used.
                WinFspNative.EnsureLoaded();
                if (!WinFspNative.Available)
                    throw new InvalidOperationException(
                        "WinFsp is not installed. ZFTP needs WinFsp to create drive letters. " +
                        "Reinstall ZFTP (the installer includes WinFsp) or install it from winfsp.dev. " +
                        (string.IsNullOrWhiteSpace(WinFspNative.LastError) ? "" : WinFspNative.LastError));

                MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
                WaitForMountPointFree();

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
                AppLog.Error("Mount", $"Mount failed for profile '{Profile.Name}' on {MountPoint}.", ex);
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
                    AppLog.Warn("Watchdog", $"Mount for '{Profile.Name}' appears to be down; attempting reconnect.");
                    SetState(MountState.Reconnecting);
                    Cleanup();   // free the drive letter from the dead mount
                    MountPoint = Profile.DriveLetter.TrimEnd(':', '\\') + ":";
                    WaitForMountPointFree();
                    bool ok = Profile.Provider switch
                    {
                        ProviderType.Sftp => MountSftp(),
                        ProviderType.Android => MountAndroid(),
                        ProviderType.IPhone => MountApple(),
                        _ => MountRclone(),
                    };
                    if (!ok && _shouldBeMounted) SetState(MountState.Reconnecting);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Watchdog", $"Reconnect failed for '{Profile.Name}'.", ex);
                    if (_shouldBeMounted) SetState(MountState.Reconnecting);
                }
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
            if (!RcloneService.CreateCredentialRemote(Profile, out var configError))
                throw new InvalidOperationException(
                    "Couldn't configure the rclone connection. " + configError);
        }

        _rcloneProc = RcloneService.Mount(Profile, MountPoint);

        // Wait for the drive to actually appear (rclone takes a second or two).
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (_rcloneProc.HasExited)
            {
                var detail = RcloneService.GetProcessFailure(_rcloneProc);
                throw new InvalidOperationException(
                    "rclone stopped before the drive could mount. " +
                    (string.IsNullOrWhiteSpace(detail) ? "Double-check the address and credentials." : detail));
            }
            if (MountPointReady())
            {
                LastError = null;
                _shouldBeMounted = true;
                SetState(MountState.Mounted);
                AppLog.Info("Mount", $"rclone drive {MountPoint} is visible and mounted for '{Profile.Name}'.");
                return true;
            }
            System.Threading.Thread.Sleep(400);
        }
        throw new InvalidOperationException(
            $"Timed out waiting for {MountPoint} to appear. rclone is still running, so this usually means WinFsp " +
            "could not publish the drive letter. Check that the letter is free and that WinFsp is installed correctly.");
    }

    private bool MountPointInUse()
    {
        try
        {
            var letter = char.ToUpperInvariant(Profile.DriveLetter.TrimEnd(':', '\\')[0]);
            if (DriveInfo.GetDrives().Any(d => d.Name.Length > 0 && char.ToUpperInvariant(d.Name[0]) == letter))
                return true;

            // QueryDosDevice also catches SUBST drives and some mapped/disconnected
            // letters that can be absent from DriveInfo.GetDrives() on Windows 11.
            var target = new StringBuilder(1024);
            return QueryDosDevice($"{letter}:", target, target.Capacity) != 0;
        }
        catch { return false; }
    }

    private void WaitForMountPointFree()
    {
        if (!MountPointInUse()) return;

        AppLog.Warn("Mount", $"Drive {MountPoint} is still in use; waiting for Windows to release it.");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            System.Threading.Thread.Sleep(250);
            if (!MountPointInUse())
            {
                AppLog.Info("Mount", $"Drive {MountPoint} was released after {sw.ElapsedMilliseconds} ms.");
                return;
            }
        }

        throw new InvalidOperationException(
            $"Drive {MountPoint} is already in use. Windows did not release the letter after 5 seconds. " +
            "Choose another drive letter or close/disconnect the program currently using it.");
    }

    private void VerifyNativeMountVisible()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (MountPointReady())
            {
                AppLog.Info("Mount", $"WinFsp drive {MountPoint} became visible after {sw.ElapsedMilliseconds} ms.");
                return;
            }
            System.Threading.Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"WinFsp reported success, but Windows did not publish drive {MountPoint} within 5 seconds. " +
            $"See {AppLog.LogPath} for diagnostics.");
    }

    private bool MountPointReady()
    {
        try
        {
            var letter = char.ToUpperInvariant(Profile.DriveLetter.TrimEnd(':', '\\')[0]);
            return DriveInfo.GetDrives().Any(d =>
                d.Name.Length > 0 && char.ToUpperInvariant(d.Name[0]) == letter);
        }
        catch
        {
            return Directory.Exists(MountPoint + "\\");
        }
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
            {
                // adb might actually see the phone but not in a "ready" state (still
                // waiting on the Allow prompt, offline, or blocked by Windows) - say so
                // instead of the generic "not detected" message, which sends people
                // down the wrong troubleshooting path.
                throw new InvalidOperationException(AdbService.ExplainNoReadyDevice() ??
                    "No Android device detected. Connect your phone via USB, turn on " +
                    "\"USB debugging\" in Developer options, and tap \"Allow\" on the phone.");
            }

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

            VerifyNativeMountVisible();

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
            AppLog.Error("Mount:Android", $"Android mount failed for '{Profile.Name}' on {MountPoint}.", ex);
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

            VerifyNativeMountVisible();

            LastError = null;
            _shouldBeMounted = true;
            SetState(MountState.Mounted);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("Mount:Apple", $"Apple mount failed for '{Profile.Name}' on {MountPoint}.", ex);
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

            // Ask the SFTP server itself for filesystem capacity before WinFsp starts
            // serving requests. SSH.NET maps this to OpenSSH's statvfs extension, so
            // we get the real filesystem size without opening a second SSH shell or
            // running `df` (the old shell probe could fault during channel teardown).
            // Not every SFTP server implements statvfs, so capacity remains best-effort.
            try
            {
                var volume = _client.GetStatus(root);
                ulong blockSize = volume.BlockSize != 0
                    ? volume.BlockSize
                    : volume.FileSystemBlockSize;
                long totalBytes = BlocksToBytes(volume.TotalBlocks, blockSize);
                long freeBytes = BlocksToBytes(volume.AvailableBlocks, blockSize);

                if (totalBytes > 0)
                {
                    _fs.UpdateVolumeSpace(totalBytes, freeBytes);
                    AppLog.Info("Mount:SFTP",
                        $"Remote capacity for '{Profile.Name}': total={totalBytes} bytes, available={freeBytes} bytes, blockSize={blockSize}.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Mount:SFTP",
                    $"Server did not provide SFTP filesystem capacity for '{Profile.Name}'; using the fallback size. {ex.Message}");
            }

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

            VerifyNativeMountVisible();

            LastError = null;
            _shouldBeMounted = true;
            SetState(MountState.Mounted);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("Mount:SFTP", $"SFTP mount failed for '{Profile.Name}' on {MountPoint}.", ex);
            Cleanup();
            SetState(MountState.Error);
            return false;
        }
    }

    private static long BlocksToBytes(ulong blocks, ulong blockSize)
    {
        if (blocks == 0 || blockSize == 0) return 0;

        // WinFsp's volume fields are ulong, but SftpFileSystem stores values in
        // long so Interlocked can update them atomically. Saturate instead of
        // wrapping if a server reports a fantastically large filesystem.
        ulong max = (ulong)long.MaxValue;
        return blocks > max / blockSize
            ? long.MaxValue
            : (long)(blocks * blockSize);
    }

    public void Unmount()
    {
        lock (_sync)
        {
            _shouldBeMounted = false;   // stop the watchdog from remounting
            AppLog.Info("Mount", $"Unmount requested for '{Profile.Name}' on {MountPoint}.");
            Cleanup();
            SetState(MountState.Disconnected);
        }
    }

    private void Cleanup()
    {
        AppLog.Info("Mount", $"Cleaning up mount resources for '{Profile.Name}' on {MountPoint}.");
        // native SFTP / Android share the WinFsp host
        try { _host?.Unmount(); } catch { /* ignore */ }
        try { _client?.Disconnect(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _adb?.Dispose(); } catch { /* ignore */ }
        try { _afc?.Dispose(); } catch { /* ignore */ }
        _host = null;
        _client = null;
        _fs = null;
        _adb = null;
        _afc = null;
        // rclone-backed: killing rclone unmounts its drive (WinFsp tears down on exit)
        try { if (_rcloneProc is { HasExited: false }) _rcloneProc.Kill(); } catch { /* ignore */ }
        RcloneService.ForgetProcess(_rcloneProc);
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
        if (State != state)
            AppLog.Info("Mount", $"'{Profile.Name}' ({MountPoint}) state: {State} -> {state}.");
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
