// ============================================================================
//  ZFTP.Daemon — zftpd
//  ---------------------------------------------------------------------------
//  Headless equivalent of ZFTP.App's MainWindow lifecycle, minus WPF: load
//  saved drives, auto-mount the ones flagged for it, and keep them alive
//  (MountSession's own watchdog handles reconnects) until asked to stop.
//
//  Usage:
//    zftpd                 run as the background host: auto-mount + stay alive
//    zftpd list            print configured drives
//    zftpd status          best-effort check of which drives look mounted
//    zftpd mount <name>    mount one drive in the foreground until Ctrl+C
//    zftpd unmount <name>  force-unmount a drive's mount directory directly
//    zftpd update          check for (and optionally install) a newer ZFTP
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using ZFTP.Core;

return args switch
{
    ["list"] => ListProfiles(),
    ["status"] => PrintStatus(),
    ["mount", var name] => RunSingleMount(name),
    ["unmount", var name] => ForceUnmount(name),
    ["update"] => await CheckForUpdate(),
    _ => RunDaemon(),
};

static List<ConnectionProfile> LoadProfiles()
{
    ProfileStore.MigrateOldLocation();
    return ProfileStore.Load();
}

static ConnectionProfile? FindProfile(List<ConnectionProfile> profiles, string name) =>
    profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

static int ListProfiles()
{
    var profiles = LoadProfiles();
    if (profiles.Count == 0)
    {
        Console.WriteLine("No drives configured yet - add them the same way ZFTP.App does (edit ~/.local/share/ZFTP/drives.json, or run the Windows GUI once against a shared profile store).");
        return 0;
    }
    foreach (var p in profiles)
        Console.WriteLine($"{p.Name}\t{p.Provider}\t{(p.Enabled ? "enabled" : "disabled")}\t{(p.AutoMount ? "auto-mount" : "manual")}");
    return 0;
}

static int PrintStatus()
{
    foreach (var p in LoadProfiles())
    {
        var target = MountTarget.Resolve(p);
        Console.WriteLine($"{p.Name}\t{target}\t{(IsMounted(target) ? "mounted" : "not mounted")}");
    }
    return 0;
}

static int RunSingleMount(string name)
{
    var profiles = LoadProfiles();
    var profile = FindProfile(profiles, name);
    if (profile == null)
    {
        Console.Error.WriteLine($"No drive named \"{name}\". Run \"zftpd list\" to see configured drives.");
        return 1;
    }

    using var session = new MountSession(profile);
    Console.WriteLine($"Mounting \"{profile.Name}\"...");
    if (!session.Mount())
    {
        Console.Error.WriteLine($"Failed to mount \"{profile.Name}\": {session.LastError}");
        return 1;
    }

    Console.WriteLine($"Mounted at {session.MountPoint}. Press Ctrl+C to unmount and exit.");
    WaitForShutdownSignal().Wait();

    Console.WriteLine("Unmounting...");
    session.Unmount();
    ProfileStore.Save(profiles);   // persist any host key learned this run
    return 0;
}

static int ForceUnmount(string name)
{
    var profiles = LoadProfiles();
    var profile = FindProfile(profiles, name);
    if (profile == null)
    {
        Console.Error.WriteLine($"No drive named \"{name}\". Run \"zftpd list\" to see configured drives.");
        return 1;
    }

    // There's no live MountSession to ask here (this is a fresh process), so this
    // is a direct best-effort teardown of whatever's sitting at the resolved mount
    // path - useful for cleaning up after a daemon that already exited.
    var target = MountTarget.Resolve(profile);
    if (!TryForceUnmount(target))
    {
        Console.Error.WriteLine($"Couldn't unmount {target} - it may not be mounted, or fusermount/umount isn't installed.");
        return 1;
    }
    Console.WriteLine($"Unmounted {target}.");
    return 0;
}

