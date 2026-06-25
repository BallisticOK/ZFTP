// ============================================================================
//  ZFTP - AdbService
//  ---------------------------------------------------------------------------
//  Drives the bundled adb.exe (Android Debug Bridge) for the Android provider.
//  This is the transport ZFTP uses to talk to a phone/tablet plugged in over
//  USB with "USB debugging" enabled - the same mechanism AndroidDrive uses.
//
//  No app is installed on the phone and root is NOT required: adb's shell runs
//  as the "shell" user, which can read and write /sdcard (shared storage).
//
//  adb.exe (plus its two USB DLLs) ships in the "tools" folder next to ZFTP.exe,
//  exactly like rclone.exe. See RcloneService for the sibling pattern.
// ============================================================================

using System.Diagnostics;
using System.IO;

namespace ZFTP.Core;

public static class AdbService
{
    /// <summary>Path to the bundled adb.exe (sits in a "tools" folder next to ZFTP.exe).</summary>
    public static string AdbPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "adb.exe");

    public static bool Available => File.Exists(AdbPath);

    // True once we've actually run an adb command this session (which starts the
    // background adb server). We use this so KillServer is a no-op - and never
    // spawns adb.exe - when no Android drive was ever touched.
    private static bool _serverTouched;

    /// <summary>One connected device, as reported by `adb devices`.</summary>
    public sealed record Device(string Serial, string Model)
    {
        /// <summary>What we show in the picker, e.g. "Pixel 7 (28301FDH2000XYZ)".</summary>
        public string Label => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} ({Serial})";
    }

    /// <summary>Make sure the adb background server is running (first call can be slow).</summary>
    public static void EnsureServer()
    {
        try { Run(new[] { "start-server" }, out _, TimeSpan.FromSeconds(20)); }
        catch { /* best effort - ListDevices starts it too */ }
    }

    /// <summary>
    /// Stop the background adb server, so it stops holding tools\adb.exe open
    /// (otherwise an in-place update can't replace the file). No-op - and never
    /// launches adb.exe - if we never used adb this session.
    /// </summary>
    public static void KillServer()
    {
        if (!_serverTouched) return;
        try { Run(new[] { "kill-server" }, out _, TimeSpan.FromSeconds(10)); }
        catch { /* best effort */ }
        _serverTouched = false;
    }

    /// <summary>Serials of every device currently in the "device" (ready) state.</summary>
    public static string[] ListDeviceSerials()
    {
        if (!Run(new[] { "devices" }, out var output, TimeSpan.FromSeconds(15)))
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;
            // "SERIAL\tdevice"  (other states: offline / unauthorized / no permissions)
            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase))
                list.Add(parts[0]);
        }
        return list.ToArray();
    }

    /// <summary>Connected, ready devices with a friendly model name for the picker.</summary>
    public static List<Device> ListDevices()
    {
        var result = new List<Device>();
        foreach (var serial in ListDeviceSerials())
            result.Add(new Device(serial, GetModel(serial)));
        return result;
    }

    /// <summary>True if this serial is currently plugged in and ready.</summary>
    public static bool DeviceConnected(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        return Array.Exists(ListDeviceSerials(), s => s.Equals(serial, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The device's marketing model (ro.product.model), or "" if unavailable.</summary>
    public static string GetModel(string serial)
    {
        try
        {
            if (Run(new[] { "-s", serial, "shell", "getprop", "ro.product.model" },
                    out var output, TimeSpan.FromSeconds(8)))
                return output.Trim();
        }
        catch { /* fall through */ }
        return "";
    }

    // ---- shell / pull / push ----------------------------------------------

    /// <summary>
    /// Run a shell command on the device and return its stdout. Uses `exec-out`
    /// so output isn't mangled by the adb pseudo-terminal CR/LF translation
    /// (important for parsing `ls`). The command is sent verbatim to the device
    /// shell, so callers must quote any paths themselves (see ShellQuote).
    /// </summary>
    public static bool Shell(string serial, string command, out string output, TimeSpan timeout)
        => Run(new[] { "-s", serial, "exec-out", command }, out output, timeout);

    /// <summary>Copy a file off the device to a local path. Returns true on success.</summary>
    public static bool Pull(string serial, string remotePath, string localPath)
        => Run(new[] { "-s", serial, "pull", remotePath, localPath }, out _, TimeSpan.FromHours(6));

    /// <summary>Copy a local file onto the device. Returns true on success.</summary>
    public static bool Push(string serial, string localPath, string remotePath)
        => Run(new[] { "-s", serial, "push", localPath, remotePath }, out _, TimeSpan.FromHours(6));

    /// <summary>Single-quote a path for the device's POSIX shell (handles embedded quotes).</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ---- disk space --------------------------------------------------------

    /// <summary>Best-effort real size/free of the device storage via `df`.</summary>
    public static bool TryGetDiskSpace(string serial, string path, out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;
        try
        {
            if (!Shell(serial, "df -Pk " + ShellQuote(path), out var output, TimeSpan.FromSeconds(10)))
                return false;

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
        }
        catch { /* ignore */ }
        return false;
    }

    // ---- process helpers ---------------------------------------------------

    private static ProcessStartInfo Psi(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static bool Run(IEnumerable<string> args, out string output, TimeSpan timeout)
    {
        output = "";
        if (!Available) return false;

        // Any adb invocation may auto-start the background server; remember that so
        // KillServer knows there's something to stop on exit.
        _serverTouched = true;

        using var p = new Process { StartInfo = Psi(args) };
        try { p.Start(); }
        catch { return false; }

        // Read stdout async while we wait, so a large `ls`/`getprop` can't deadlock
        // by filling the pipe buffer before we read it.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(); } catch { }
            return false;
        }

        output = stdout.GetAwaiter().GetResult();
        _ = stderr.GetAwaiter().GetResult();
        return p.ExitCode == 0;
    }
}
