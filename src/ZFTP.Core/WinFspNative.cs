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

    private static readonly object Sync = new();
    private static bool _done;

    /// <summary>True if WinFsp is installed and its native DLL was loaded.</summary>
    public static bool Available { get; private set; }

    /// <summary>Where WinFsp is installed, or null if not found.</summary>
    public static string? InstallDir { get; private set; }

    /// <summary>Diagnostic detail from the last WinFsp discovery/load attempt.</summary>
    public static string? LastError { get; private set; }

    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_done) return;
            _done = true;

            try
            {
                InstallDir = FindInstallDir();
                if (string.IsNullOrEmpty(InstallDir))
                {
                    LastError = "WinFsp install directory was not found in the registry or standard install folders.";
                    AppLog.Error("WinFsp", LastError);
                    return;
                }

                var dll = Path.Combine(InstallDir, "bin", IntPtr.Size == 8 ? "winfsp-x64.dll" : "winfsp-x86.dll");
                if (!File.Exists(dll))
                {
                    LastError = $"WinFsp native DLL was not found at {dll}.";
                    AppLog.Error("WinFsp", LastError);
                    return;
                }

                var handle = LoadLibraryW(dll);
                if (handle == IntPtr.Zero)
                {
                    var code = Marshal.GetLastWin32Error();
                    LastError = $"LoadLibrary failed for {dll} with Win32 error {code}.";
                    AppLog.Error("WinFsp", LastError);
                    return;
                }

                Available = true;
                LastError = null;
                AppLog.Info("WinFsp", $"Loaded native library from {dll}.");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Error("WinFsp", "Unexpected error while loading WinFsp.", ex);
                // Leave Available = false; the app will report a friendly error.
            }
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

        // Some WinFsp installs can be present even when the registry entry is
        // unavailable to the current process. Fall back to the normal locations.
        foreach (var baseDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            var candidate = Path.Combine(baseDir, "WinFsp");
            var dll = Path.Combine(candidate, "bin", IntPtr.Size == 8 ? "winfsp-x64.dll" : "winfsp-x86.dll");
            if (File.Exists(dll)) return candidate;
        }

        return null;
    }
}
