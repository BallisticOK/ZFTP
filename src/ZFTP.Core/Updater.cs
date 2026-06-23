// ============================================================================
//  ZFTP — Updater
//  ---------------------------------------------------------------------------
//  Checks for a newer ZFTP from TWO sources and uses whichever is newest:
//
//   1) GitHub Releases  (https://github.com/BallisticOK/ZFTP)
//        - latest release tag = version; the .exe release asset = installer
//   2) The ZFTP CDN     (https://cdn.ballisticok.xyz/files/zftp/)
//        - version.txt (line 1 = version, lines 2+ = notes)
//        - ZFTP-Setup-<version>.exe = installer
//
//  Either source can be unreachable; we just use the other. If neither yields
//  a version, the check reports "couldn't check".
// ============================================================================

using System.Net.Http;
using System.Text.Json;

namespace ZFTP.Core;

public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "";   // "GitHub" or "CDN"
}

public enum UpdateCheckStatus { UpToDate, UpdateAvailable, CouldNotCheck }

public sealed class UpdateResult
{
    public UpdateCheckStatus Status { get; init; }
    public UpdateInfo? Info { get; init; }
}

public static class Updater
{
    public const string BaseUrl = "https://cdn.ballisticok.xyz/files/zftp/";
    public const string GitHubRepo = "BallisticOK/ZFTP";

    public static string VersionUrl => BaseUrl + "version.txt";
    public static string GitHubLatestUrl => $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
    public static string InstallerUrl(string version) => BaseUrl + $"ZFTP-Setup-{version}.exe";

    public static async Task<UpdateResult> CheckAsync(string currentVersion)
    {
        var candidates = new List<UpdateInfo>();

        var gh = await TryGitHubAsync();
        if (gh != null) candidates.Add(gh);

        var cdn = await TryCdnAsync();
        if (cdn != null) candidates.Add(cdn);

        if (candidates.Count == 0)
            return new UpdateResult { Status = UpdateCheckStatus.CouldNotCheck };

        // Highest version wins (GitHub and CDN may both have one).
        var best = candidates.OrderByDescending(c => SafeVer(c.Version)).First();

        return IsNewer(best.Version, currentVersion)
            ? new UpdateResult { Status = UpdateCheckStatus.UpdateAvailable, Info = best }
            : new UpdateResult { Status = UpdateCheckStatus.UpToDate };
    }

    // ---- GitHub Releases ---------------------------------------------------

    private static async Task<UpdateInfo?> TryGitHubAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "ZFTP-Updater");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var resp = await http.GetAsync(GitHubLatestUrl);
            if (!resp.IsSuccessStatusCode) return null;   // 404 = no releases yet

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var version = Clean(tagEl.GetString() ?? "");
            if (!Version.TryParse(version, out _)) return null;

            // Find the .exe installer asset.
            string? url = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(url)) break;
                    }
                }
            }
            if (string.IsNullOrEmpty(url)) return null;   // release has no installer attached

            var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            return new UpdateInfo { Version = version, Url = url, Notes = notes.Trim(), Source = "GitHub" };
        }
        catch
        {
            return null;
        }
    }

    // ---- CDN version.txt ---------------------------------------------------

    private static async Task<UpdateInfo?> TryCdnAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "ZFTP-Updater");
            return Parse(await http.GetStringAsync(VersionUrl));
        }
        catch
        {
            return null;
        }
    }

    public static UpdateInfo? Parse(string text)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).ToArray();
        var version = lines.FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(version) || !Version.TryParse(Clean(version), out _)) return null;

        int idx = Array.IndexOf(lines, version);
        var notes = string.Join("\n", lines.Skip(idx + 1)).Trim();
        var v = Clean(version);
        return new UpdateInfo { Version = v, Url = InstallerUrl(v), Notes = notes, Source = "CDN" };
    }

    // ---- download ----------------------------------------------------------

    public static async Task<string?> DownloadInstallerAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Add("User-Agent", "ZFTP-Updater");
            var bytes = await http.GetByteArrayAsync(url);
            var path = Path.Combine(Path.GetTempPath(), "ZFTP-Setup-update.exe");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ---- helpers -----------------------------------------------------------

    public static bool IsNewer(string candidate, string current) => SafeVer(candidate) > SafeVer(current);

    private static Version SafeVer(string v) =>
        Version.TryParse(Clean(v), out var r) ? r : new Version(0, 0);

    private static string Clean(string v) =>
        new string((v ?? "").Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
}
