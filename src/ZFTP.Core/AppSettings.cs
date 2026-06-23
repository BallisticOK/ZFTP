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

    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    // ---- persistence -------------------------------------------------------

    private static string FilePath { get; } = Path.Combine(ProfileStore.FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ProfileStore.FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }
}
