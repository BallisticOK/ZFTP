// ============================================================================
//  ZFTP — WinFspNative
//  ---------------------------------------------------------------------------
//  The WinFsp .NET binding (Fsp.Interop) needs the native winfsp-x64.dll. When
//  ZFTP is published as a single-file / self-contained app, the binding can't
//  always find that DLL on its own, which makes the very first use of any Fsp
//  type throw "The type initializer for 'Fsp.Interop.Api' threw an exception".
//
//  Fix: before we touch any WinFsp type, load the native DLL ourselves from the
//  install path recorded in the registry. Once it's loaded by full path, the
//  binding's [DllImport("winfsp-x64.dll")] calls resolve to it by name.
// ============================================================================

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZFTP.Core;

public static class WinFspNative
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    private static bool _done;

    /// <summary>True if WinFsp is installed and its native DLL was loaded.</summary>
    public static bool Available { get; private set; }

    /// <summary>Where WinFsp is installed, or null if not found.</summary>
    public static string? InstallDir { get; private set; }

    public static void EnsureLoaded()
    {
        if (_done) return;
        _done = true;

        try
        {
            InstallDir = FindInstallDir();
            if (string.IsNullOrEmpty(InstallDir)) return;

            var dll = Path.Combine(InstallDir, "bin", IntPtr.Size == 8 ? "winfsp-x64.dll" : "winfsp-x86.dll");
            if (File.Exists(dll) && LoadLibraryW(dll) != IntPtr.Zero)
                Available = true;
        }
        catch
        {
            // Leave Available = false; the app will report a friendly error.
        }
    }

    private static string? FindInstallDir()
    {
        // WinFsp registers under the 32-bit view (WOW6432Node) on x64.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\WinFsp");
                if (key?.GetValue("InstallDir") is string dir && !string.IsNullOrEmpty(dir))
                    return dir;
            }
            catch { /* try next view */ }
        }
        return null;
    }
}
