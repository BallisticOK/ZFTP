// ============================================================================
//  ZFTP — AppSettings
//  Global app preferences (not per-drive). Saved as JSON next to drives.json:
//      %AppData%\ZFTP\settings.json
// ============================================================================

using System.IO;
using System.Text.Json;

namespace ZFTP.Core;

public sealed class AppSettings
{
    /// <summary>Launch ZFTP automatically when you log into Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Open hidden in the system tray instead of showing the window.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>On launch, mount every enabled drive that has Auto-mount turned on.</summary>
    public bool AutoMountOnStart { get; set; } = true;

    /// <summary>Closing the window hides to the tray (true) or fully exits (false).</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Stable theme id (older builds may contain the display name).</summary>
    public string Theme { get; set; } = "dark-blue";

    // ---- persistence -------------------------------------------------------

    private static string FilePath { get; } = Path.Combine(ProfileStore.FolderPath, "settings.json");

    public static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch { /* try the backup */ }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }
}
