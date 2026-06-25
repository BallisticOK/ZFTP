// ============================================================================
//  ZFTP — RcloneService
//  ---------------------------------------------------------------------------
//  Drives the bundled rclone.exe for every non-SFTP provider. For each saved
//  drive we create an rclone "remote" (stored in ZFTP's own rclone.conf), then
//  mount it to a drive letter with `rclone mount` (which uses WinFsp, same as
//  our native engine).
//
//  Credential backends (FTP/FTPS/WebDAV/S3) are configured non-interactively.
//  OAuth backends (Google Drive/Dropbox/OneDrive/Box) use rclone's own browser
//  sign-in via `rclone config create`, so we never have to register apps.
// ============================================================================

using System.Diagnostics;
using System.IO;

namespace ZFTP.Core;

public static class RcloneService
{
    /// <summary>Path to the bundled rclone.exe (sits in a "tools" folder next to ZFTP.exe).</summary>
    public static string RclonePath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "rclone.exe");

    /// <summary>ZFTP's private rclone config file.</summary>
    public static string ConfigPath =>
        Path.Combine(ProfileStore.FolderPath, "rclone.conf");

    public static bool Available => File.Exists(RclonePath);

    /// <summary>The rclone remote name we use for a profile (its stable id).</summary>
    public static string RemoteName(ConnectionProfile p) => "zftp_" + p.Id;

    public static bool IsRcloneProvider(ProviderType t) => t != ProviderType.Sftp;

    public static bool RequiresOAuth(ProviderType t) =>
        t is ProviderType.GoogleDrive or ProviderType.Dropbox or ProviderType.OneDrive or ProviderType.Box;

    private static string RcloneType(ProviderType t) => t switch
    {
        ProviderType.Ftp or ProviderType.Ftps => "ftp",
        ProviderType.WebDav => "webdav",
        ProviderType.S3 => "s3",
        ProviderType.GoogleDrive => "drive",
        ProviderType.Dropbox => "dropbox",
        ProviderType.OneDrive => "onedrive",
        ProviderType.Box => "box",
        _ => "ftp",
    };

    /// <summary>The "remote:path" rclone should mount for this profile.</summary>
    public static string RemotePath(ConnectionProfile p)
    {
        var root = (p.RemoteRoot ?? "").Trim().TrimStart('/');
        if (p.Provider == ProviderType.S3)
        {
            var bucket = (p.S3Bucket ?? "").Trim().Trim('/');
            var sub = string.IsNullOrEmpty(root) ? bucket : $"{bucket}/{root}";
            return $"{RemoteName(p)}:{sub}";
        }
        return $"{RemoteName(p)}:{root}";
    }

    // ---- config ------------------------------------------------------------

    /// <summary>
    /// Create/replace the rclone remote for a credential backend (no OAuth).
    /// Returns true on success.
    /// </summary>
    public static bool CreateCredentialRemote(ConnectionProfile p)
    {
        var name = RemoteName(p);
        var args = new List<string> { "config", "create", name, RcloneType(p.Provider) };

        switch (p.Provider)
        {
            case ProviderType.Ftp:
            case ProviderType.Ftps:
                args.AddRange(new[] { "host", p.Host, "user", p.Username, "pass", p.Password });
                if (p.Port > 0) args.AddRange(new[] { "port", p.Port.ToString() });
                if (p.Provider == ProviderType.Ftps) args.AddRange(new[] { "explicit_tls", "true" });
                break;

            case ProviderType.WebDav:
                args.AddRange(new[] { "url", p.Url, "vendor", "other", "user", p.Username, "pass", p.Password });
                break;

            case ProviderType.S3:
                args.AddRange(new[]
                {
                    "provider", string.IsNullOrWhiteSpace(p.S3Endpoint) ? "AWS" : "Other",
                    "access_key_id", p.S3AccessKey,
                    "secret_access_key", p.S3Secret,
                });
                if (!string.IsNullOrWhiteSpace(p.S3Region)) args.AddRange(new[] { "region", p.S3Region });
                if (!string.IsNullOrWhiteSpace(p.S3Endpoint)) args.AddRange(new[] { "endpoint", p.S3Endpoint });
                break;
        }

        args.AddRange(new[] { "--config", ConfigPath, "--obscure", "--non-interactive" });
        return Run(args, out _, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Start an OAuth sign-in for a cloud backend. rclone opens the browser; the
    /// returned process completes once the user approves. (For Drive/Dropbox/etc.)
    /// </summary>
    public static Process StartOAuthSetup(ConnectionProfile p)
    {
        var name = RemoteName(p);
        var args = new List<string> { "config", "create", name, RcloneType(p.Provider) };
        // A personal OAuth app (optional) makes the sign-in permanent.
        if (!string.IsNullOrWhiteSpace(p.ClientId))
            args.AddRange(new[] { "client_id", p.ClientId.Trim() });
        if (!string.IsNullOrWhiteSpace(p.ClientSecret))
            args.AddRange(new[] { "client_secret", p.ClientSecret.Trim() });
        args.AddRange(new[] { "--config", ConfigPath });
        return Start(args, hidden: false);   // visible so rclone's browser flow can run
    }

    /// <summary>True if this profile's cloud remote is already authorized (token saved).</summary>
    public static bool IsSignedIn(ConnectionProfile p)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var section = "[" + RemoteName(p) + "]";
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(ConfigPath))
            {
                var t = raw.Trim();
                if (t.StartsWith("["))
                    inSection = t.Equals(section, StringComparison.OrdinalIgnoreCase);
                else if (inSection && t.StartsWith("token", StringComparison.OrdinalIgnoreCase) && t.Contains('='))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Ask the cloud who's signed in (e.g. the Google account email). Null if unknown.</summary>
    public static async Task<string?> GetAccountAsync(ConnectionProfile p)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!Run(new[] { "config", "userinfo", RemoteName(p) + ":", "--config", ConfigPath },
                        out var output, TimeSpan.FromSeconds(20)))
                    return null;

                // Prefer an email address if present.
                var email = System.Text.RegularExpressions.Regex.Match(output, @"[\w.+-]+@[\w-]+\.[\w.-]+");
                if (email.Success) return email.Value;

                // Otherwise the first "key: value" value (display name etc.).
                foreach (var line in output.Split('\n'))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && i < line.Length - 1)
                    {
                        var v = line[(i + 1)..].Trim();
                        if (v.Length > 0) return v;
                    }
                }
                return null;
            }
            catch { return null; }
        });
    }

    // ---- mount / unmount ---------------------------------------------------

    /// <summary>
    /// Backends that can push change notifications to rclone (ChangeNotify). For
    /// these we can cache directory listings for a very long time and still stay
    /// fresh, because rclone learns about remote changes via polling. The others
    /// (FTP/FTPS/WebDAV/S3) can't, so we use a shorter dir cache for them.
    /// </summary>
    private static bool SupportsPolling(ProviderType t) =>
        t is ProviderType.GoogleDrive or ProviderType.Dropbox
          or ProviderType.OneDrive or ProviderType.Box;

    /// <summary>Mount the profile's remote to its drive letter. Returns the rclone process.</summary>
    public static Process Mount(ConnectionProfile p, string mountPoint)
    {
        bool poll = SupportsPolling(p.Provider);

        var args = new List<string>
        {
            "mount", RemotePath(p), mountPoint,
            "--config", ConfigPath,
            "--volname", SafeVolName(p.Name),
            // No --network-mode → mounts as a regular LOCAL drive under
            // "Devices and drives" in This PC (no Network-location ghosts).

            // ---- caching: this is what makes browsing feel instant ----
            // 'full' caches reads on disk too (not just writes), so re-opening a
            // file or seeking around it doesn't re-download. Bounded below.
            "--vfs-cache-mode", "full",
            "--vfs-cache-max-age", "12h",
            "--vfs-cache-max-size", "10G",
            "--vfs-cache-poll-interval", "1m",
            "--vfs-fast-fingerprint",         // cheaper cache validation

            // Read in chunks and read ahead so OPENING a big file starts streaming
            // straight away instead of stalling while the whole thing downloads.
            // This is the difference between "click file → opens" and "click file
            // → Explorer freezes for ages".
            "--vfs-read-chunk-size", "32M",
            "--vfs-read-chunk-size-limit", "1G",
            "--vfs-read-ahead", "128M",
            "--buffer-size", "32M",           // per-open in-memory read buffer

            // How long a folder listing is trusted before rclone re-fetches it.
            // For polling backends we trust it for a long time (changes arrive via
            // --poll-interval); for the rest we use a modest window.
            "--dir-cache-time", poll ? "1000h" : "30s",

            // How long WinFsp itself caches file attributes, so Explorer stat-ing
            // a folder full of files doesn't hammer the backend. (Default is 1s.)
            "--attr-timeout", "8s",

            // More parallelism for copies, metadata checks, and the warm-up below.
            "--transfers", "8",
            "--checkers", "32",

            "--no-console",
        };

        if (poll)
        {
            args.AddRange(new[] { "--poll-interval", "15s" });   // pick up remote changes

            // THE big responsiveness fix for cloud drives. Without this, the FIRST
            // time you open any folder, Explorer's UI thread blocks while rclone
            // makes a slow API call to list it — that's the "(Not Responding) …
            // then it suddenly fills in" you see. --vfs-refresh walks the whole
            // tree in the BACKGROUND right after mounting and parks it in the dir
            // cache (kept fresh by polling above), so by the time you click around,
            // listings are served instantly from memory instead of over the wire.
            args.Add("--vfs-refresh");
        }
        else
        {
            args.AddRange(new[] { "--poll-interval", "0" });     // backend can't poll; don't waste calls
        }

        // Google Drive throttles aggressively by default; loosen the pacer and use
        // bigger chunks so listing and transferring large folders isn't crawling.
        if (p.Provider == ProviderType.GoogleDrive)
            args.AddRange(new[]
            {
                "--drive-pacer-min-sleep", "10ms",
                "--drive-pacer-burst", "200",
                "--drive-chunk-size", "64M",
            });

        if (p.Access == AccessMode.ReadOnly) args.Add("--read-only");
        return Start(args, hidden: true);
    }

    // ---- process helpers ---------------------------------------------------

    private static ProcessStartInfo Psi(IEnumerable<string> args, bool hidden)
    {
        var psi = new ProcessStartInfo(RclonePath)
        {
            UseShellExecute = false,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            RedirectStandardError = hidden,
            RedirectStandardOutput = hidden,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Directory.CreateDirectory(ProfileStore.FolderPath);
        return psi;
    }

    private static Process Start(IEnumerable<string> args, bool hidden)
    {
        var p = new Process { StartInfo = Psi(args, hidden) };
        p.Start();
        return p;
    }

    private static bool Run(IEnumerable<string> args, out string output, TimeSpan timeout)
    {
        using var p = new Process { StartInfo = Psi(args, hidden: true) };
        p.Start();
        output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(); } catch { } return false; }
        return p.ExitCode == 0;
    }

    /// <summary>The volume name rclone uses (also the share part of its \\server\&lt;name&gt; UNC).</summary>
    public static string VolName(string name)
    {
        var safe = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "ZFTP" : safe;
    }

    private static string SafeVolName(string name) => VolName(name);
}
