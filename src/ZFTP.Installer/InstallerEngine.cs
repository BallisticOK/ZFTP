using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ZFTP.Installer;

public sealed record InstallOptions(string InstallPath, bool CreateDesktopShortcut, bool StartWithWindows);
public sealed record InstallProgress(int Percent, string Status, string Detail);

public sealed class InstallerEngine
{
    public static string Version { get; } = typeof(InstallerEngine).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public static string DefaultInstallPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZFTP");

    private const string PayloadResource = "ZFTP.Payload.zip";
    private const string WinFspResource = "ZFTP.WinFsp.msi";
    private const string DotNetResource = "ZFTP.DotNetDesktop.exe";

    public bool IsWinFspInstalled()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x64.dll")) ||
               File.Exists(Path.Combine(pf, "WinFsp", "bin", "winfsp-x64.dll"));
    }

    public bool IsDotNetDesktopInstalled()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(root))
            return false;
        return Directory.EnumerateDirectories(root, "8.*").Any();
    }

    public async Task InstallAsync(InstallOptions options, IProgress<InstallProgress> progress)
    {
        await Task.Run(() => InstallCore(options, progress));
    }

    public async Task UninstallAsync(IProgress<InstallProgress> progress)
    {
        await Task.Run(() => UninstallCore(progress));
    }

    private void InstallCore(InstallOptions options, IProgress<InstallProgress> progress)
    {
        progress.Report(new(3, "Preparing ZFTP", "Checking this PC…"));
        ValidateResources();
        options = options with { InstallPath = ValidateInstallPath(options.InstallPath) };

        var tempRoot = Path.Combine(Path.GetTempPath(), $"ZFTP-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            StopRunningZftp(options.InstallPath);

            if (!IsDotNetDesktopInstalled())
            {
                progress.Report(new(10, "Adding the .NET 8 desktop runtime", "Required to run ZFTP"));
                var dotnetPath = Path.Combine(tempRoot, "dotnet-desktop.exe");
                ExtractResource(DotNetResource, dotnetPath);
                RunAndRequireSuccess(dotnetPath, "/install /quiet /norestart", "the .NET 8 Desktop Runtime");
            }
            else
            {
                progress.Report(new(18, ".NET 8 is already ready", "Skipping an unnecessary install"));
            }

            if (!IsWinFspInstalled())
            {
                progress.Report(new(25, "Adding the WinFsp filesystem driver", "This is what makes drive letters possible"));
                var winfspPath = Path.Combine(tempRoot, "winfsp.msi");
                ExtractResource(WinFspResource, winfspPath);
                RunAndRequireSuccess("msiexec.exe", $"/i \"{winfspPath}\" /qn /norestart", "WinFsp");
            }
            else
            {
                progress.Report(new(32, "WinFsp is already ready", "Keeping the installed driver"));
            }

            progress.Report(new(42, "Unpacking ZFTP", "Installing the app and storage engines…"));
            var payloadZip = Path.Combine(tempRoot, "payload.zip");
            ExtractResource(PayloadResource, payloadZip);

            PrepareInstallDirectory(options.InstallPath);
            ZipFile.ExtractToDirectory(payloadZip, options.InstallPath, overwriteFiles: true);

            progress.Report(new(76, "Connecting ZFTP to Windows", "Creating shortcuts and app registration…"));
            ConfigureShortcuts(options);
            ConfigureStartup(options);
            RegisterUninstaller(options.InstallPath);

            if (!File.Exists(Path.Combine(options.InstallPath, "ZFTP.Uninstall.exe")))
                throw new InvalidOperationException("The uninstall helper was not included in the ZFTP payload.");

            progress.Report(new(100, "ZFTP is ready", "Installation complete"));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private void UninstallCore(IProgress<InstallProgress> progress)
    {
        var installPath = ValidateUninstallPath(GetRegisteredInstallPath() ?? DefaultInstallPath);
        progress.Report(new(10, "Preparing to remove ZFTP", "Closing the app and mounted-drive helpers…"));
        StopRunningZftp(installPath);

        progress.Report(new(35, "Removing Windows integration", "Cleaning up shortcuts and startup registration…"));
        RemoveShortcuts();
        using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            startup.DeleteValue("ZFTP", throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZFTP", throwOnMissingSubKey: false);

        progress.Report(new(62, "Removing ZFTP", "Deleting application files…"));
        var self = Environment.ProcessPath;
        if (Directory.Exists(installPath))
        {
            foreach (var file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (self is not null && Path.GetFullPath(file).Equals(Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(file); } catch { }
            }
            foreach (var directory in Directory.EnumerateDirectories(installPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                try { Directory.Delete(directory, recursive: false); } catch { }
            }
        }

        if (self is not null && File.Exists(self) && IsPathInside(self, installPath))
            ScheduleSelfDelete(self, installPath);

        progress.Report(new(100, "ZFTP removed", "Your saved profiles and themes were kept"));
    }

    private static string? GetRegisteredInstallPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZFTP");
        return key?.GetValue("InstallLocation") as string;
    }

    private static void RemoveShortcuts()
    {
        var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ZFTP");
        try
        {
            if (Directory.Exists(startMenuDir))
                Directory.Delete(startMenuDir, recursive: true);
        }
        catch { }

        var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ZFTP.lnk");
        try
        {
            if (File.Exists(desktopShortcut))
                File.Delete(desktopShortcut);
        }
        catch { }
    }

    private static bool IsPathInside(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateInstallPath(string installPath)
    {
        var fullPath = ValidateDirectoryTarget(installPath);
        if (!Directory.Exists(fullPath))
            return fullPath;

        var hasExistingFiles = Directory.EnumerateFileSystemEntries(fullPath).Any();
        var looksLikeExistingZftp = File.Exists(Path.Combine(fullPath, "ZFTP.exe")) ||
                                    File.Exists(Path.Combine(fullPath, "ZFTP.Uninstall.exe"));
        if (hasExistingFiles && !looksLikeExistingZftp)
        {
            throw new InvalidOperationException(
                "The selected install folder already contains other files. Choose an empty folder or an existing ZFTP installation folder.");
        }

        return fullPath;
    }

    private static string ValidateUninstallPath(string installPath)
    {
        var fullPath = ValidateDirectoryTarget(installPath);
        var selfDirectory = Environment.ProcessPath is { Length: > 0 } selfPath
            ? Path.GetDirectoryName(Path.GetFullPath(selfPath))
            : null;

        if (selfDirectory is null || !PathsEqual(fullPath, selfDirectory))
        {
            throw new InvalidOperationException(
                "The registered ZFTP install folder does not match the uninstall helper location, so uninstall was stopped to protect unrelated files.");
        }

        return fullPath;
    }

    private static string ValidateDirectoryTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Choose a valid folder for ZFTP.");

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || PathsEqual(fullPath, root))
            throw new InvalidOperationException("ZFTP cannot be installed to or removed from the root of a drive.");

        var protectedDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        if (protectedDirectories.Any(directory =>
                !string.IsNullOrWhiteSpace(directory) && PathsEqual(fullPath, directory)))
        {
            throw new InvalidOperationException(
                "Choose a dedicated ZFTP subfolder instead of a Windows, Program Files, or user-profile root folder.");
        }

        return fullPath;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ScheduleSelfDelete(string selfPath, string installPath)
    {
        var escapedSelf = selfPath.Replace("\"", "\"\"");
        var escapedInstall = installPath.Replace("\"", "\"\"");
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/d /c timeout /t 2 /nobreak >nul & del /f /q \"{escapedSelf}\" & rmdir /s /q \"{escapedInstall}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void ValidateResources()
    {
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
        foreach (var required in new[] { PayloadResource, WinFspResource, DotNetResource })
        {
            if (!names.Contains(required, StringComparer.Ordinal))
                throw new InvalidOperationException($"Setup is missing its embedded resource: {required}");
        }
    }

    private static void StopRunningZftp(string installPath)
    {
        var adb = Path.Combine(installPath, "tools", "adb.exe");
        if (File.Exists(adb))
        {
            try
            {
                using var adbProcess = Process.Start(new ProcessStartInfo(adb, "kill-server")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                adbProcess?.WaitForExit(5000);
            }
            catch { }
        }

        foreach (var process in Process.GetProcessesByName("ZFTP"))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private static void PrepareInstallDirectory(string installPath)
    {
        Directory.CreateDirectory(installPath);
        foreach (var file in Directory.EnumerateFiles(installPath))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var directory in Directory.EnumerateDirectories(installPath))
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void ExtractResource(string resourceName, string destinationPath)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded setup resource '{resourceName}' could not be opened.");
        using var output = File.Create(destinationPath);
        resource.CopyTo(output);
    }

    private static void RunAndRequireSuccess(string fileName, string arguments, string componentName)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException($"Could not start the installer for {componentName}.");

        process.WaitForExit();
        if (process.ExitCode is not (0 or 1641 or 3010))
            throw new InvalidOperationException($"Installing {componentName} failed with exit code {process.ExitCode}.");
    }

    private static void ConfigureShortcuts(InstallOptions options)
    {
        var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ZFTP");
        Directory.CreateDirectory(startMenuDir);
        CreateShortcut(Path.Combine(startMenuDir, "ZFTP.lnk"), Path.Combine(options.InstallPath, "ZFTP.exe"), options.InstallPath);
        CreateShortcut(Path.Combine(startMenuDir, "Check for ZFTP Updates.lnk"), Path.Combine(options.InstallPath, "ZFTP.Updater.exe"), options.InstallPath);

        var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ZFTP.lnk");
        if (options.CreateDesktopShortcut)
            CreateShortcut(desktopShortcut, Path.Combine(options.InstallPath, "ZFTP.exe"), options.InstallPath);
        else if (File.Exists(desktopShortcut))
            File.Delete(desktopShortcut);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, so a shortcut could not be created.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = Path.Combine(workingDirectory, "ZFTP.exe") + ",0";
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void ConfigureStartup(InstallOptions options)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (options.StartWithWindows)
            key.SetValue("ZFTP", $"\"{Path.Combine(options.InstallPath, "ZFTP.exe")}\" --minimized", RegistryValueKind.String);
        else
            key.DeleteValue("ZFTP", throwOnMissingValue: false);
    }

    private static void RegisterUninstaller(string installPath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZFTP", writable: true);
        key.SetValue("DisplayName", "ZFTP");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "ZFTP");
        key.SetValue("InstallLocation", installPath);
        key.SetValue("DisplayIcon", Path.Combine(installPath, "ZFTP.exe"));
        key.SetValue("UninstallString", $"\"{Path.Combine(installPath, "ZFTP.Uninstall.exe")}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", 220000, RegistryValueKind.DWord);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
