// ============================================================================
//  ZFTP — ToolResolver
//  ---------------------------------------------------------------------------
//  Finds a bundled command-line tool (rclone, adb). Windows ships them in a
//  "tools" folder next to ZFTP.exe; Linux/macOS builds don't bundle binaries
//  yet, so there we fall back to whatever's on PATH (e.g. distro-packaged
//  rclone/adb, or Homebrew's).
// ============================================================================

namespace ZFTP.Core;

internal static class ToolResolver
{
    /// <summary>Resolve <paramref name="exeName"/>: the bundled "tools/&lt;exeName&gt;"
    /// next to the app if it exists, otherwise the first match on PATH.</summary>
    public static string Resolve(string exeName)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (File.Exists(bundled)) return bundled;

        var fromPath = FindOnPath(exeName);
        return fromPath ?? bundled;   // keep the bundled path as the reported "expected" location
    }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry - skip it */ }
        }
        return null;
    }
}
