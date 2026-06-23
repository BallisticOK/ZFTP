// ============================================================================
//  ZFTP — StartupManager
//  Toggles whether ZFTP launches automatically when you log into Windows.
//  Uses the per-user "Run" registry key (no admin rights needed).
// ============================================================================

using Microsoft.Win32;

namespace ZFTP.Core;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZFTP";

    /// <summary>Is ZFTP currently set to start with Windows?</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) != null;
    }

    /// <summary>
    /// Enable/disable launch at login. Pass the path to ZFTP.exe and any args
    /// (e.g. "--minimized" so it starts hidden in the tray).
    /// </summary>
    public static void Set(bool enabled, string exePath, string arguments = "")
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key == null) return;

        if (enabled)
        {
            var command = string.IsNullOrEmpty(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            key.SetValue(ValueName, command);
        }
        else if (key.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
