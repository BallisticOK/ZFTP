// ============================================================================
//  ZFTP — NetworkDriveCleanup
//  ---------------------------------------------------------------------------
//  ZFTP drives mount as NETWORK drives so they appear under "Network locations"
//  in This PC. Windows caches those under MountPoints2 and leaves greyed-out
//  "disconnected" entries when a drive isn't mounted. This removes the cached
//  entries for any ZFTP drive that ISN'T currently mounted, so no ghosts linger.
// ============================================================================

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZFTP.Core;

public static class NetworkDriveCleanup
{
    private const string MountPoints2 =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2";

    /// <summary>Safe UNC share name from a display name (letters/digits only).</summary>
    public static string ShareName(string name)
    {
        var safe = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(safe) ? "Drive" : safe;
    }

    /// <summary>
    /// ZFTP now mounts everything as a LOCAL drive (under "Devices and drives"),
    /// so it no longer creates any Network-location cache entries. This purges
    /// leftover ghosts written by older network-mode builds of ZFTP:
    ///   ##ZFTP#&lt;share&gt;    — native SFTP drives (old UNC prefix \ZFTP\&lt;share&gt;)
    ///   ##server#&lt;volname&gt; — rclone drives (old --network-mode uses \\server\&lt;volname&gt;)
    /// </summary>
    public static void CleanGhosts()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(MountPoints2, writable: true);
            if (k == null) return;

            foreach (var name in k.GetSubKeyNames())
            {
                bool ours = name.StartsWith("##ZFTP#", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("##server#", StringComparison.OrdinalIgnoreCase);
                if (ours)
                    try { k.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
            }
        }
        catch { /* best effort */ }

        Refresh();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);

    private static void Refresh() => SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
}
