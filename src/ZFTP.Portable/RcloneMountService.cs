using System.Diagnostics;

namespace ZFTP.Portable;

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

public static class RcloneMountService
{
    public static string Executable => ResolveExecutable();

    public static async Task<CommandResult> DoctorAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(Executable, ["version"], cancellationToken);

    public static async Task<IReadOnlyList<string>> ListRemotesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(Executable, ["listremotes"], cancellationToken);
        if (!result.Success) throw new InvalidOperationException(FriendlyError(result, "Could not list rclone remotes."));

        return result.Output
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.TrimEnd(':'))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<CommandResult> MountAsync(PortableProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureUnix();
        ValidateProfile(profile);

        var remotes = await ListRemotesAsync(cancellationToken);
        if (!remotes.Contains(profile.RemoteName, StringComparer.OrdinalIgnoreCase))
            return new CommandResult(2, $"rclone remote '{profile.RemoteName}' does not exist. Run 'zftp config' first.");

        var mountPath = ExpandHome(string.IsNullOrWhiteSpace(profile.MountPath)
            ? PortableProfileStore.DefaultMountPath(profile.Name)
            : profile.MountPath);
        Directory.CreateDirectory(mountPath);

        if (await IsMountedAsync(mountPath, cancellationToken))
            return new CommandResult(0, $"Already mounted at {mountPath}");

        var args = new List<string>
        {
            "mount", profile.RemoteSpec, mountPath,
            "--daemon",
            "--vfs-cache-mode", "full",
            "--vfs-cache-max-age", "12h",
            "--vfs-cache-max-size", "10G",
            "--dir-cache-time", "30s",
            "--attr-timeout", "8s",
            "--buffer-size", "32M",
        };

        if (profile.ReadOnly) args.Add("--read-only");
        if (OperatingSystem.IsMacOS())
        {
            args.Add("--volname");
            args.Add(string.IsNullOrWhiteSpace(profile.Name) ? "ZFTP" : profile.Name);
        }

        var result = await RunAsync(Executable, args, cancellationToken);
        return result.Success
            ? new CommandResult(0, $"Mounted {profile.Name} at {mountPath}")
            : new CommandResult(result.ExitCode, FriendlyError(result, $"Could not mount {profile.Name}."));
    }

    public static async Task<CommandResult> UnmountAsync(PortableProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureUnix();
        var mountPath = ExpandHome(string.IsNullOrWhiteSpace(profile.MountPath)
            ? PortableProfileStore.DefaultMountPath(profile.Name)
            : profile.MountPath);

        if (!await IsMountedAsync(mountPath, cancellationToken))
            return new CommandResult(0, $"{profile.Name} is not mounted.");

        var candidates = OperatingSystem.IsMacOS()
            ? new[] { ("diskutil", new[] { "unmount", mountPath }), ("umount", new[] { mountPath }) }
            : new[] { ("fusermount3", new[] { "-u", mountPath }), ("fusermount", new[] { "-u", mountPath }), ("umount", new[] { mountPath }) };

        var failures = new List<string>();
        foreach (var (command, args) in candidates)
        {
            if (!CommandExists(command)) continue;
            var result = await RunAsync(command, args, cancellationToken);
            if (result.Success) return new CommandResult(0, $"Unmounted {profile.Name} from {mountPath}");
            if (!string.IsNullOrWhiteSpace(result.Output)) failures.Add(result.Output.Trim());
        }

        return new CommandResult(1, failures.Count == 0
            ? $"Could not find a supported unmount command for {mountPath}."
            : string.Join(Environment.NewLine, failures));
    }

    public static async Task<bool> IsMountedAsync(string mountPath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        var expanded = Path.GetFullPath(ExpandHome(mountPath));
        var result = await RunAsync("mount", [], cancellationToken);
        if (!result.Success) return false;

        return result.Output.Replace("\\040", " ", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(expanded, StringComparison.Ordinal));
    }

    public static async Task<CommandResult> OpenMountAsync(PortableProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureUnix();
        var mountPath = ExpandHome(string.IsNullOrWhiteSpace(profile.MountPath)
            ? PortableProfileStore.DefaultMountPath(profile.Name)
            : profile.MountPath);
        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        return await RunAsync(command, [mountPath], cancellationToken, redirect: false);
    }

    public static CommandResult RunInteractiveConfig()
    {
        var psi = new ProcessStartInfo(Executable) { UseShellExecute = false };
        psi.ArgumentList.Add("config");
        using var process = Process.Start(psi);
        if (process is null) return new CommandResult(1, "Could not start rclone config.");
        process.WaitForExit();
        return new CommandResult(process.ExitCode, "");
    }

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, CancellationToken cancellationToken, bool redirect = true)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect,
                CreateNoWindow = redirect,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();

            if (!redirect)
            {
                await process.WaitForExitAsync(cancellationToken);
                return new CommandResult(process.ExitCode, "");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await stdout) + (await stderr);
            return new CommandResult(process.ExitCode, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(127, $"Could not start '{executable}'. {ex.Message}");
        }
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, command)));
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ZFTP_RCLONE");
        if (!string.IsNullOrWhiteSpace(configured)) return ExpandHome(configured);

        var localName = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var local = Path.Combine(AppContext.BaseDirectory, "tools", localName);
        if (File.Exists(local)) return local;

        if (OperatingSystem.IsMacOS())
        {
            foreach (var candidate in new[]
            {
                "/opt/homebrew/bin/rclone", // Apple Silicon Homebrew
                "/usr/local/bin/rclone",    // Intel Homebrew
                "/opt/local/bin/rclone",    // MacPorts
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var candidate in new[] { "/usr/bin/rclone", "/usr/local/bin/rclone" })
                if (File.Exists(candidate)) return candidate;
        }

        return localName;
    }

    private static void ValidateProfile(PortableProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Profile name is required.");
        if (string.IsNullOrWhiteSpace(profile.RemoteName)) throw new ArgumentException("rclone remote name is required.");
    }

    private static void EnsureUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The portable mount engine is for Linux and macOS. The existing Windows GUI remains the Windows implementation.");
    }

    private static string FriendlyError(CommandResult result, string prefix)
    {
        var detail = string.IsNullOrWhiteSpace(result.Output) ? $"Exit code {result.ExitCode}." : result.Output.Trim();
        return $"{prefix} {detail}";
    }
}
