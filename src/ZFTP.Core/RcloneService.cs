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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZFTP.Core;

public static class RcloneService
{
    private static readonly string RcloneExeName = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

    /// <summary>Path to rclone: the bundled copy in a "tools" folder next to ZFTP.exe
    /// if present (how Windows ships it today), otherwise whatever "rclone" resolves
    /// to on PATH (how it's expected to be installed on Linux/macOS for now).</summary>
    public static string RclonePath => ToolResolver.Resolve(RcloneExeName);

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
        // Only reached on non-Windows - see MountSession.MountRcloneSftp(). Windows
        // still mounts Sftp through the native WinFsp+SSH.NET engine.
        ProviderType.Sftp => "sftp",
        ProviderType.Ftp or ProviderType.Ftps => "ftp",
        ProviderType.WebDav => "webdav",
        ProviderType.S3 => "s3",
        ProviderType.GoogleDrive => "drive",
        ProviderType.Dropbox => "dropbox",
        ProviderType.OneDrive => "onedrive",
        ProviderType.Box => "box",
        ProviderType.Smb => "smb",
        ProviderType.B2 => "b2",
        ProviderType.Azure => "azureblob",
        ProviderType.Mega => "mega",
        ProviderType.Proton => "protondrive",
        _ => "ftp",
    };

    /// <summary>Backends whose path is rooted at a bucket/share/container rather than
    /// the remote itself (like S3). Maps a profile to that top-level name.</summary>
    private static string? BucketLike(ConnectionProfile p) => p.Provider switch
    {
        ProviderType.S3 => p.S3Bucket,
        ProviderType.Smb => p.SmbShare,
        ProviderType.B2 => p.B2Bucket,
        ProviderType.Azure => p.AzureContainer,
        _ => null,
    };

    /// <summary>The "remote:path" rclone should mount for this profile.</summary>
    public static string RemotePath(ConnectionProfile p)
    {
        var root = (p.RemoteRoot ?? "").Trim().TrimStart('/');
        var bucket = BucketLike(p);
        if (bucket != null)
        {
            var top = bucket.Trim().Trim('/');
            var sub = string.IsNullOrEmpty(root) ? top : $"{top}/{root}";
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
            case ProviderType.Sftp:
                args.AddRange(new[] { "host", p.Host, "user", p.Username });
                if (p.Port > 0) args.AddRange(new[] { "port", p.Port.ToString() });
                if (p.Auth == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(p.KeyPath))
                {
                    args.AddRange(new[] { "key_file", p.KeyPath });
                    if (!string.IsNullOrEmpty(p.KeyPassphrase)) args.AddRange(new[] { "key_file_pass", p.KeyPassphrase });
                }
                else
                {
                    args.AddRange(new[] { "pass", p.Password });
                }
                break;

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

            case ProviderType.Smb:
                args.AddRange(new[] { "host", p.Host, "user", p.Username, "pass", p.Password });
                if (p.Port > 0) args.AddRange(new[] { "port", p.Port.ToString() });
                if (!string.IsNullOrWhiteSpace(p.SmbDomain)) args.AddRange(new[] { "domain", p.SmbDomain });
                break;

            case ProviderType.B2:
                args.AddRange(new[] { "account", p.B2AccountId, "key", p.B2ApplicationKey });
                break;

            case ProviderType.Azure:
                args.AddRange(new[] { "account", p.AzureAccount, "key", p.AzureKey });
                break;

            case ProviderType.Mega:
                args.AddRange(new[] { "user", p.Username, "pass", p.Password });
                break;

            case ProviderType.Proton:
                args.AddRange(new[] { "username", p.Username, "password", p.Password });
                if (!string.IsNullOrWhiteSpace(p.ProtonTwoFactorCode)) args.AddRange(new[] { "2fa", p.ProtonTwoFactorCode.Trim() });
                if (!string.IsNullOrWhiteSpace(p.ProtonMailboxPassword)) args.AddRange(new[] { "mailbox_password", p.ProtonMailboxPassword });
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

    // ---- OneDrive: drive picker --------------------------------------------
    //
    // OneDrive needs its own flow because rclone's normal interactive wizard
    // asks a "which drive?" question that Microsoft can answer with MULTIPLE
    // candidates for a single account - not just "Personal vs Business", but
    // also hidden system drives (metadata archives, "Bundles", etc.) alongside
    // the real one. Driven non-interactively (as StartOAuthSetup does), rclone
    // silently accepts the FIRST candidate, which is often one of those hidden
    // drives - the mount then fails with a cryptic Graph API error. So instead
    // we drive rclone's "--non-interactive --continue" config protocol
    // ourselves, stopping to let the user pick the real drive from the list.

    private sealed record WizardExample(
        [property: JsonPropertyName("Value")] string Value,
        [property: JsonPropertyName("Help")] string Help);

    private sealed record WizardOption(
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("Examples")] List<WizardExample>? Examples);

    private sealed record WizardResponse(
        [property: JsonPropertyName("State")] string? State,
        [property: JsonPropertyName("Option")] WizardOption? Option,
        [property: JsonPropertyName("Error")] string? Error);

    private static readonly JsonSerializerOptions WizardJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static WizardResponse? RunWizardStep(IEnumerable<string> args)
    {
        Run(args, out var raw, TimeSpan.FromSeconds(30));
        try { return JsonSerializer.Deserialize<WizardResponse>(raw, WizardJsonOpts); }
        catch { return null; }
    }

    private static WizardResponse? ContinueWizard(string name, string state, string result) =>
        RunWizardStep(new[]
        {
            "config", "create", name, "onedrive", "--non-interactive", "--continue",
            "--state", state, "--result", result, "--config", ConfigPath,
        });

    /// <summary>One drive Microsoft offered for this account.</summary>
    public sealed record OnedriveDriveOption(string Value, string Label);

    public sealed class OnedriveWizardResult
    {
        public bool Done { get; init; }
        public string? Error { get; init; }
        public List<OnedriveDriveOption>? Choices { get; init; }   // set when the user must pick
        public string? ResumeState { get; init; }                  // pass back into FinishOnedriveWizard
    }

    /// <summary>
    /// Run rclone's own OAuth flow decoupled from config (so a broken drive pick
    /// can never poison the token), then return the raw token JSON blob. Opens
    /// the browser itself, same as before. Null on failure.
    /// <paramref name="onStatus"/>, if given, is called with the fallback sign-in
    /// URL as soon as rclone prints it - the caller's only way to show it if the
    /// browser doesn't open automatically, since we otherwise run this hidden.
    /// </summary>
    public static async Task<string?> AuthorizeOnedriveAsync(ConnectionProfile p, Action<string>? onStatus = null)
    {
        var args = new List<string> { "authorize", "onedrive" };
        if (!string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.ClientSecret))
            args.AddRange(new[] { p.ClientId.Trim(), p.ClientSecret.Trim() });

        using var proc = new Process { StartInfo = Psi(args, hidden: true) };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                var m = Regex.Match(line, @"https?://\S+");
                if (m.Success) onStatus?.Invoke(m.Value);
            }
        });
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        await stderrTask;

        var blob = Regex.Match(stdout, @"\{.*\}");
        return blob.Success ? blob.Value : null;
    }

    /// <summary>
    /// Start the OneDrive config wizard with a fresh token and answer the two
    /// deterministic questions ourselves (don't refresh a token we just got;
    /// use the plain "onedrive" account type, not a specific Sharepoint site).
    /// Returns the real list of drives Microsoft has for this account so the
    /// caller can show it to the user - see the class comment for why this
    /// can't just be auto-picked.
    /// </summary>
    public static OnedriveWizardResult BeginOnedriveWizard(ConnectionProfile p, string tokenJson)
    {
        var name = RemoteName(p);
        var args = new List<string> { "config", "create", name, "onedrive", "token", tokenJson };
        if (!string.IsNullOrWhiteSpace(p.ClientId)) args.AddRange(new[] { "client_id", p.ClientId.Trim() });
        if (!string.IsNullOrWhiteSpace(p.ClientSecret)) args.AddRange(new[] { "client_secret", p.ClientSecret.Trim() });
        args.AddRange(new[] { "--non-interactive", "--config", ConfigPath });

        var r1 = RunWizardStep(args);
        if (r1 == null) return new() { Error = "Couldn't start the OneDrive setup wizard." };
        if (!string.IsNullOrEmpty(r1.Error)) return new() { Error = r1.Error };
        if (r1.Option?.Name != "config_refresh_token" || r1.State == null)
            return new() { Error = "OneDrive setup asked an unexpected question (rclone may have changed) - please report this." };

        var r2 = ContinueWizard(name, r1.State, "false");
        if (r2 == null) return new() { Error = "OneDrive setup failed." };
        if (!string.IsNullOrEmpty(r2.Error)) return new() { Error = r2.Error };
        if (r2.Option?.Name != "config_type" || r2.State == null)
            return new() { Error = "OneDrive setup asked an unexpected question (rclone may have changed) - please report this." };

        var r3 = ContinueWizard(name, r2.State, "onedrive");
        if (r3 == null) return new() { Error = "OneDrive setup failed." };
        if (!string.IsNullOrEmpty(r3.Error)) return new() { Error = r3.Error };

        var examples = r3.Option?.Examples ?? new List<WizardExample>();
        if (examples.Count == 0 || r3.State == null)
            return new() { Error = "Microsoft didn't return any usable OneDrive for this account." };

        return new OnedriveWizardResult
        {
            Choices = examples.Select(e => new OnedriveDriveOption(e.Value, e.Help)).ToList(),
            ResumeState = r3.State,
        };
    }

    /// <summary>Finish the wizard once the user has picked which drive to use.</summary>
    public static OnedriveWizardResult FinishOnedriveWizard(ConnectionProfile p, string resumeState, string chosenDriveId)
    {
        var name = RemoteName(p);
        var r4 = ContinueWizard(name, resumeState, chosenDriveId);
        if (r4 == null) return new() { Error = "OneDrive setup failed." };
        if (!string.IsNullOrEmpty(r4.Error)) return new() { Error = r4.Error };
        if (r4.State == null) return new() { Done = true };   // some accounts skip the confirmation step

        // Final "Drive OK?" confirmation - the user already made the real choice
        // by picking from the list, so accept it without asking a second time.
        var r5 = ContinueWizard(name, r4.State, "true");
        if (r5 == null) return new() { Error = "OneDrive setup failed." };
        if (!string.IsNullOrEmpty(r5.Error)) return new() { Error = r5.Error };
        return new OnedriveWizardResult { Done = string.IsNullOrEmpty(r5.State) };
    }

    /// <summary>
    /// Best-effort guess at which drive is the "real" one to default the picker
    /// to - Microsoft's list mixes the actual drive in with hidden system ones
    /// (metadata archives, "Bundles_...", drives named only by a raw GUID).
    /// Still just a default; the user can always pick a different one.
    /// </summary>
    public static int GuessDefaultOnedriveDrive(List<OnedriveDriveOption> choices)
    {
        for (int i = 0; i < choices.Count; i++)
        {
            var label = choices[i].Label.Split(" (")[0];
            if (label.Contains("Metadata", StringComparison.OrdinalIgnoreCase)) continue;
            if (label.Contains("Bundles", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(label, @"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")) continue;
            if (Regex.IsMatch(label, @"^[0-9A-Fa-f]{16,}$")) continue;
            return i;
        }
        return 0;
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
