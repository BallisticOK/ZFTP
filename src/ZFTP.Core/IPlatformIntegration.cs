// ============================================================================
//  ZFTP — IPlatformIntegration
//  ---------------------------------------------------------------------------
//  OS-specific shell integration: launch-at-login, custom drive icons, and
//  mount-point cache cleanup. One implementation per OS. `PlatformIntegration.
//  Current` picks the right one at startup so the rest of the app never has
//  to branch on OS itself.
//
//  The Windows implementation wraps StartupManager/DriveIconManager/
//  NetworkDriveCleanup exactly as they already worked - unchanged behavior.
//  macOS/Linux get real implementations in later phases (launchd LaunchAgent /
//  XDG autostart for startup; there's no Explorer-icon or mount-point-cache
//  equivalent to clean up on those OSes, so those two stay no-ops there).
// ============================================================================

namespace ZFTP.Core;

public interface IPlatformIntegration
{
    /// <summary>Is ZFTP currently set to launch at login?</summary>
    bool IsStartupEnabled();

    /// <summary>Enable/disable launch at login (starts minimized to the tray).</summary>
    void SetStartupEnabled(bool enabled);

    /// <summary>Called after a drive mounts, so the shell can show it distinctly
    /// (e.g. a custom icon in Explorer). No-op where the OS has no equivalent.</summary>
    void OnDriveMounted(ConnectionProfile profile);

    /// <summary>Called after a drive unmounts - undo whatever OnDriveMounted did.</summary>
    void OnDriveUnmounted(ConnectionProfile profile);

    /// <summary>Nudge the shell to refresh after a batch of icon changes.</summary>
    void RefreshShell();

    /// <summary>Purge any leftover mount-point cache ghosts from old/renamed drives.</summary>
    void CleanupGhosts();
}

public static class PlatformIntegration
{
    public static IPlatformIntegration Current { get; } = Create();

    private static IPlatformIntegration Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatformIntegration();
        // Real macOS/Linux implementations land when those OSes are wired up
        // (launchd LaunchAgent / XDG autostart for startup); until then there's
        // simply no shell integration to perform.
        return new NoOpPlatformIntegration();
    }
}

internal sealed class WindowsPlatformIntegration : IPlatformIntegration
{
    private static string ExePath => Environment.ProcessPath ?? "";
    private static string IconResource => ExePath + ",0";

    public bool IsStartupEnabled() => StartupManager.IsEnabled();

    public void SetStartupEnabled(bool enabled) => StartupManager.Set(enabled, ExePath, "--minimized");

    public void OnDriveMounted(ConnectionProfile profile) => DriveIconManager.SetIcon(profile.DriveLetter, IconResource);

    public void OnDriveUnmounted(ConnectionProfile profile) => DriveIconManager.ClearIcon(profile.DriveLetter);

    public void RefreshShell() => DriveIconManager.Refresh();

    public void CleanupGhosts() => NetworkDriveCleanup.CleanGhosts();
}

internal sealed class NoOpPlatformIntegration : IPlatformIntegration
{
    public bool IsStartupEnabled() => false;
    public void SetStartupEnabled(bool enabled) { }
    public void OnDriveMounted(ConnectionProfile profile) { }
    public void OnDriveUnmounted(ConnectionProfile profile) { }
    public void RefreshShell() { }
    public void CleanupGhosts() { }
}