static async Task<int> CheckForUpdate()
{
    var current = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    Console.WriteLine($"Current version: {current}");
    Console.WriteLine("Checking for updates...");

    UpdateResult result;
    try { result = await Updater.CheckAsync(current); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Update check failed: {ex.Message}");
        return 1;
    }

    switch (result.Status)
    {
        case UpdateCheckStatus.UpToDate:
            Console.WriteLine("zftpd is up to date.");
            return 0;

        case UpdateCheckStatus.CouldNotCheck:
            Console.Error.WriteLine("Couldn't find any update info (GitHub and the CDN were both unreachable, or no release exists yet).");
            return 1;

        case UpdateCheckStatus.UpdateAvailable:
            var info = result.Info!;
            Console.WriteLine($"ZFTP {info.Version} is available (from {info.Source}).");
            if (!string.IsNullOrWhiteSpace(info.Notes)) Console.WriteLine(info.Notes);

            if (string.IsNullOrEmpty(info.Url))
            {
                // Matches Updater.CheckAsync's OS-aware asset matching: a release
                // can exist without anything built for this OS yet (true for
                // Linux/macOS today - CI only publishes a Windows installer).
                Console.WriteLine();
                Console.WriteLine("No build published for this OS yet - check https://github.com/BallisticOK/ZFTP/releases");
                return 0;
            }

            Console.Write("Download and install it now? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                return 0;

            var path = await Updater.DownloadInstallerAsync(info.Url);
            if (path == null)
            {
                Console.Error.WriteLine("Download failed. Try again later.");
                return 1;
            }
            Console.WriteLine($"Downloaded to {path}.");
            Console.WriteLine("zftpd can't replace its own running binary while it's executing - " +
                               "stop the daemon, then move the downloaded file over the current " +
                               "install and re-run it (chmod +x it first on Linux/macOS).");
            return 0;

        default:
            return 1;
    }
}

static int RunDaemon()
{
    var settings = AppSettings.Load();
    var profiles = LoadProfiles();
    var sessions = new List<MountSession>();

    if (settings.AutoMountOnStart)
    {
        foreach (var profile in profiles)
        {
            if (!profile.Enabled || !profile.AutoMount) continue;
            if (!OperatingSystem.IsWindows() && profile.Provider is ProviderType.Android or ProviderType.IPhone)
            {
                Console.WriteLine($"Skipping \"{profile.Name}\": Android/iPhone drives need Windows for now.");
                continue;
            }

            var session = new MountSession(profile);
            Console.WriteLine($"Mounting \"{profile.Name}\"...");
            if (session.Mount())
            {
                Console.WriteLine($"Mounted \"{profile.Name}\" at {session.MountPoint}");
                sessions.Add(session);
            }
            else
            {
                Console.Error.WriteLine($"Failed to mount \"{profile.Name}\": {session.LastError}");
                session.Dispose();
            }
        }
    }

    Console.WriteLine(sessions.Count == 0
        ? "No drives auto-mounted. Use \"zftpd mount <name>\" to mount one manually. Running - Ctrl+C to exit."
        : $"{sessions.Count} drive(s) mounted. Running - Ctrl+C to unmount and exit.");

    WaitForShutdownSignal().Wait();

    Console.WriteLine("Shutting down, unmounting all drives...");
    foreach (var session in sessions)
    {
        try { session.Unmount(); } catch { /* best effort */ }
        session.Dispose();
    }
    ProfileStore.Save(profiles);   // persist any host keys learned this run
    return 0;
}

// ---- shutdown signal plumbing ---------------------------------------------

static Task WaitForShutdownSignal()
{
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };

    var regs = new List<PosixSignalRegistration>();
    void TryRegister(PosixSignal signal)
    {
        try
        {
            regs.Add(PosixSignalRegistration.Create(signal, ctx => { ctx.Cancel = true; tcs.TrySetResult(); }));
        }
        catch { /* not supported on this platform - Console.CancelKeyPress still covers Ctrl+C */ }
    }
    TryRegister(PosixSignal.SIGINT);
    TryRegister(PosixSignal.SIGTERM);

    return tcs.Task.ContinueWith(_ => { foreach (var r in regs) r.Dispose(); }, TaskScheduler.Default);
}

// ---- best-effort "is this actually mounted" check for `status` ------------

static bool IsMounted(string path)
{
    try
    {
        if (!Directory.Exists(path)) return false;
        var full = Path.GetFullPath(path).TrimEnd('/');

        if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var cols = line.Split(' ');
                if (cols.Length > 1 && cols[1].TrimEnd('/') == full) return true;
            }
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("mount") { UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p == null) return Directory.Exists(path);
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output.Split('\n').Any(l => l.Contains(" on " + full + " "));
        }

        // Windows: a mounted drive letter is just a directory that exists.
        return Directory.Exists(path);
    }
    catch { return false; }
}

static bool TryForceUnmount(string path)
{
    try
    {
        var psi = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("umount") { ArgumentList = { path } }
            : new ProcessStartInfo("fusermount") { ArgumentList = { "-u", path } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.WaitForExit(5000);
        return p.ExitCode == 0;
    }
    catch { return false; }
}
