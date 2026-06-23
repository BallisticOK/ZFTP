// ============================================================================
//  ZFTP — DriveIconManager
//  Gives ZFTP-mounted drives a custom icon (the ZFTP logo) in This PC, instead
//  of the generic drive icon. Uses the per-user DriveIcons registry key, so no
//  admin is needed. The icon is set when a drive mounts and removed when it
//  unmounts (so the letter doesn't keep our icon if something else uses it).
// ============================================================================

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZFTP.Core;

public static class DriveIconManager
{
    private const string Base =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    /// <summary>iconResource is like "C:\Path\ZFTP.exe,0".</summary>
    public static void SetIcon(string driveLetter, string iconResource)
    {
        var letter = Letter(driveLetter);
        if (letter == null) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"{Base}\{letter}\DefaultIcon");
            k?.SetValue(null, iconResource, RegistryValueKind.ExpandString);
        }
        catch { /* ignore */ }
    }

    public static void ClearIcon(string driveLetter)
    {
        var letter = Letter(driveLetter);
        if (letter == null) return;
        try { Registry.CurrentUser.DeleteSubKeyTree($@"{Base}\{letter}", throwOnMissingSubKey: false); }
        catch { /* ignore */ }
    }

    private static string? Letter(string drive)
    {
        var l = (drive ?? "").TrimEnd(':', '\\');
        return l.Length == 1 ? l.ToUpperInvariant() : null;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);

    /// <summary>Nudge Explorer so icon changes show immediately.</summary>
    public static void Refresh() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x1000 /*SHCNF_FLUSH*/, IntPtr.Zero, IntPtr.Zero);
}
