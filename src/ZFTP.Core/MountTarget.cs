// ============================================================================
//  ZFTP — MountTarget
//  ---------------------------------------------------------------------------
//  Resolves what a profile actually mounts onto: a drive letter string on
//  Windows (unchanged legacy behavior), or a directory path on Linux/macOS,
//  where there's no drive-letter concept and FUSE/rclone require the mount
//  directory to already exist (and be empty) before mounting into it.
// ============================================================================

using System.Text.RegularExpressions;

namespace ZFTP.Core;

public static class MountTarget
{
    /// <summary>Resolve this profile's mount target, creating the directory first
    /// when it's a path (non-Windows) rather than a drive letter (Windows).</summary>
    public static string Resolve(ConnectionProfile profile)
    {
        if (OperatingSystem.IsWindows())
            return profile.DriveLetter.TrimEnd(':', '\\') + ":";

        var path = string.IsNullOrWhiteSpace(profile.MountPath)
            ? DefaultMountPath(profile)
            : profile.MountPath;

        Directory.CreateDirectory(path);
        return path;
    }

    private static string DefaultMountPath(ConnectionProfile profile)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZFTP", "mounts");
        var idFragment = profile.Id[..Math.Min(8, profile.Id.Length)];
        return Path.Combine(root, $"{Slugify(profile.Name)}-{idFragment}");
    }

    private static string Slugify(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "drive" : slug;
    }
}
