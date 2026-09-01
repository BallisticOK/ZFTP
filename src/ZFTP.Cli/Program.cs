using System.Reflection;
using ZFTP.Portable;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The ZFTP CLI is intended for Linux. On Windows, keep using the existing ZFTP GUI.");
            return 2;
        }

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Help();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => await ListAsync(),
                "status" => await StatusAsync(),
                "remotes" => await RemotesAsync(),
                "config" => Config(),
                "doctor" => await DoctorAsync(),
                "add" => Add(args[1..]),
                "remove" or "rm" => Remove(args[1..]),
                "mount" => await MountAsync(args[1..]),
                "unmount" or "umount" => await UnmountAsync(args[1..]),
                "open" => await OpenAsync(args[1..]),
                "version" or "--version" => Version(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"zftp: {ex.Message}");
            return 1;
        }
    }

    private static Task<int> ListAsync()
    {
        var profiles = PortableProfileStore.Load();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No ZFTP profiles yet. Add one with: zftp add --name MyDrive --remote myremote");
            return Task.FromResult(0);
        }

        Console.WriteLine("NAME\tREMOTE\tMOUNT PATH\tAUTO\tMODE");
        foreach (var p in profiles)
            Console.WriteLine($"{p.Name}\t{p.RemoteSpec}\t{ResolvedMount(p)}\t{(p.AutoMount ? "yes" : "no")}\t{(p.ReadOnly ? "read-only" : "read-write")}");
        return Task.FromResult(0);
    }

    private static async Task<int> StatusAsync()
    {
        var profiles = PortableProfileStore.Load();
        if (profiles.Count == 0) return await ListAsync();

        foreach (var p in profiles)
        {
            var mounted = await RcloneMountService.IsMountedAsync(ResolvedMount(p));
            Console.WriteLine($"{(mounted ? "mounted " : "stopped ")}  {p.Name}  ->  {ResolvedMount(p)}");
        }
        return 0;
    }

    private static async Task<int> RemotesAsync()
    {
        var remotes = await RcloneMountService.ListRemotesAsync();
        if (remotes.Count == 0) Console.WriteLine("No rclone remotes configured. Run: zftp config");
        foreach (var remote in remotes) Console.WriteLine(remote);
        return 0;
    }

    private static int Config()
    {
        Console.WriteLine("Opening rclone's provider setup. When it finishes, use 'zftp remotes' to see the remote name.");
        return RcloneMountService.RunInteractiveConfig().ExitCode;
    }

    private static async Task<int> DoctorAsync()
    {
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Config: {PortableProfileStore.FilePath}");
        Console.WriteLine($"rclone: {RcloneMountService.Executable}");
        var result = await RcloneMountService.DoctorAsync();
        if (!result.Success)
        {
            Console.WriteLine("rclone: NOT READY");
            Console.WriteLine(result.Output);
            Console.WriteLine(OperatingSystem.IsMacOS()
                ? "Install rclone and macFUSE, then run this check again."
                : "Install rclone and a FUSE 3 package (for example fuse3), then run this check again.");
            return 1;
        }

        Console.WriteLine("rclone: ready");
        Console.WriteLine(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim());
        Console.WriteLine(OperatingSystem.IsMacOS() ? "Mount driver: macFUSE is required for rclone mount." : "Mount driver: FUSE 3 is required for rclone mount.");
        return 0;
    }

    private static int Add(string[] args)
    {
        var name = Option(args, "--name") ?? throw new ArgumentException("--name is required.");
        var remote = Option(args, "--remote") ?? throw new ArgumentException("--remote is required. Use 'zftp remotes' to list configured rclone remotes.");
        var profiles = PortableProfileStore.Load();
        if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A profile named '{name}' already exists.");

        var profile = new PortableProfile
        {
            Name = name,
            RemoteName = remote.TrimEnd(':'),
            RemotePath = Option(args, "--path") ?? "",
            MountPath = RcloneMountService.ExpandHome(Option(args, "--mount") ?? PortableProfileStore.DefaultMountPath(name)),
            ReadOnly = Has(args, "--read-only"),
            AutoMount = Has(args, "--auto"),
        };
        profiles.Add(profile);
        PortableProfileStore.Save(profiles);
        Console.WriteLine($"Added {profile.Name}: {profile.RemoteSpec} -> {profile.MountPath}");
        return 0;
    }

    private static int Remove(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Give the profile name or id to remove.");
        var profiles = PortableProfileStore.Load();
        var profile = Find(profiles, args[0]);
        profiles.Remove(profile);
        PortableProfileStore.Save(profiles);
        Console.WriteLine($"Removed {profile.Name}. The rclone remote itself was left untouched.");
        return 0;
    }

    private static async Task<int> MountAsync(string[] args)
    {
        var profiles = PortableProfileStore.Load();
        var targets = SelectTargets(profiles, args, allowAuto: true);
        return await RunForEach(targets, RcloneMountService.MountAsync);
    }

    private static async Task<int> UnmountAsync(string[] args)
    {
        var profiles = PortableProfileStore.Load();
        var targets = SelectTargets(profiles, args, allowAuto: false);
        return await RunForEach(targets, RcloneMountService.UnmountAsync);
    }

    private static async Task<int> OpenAsync(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Give the profile name or id to open.");
        var profile = Find(PortableProfileStore.Load(), args[0]);
        if (!await RcloneMountService.IsMountedAsync(ResolvedMount(profile)))
            throw new InvalidOperationException($"{profile.Name} is not mounted.");
        var result = await RcloneMountService.OpenMountAsync(profile);
        if (!result.Success) Console.Error.WriteLine(result.Output);
        return result.ExitCode;
    }

    private static int Version()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        Console.WriteLine($"ZFTP CLI {version}");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'zftp help'.");
        return 2;
    }

    private static async Task<int> RunForEach(IEnumerable<PortableProfile> profiles, Func<PortableProfile, CancellationToken, Task<CommandResult>> action)
    {
        var targets = profiles.ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine("No matching profiles.");
            return 0;
        }

        var exit = 0;
        foreach (var profile in targets)
        {
            var result = await action(profile, CancellationToken.None);
            Console.WriteLine(result.Output);
            if (!result.Success) exit = result.ExitCode == 0 ? 1 : result.ExitCode;
        }
        return exit;
    }

    private static IEnumerable<PortableProfile> SelectTargets(List<PortableProfile> profiles, string[] args, bool allowAuto)
    {
        if (Has(args, "--all")) return profiles;
        if (allowAuto && Has(args, "--auto")) return profiles.Where(p => p.AutoMount);
        if (args.Length == 0) throw new ArgumentException("Give a profile name, --all" + (allowAuto ? ", or --auto." : "."));
        return [Find(profiles, args[0])];
    }

    private static PortableProfile Find(List<PortableProfile> profiles, string key) =>
        profiles.FirstOrDefault(p => p.Id.Equals(key, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No profile matches '{key}'.");

    private static string ResolvedMount(PortableProfile p) => RcloneMountService.ExpandHome(
        string.IsNullOrWhiteSpace(p.MountPath) ? PortableProfileStore.DefaultMountPath(p.Name) : p.MountPath);

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Help()
    {
        Console.WriteLine("""
ZFTP CLI - Linux command-line client (macOS uses the ZFTP GUI)

Usage:
  zftp doctor
  zftp config
  zftp remotes
  zftp add --name NAME --remote RCLONE_REMOTE [--path REMOTE_PATH] [--mount LOCAL_PATH] [--read-only] [--auto]
  zftp list
  zftp status
  zftp mount NAME | --all | --auto
  zftp unmount NAME | --all
  zftp open NAME
  zftp remove NAME

Notes:
  Linux needs rclone + FUSE 3 for mounts.
  ZFTP uses rclone's own provider config and keeps portable profiles separate from the Windows GUI config.
""");
    }
}
