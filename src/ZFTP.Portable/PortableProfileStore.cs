using System.Text.Json;

namespace ZFTP.Portable;

public static class PortableProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FolderPath { get; } = ResolveFolder();
    public static string FilePath { get; } = Path.Combine(FolderPath, "portable-drives.json");

    public static List<PortableProfile> Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                return JsonSerializer.Deserialize<List<PortableProfile>>(File.ReadAllText(path), JsonOptions) ?? [];
            }
            catch
            {
                // Try the backup before returning an empty list.
            }
        }

        return [];
    }

    public static void Save(IEnumerable<PortableProfile> profiles)
    {
        Directory.CreateDirectory(FolderPath);
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        var temp = FilePath + ".tmp";
        var backup = FilePath + ".bak";
        File.WriteAllText(temp, json);

        if (File.Exists(FilePath))
            File.Copy(FilePath, backup, overwrite: true);

        File.Move(temp, FilePath, overwrite: true);
    }

    public static string DefaultMountPath(string name)
    {
        var safe = string.Concat((name ?? "Drive").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "Drive";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZFTP", safe);
    }

    private static string ResolveFolder()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ZFTP");

        if (OperatingSystem.IsLinux())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "zftp");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "zftp");
        }

        // Deliberately separate from the existing Windows GUI's drives.json.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZFTP", "portable");
    }
}
