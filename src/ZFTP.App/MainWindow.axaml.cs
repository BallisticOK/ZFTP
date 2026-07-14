// ============================================================================
//  ZFTP.App — MainWindow code-behind
//  ---------------------------------------------------------------------------
//  The drives table and everything around it: load/save servers, mount/unmount,
//  auto-mount on startup, the system-tray icon, and the live speed display.
// ============================================================================

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using ZFTP.Core;

namespace ZFTP.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ServerItem> _servers = new();
    // Sample 2x/second and average over a short window so bursty transfers
    // show as a steady rate.
    private readonly DispatcherTimer _speedTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly long[] _readWindow = new long[6];   // 6 * 500ms = 3s window
    private readonly long[] _writeWindow = new long[6];
    private int _windowIdx;
    private TrayIcon? _tray;
    private bool _reallyExit;
    private bool _loadingSettings;

    private readonly AppSettings _settings = AppSettings.Load();

    // Used to compute speed = (bytes now - bytes last tick) per second.
    private long _lastBytesRead;
    private long _lastBytesWritten;

    public MainWindow()
    {
        InitializeComponent();

        DrivesList.ItemsSource = _servers;
        ConfigPathText.Text = ProfileStore.FilePath;
        VersionText.Text = $"ZFTP version {CurrentVersion}";
        FooterVersionText.Text = $"ZFTP v{CurrentVersion}";

        foreach (var t in ThemeDefs) ThemeCombo.Items.Add(t.Name);
        ApplyTheme(_settings.Theme);
        LoadProfiles();
        CleanGhosts();   // clear any disconnected entries left over from a prior crash
        SetupTray();
        LoadSettingsToggles();

        _speedTimer.Tick += SpeedTimer_Tick;
        _speedTimer.Start();

        Loaded += async (_, _) => await AutoMountAsync();

        // If we should start in the tray, hide ONLY after the first paint — hiding
        // before the window has rendered leaves a blank surface when it's later
        // restored from the tray.
        if (App.StartHidden || _settings.StartMinimized)
            Opened += HideToTrayOnce;

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                Hide();   // tuck into the tray instead of showing a taskbar-minimized window
        };
        Closing += (_, e) =>
        {
            if (!_reallyExit && _settings.MinimizeToTrayOnClose)
            {
                // Clicking X hides to tray instead of quitting (drives stay mounted).
                e.Cancel = true;
                Hide();
                return;
            }
            if (!_reallyExit) { e.Cancel = true; ExitApp(); }
        };
    }

    private void HideToTrayOnce(object? sender, EventArgs e)
    {
        Opened -= HideToTrayOnce;
        Hide();
    }

    // ---- load / save -------------------------------------------------------

    private void LoadProfiles()
    {
        _servers.Clear();
        foreach (var p in ProfileStore.Load())
            _servers.Add(new ServerItem(p));
        if (_servers.Count > 0) DrivesList.SelectedIndex = 0;
        RefreshGlobalStatus();
    }

    private void SaveProfiles()
    {
        try { ProfileStore.Save(_servers.Select(s => s.Profile)); }
        catch (Exception ex) { _ = Dialogs.ShowMessageAsync("ZFTP", "Could not save: " + ex.Message); }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        SaveProfiles();
        GlobalStatusText.Text = "Saved.";
    }

    // ---- selection helper --------------------------------------------------

    private ServerItem? Selected => DrivesList.SelectedItem as ServerItem;

    // ---- New / Edit / Duplicate / Delete -----------------------------------

    private async void New_Click(object? sender, RoutedEventArgs e)
    {
        var profile = new ConnectionProfile { Name = "New Server" };
        if (OperatingSystem.IsWindows())
            profile.DriveLetter = AvailableDriveLetters().LastOrDefault() ?? "Z";

        var dlg = new EditDriveWindow(profile, DriveOptions(profile));
        if (await dlg.ShowDialog<bool>(this))
        {
            var item = new ServerItem(dlg.Result);
            _servers.Add(item);
            DrivesList.SelectedItem = item;
            SaveProfiles();
        }
    }

    private void Edit_Click(object? sender, RoutedEventArgs e) => _ = EditSelectedAsync();

    private void DrivesList_DoubleTapped(object? sender, TappedEventArgs e) => _ = EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        var item = Selected;
        if (item == null) return;

        // Edit a copy; only commit if the user saves.
        var working = item.Profile.Clone();
        var dlg = new EditDriveWindow(working, DriveOptions(item.Profile));
        if (await dlg.ShowDialog<bool>(this))
        {
            bool wasMounted = item.IsMounted;
            if (wasMounted) item.Session.Unmount();   // settings changed — remount fresh
            item.Profile.CopyFrom(dlg.Result);
            item.RefreshAll();
            SaveProfiles();
        }
    }

    private void Duplicate_Click(object? sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        var copy = item.Profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " (copy)";
        if (OperatingSystem.IsWindows())
            copy.DriveLetter = AvailableDriveLetters().LastOrDefault() ?? copy.DriveLetter;
        else
            copy.MountPath = "";   // let it pick its own default under ~/ZFTP/mounts
        var newItem = new ServerItem(copy);
        _servers.Add(newItem);
        DrivesList.SelectedItem = newItem;
        SaveProfiles();
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        if (!await Dialogs.ShowConfirmAsync("ZFTP", $"Delete '{item.Name}'?")) return;
        if (item.IsMounted) item.Session.Unmount();
        _servers.Remove(item);
        SaveProfiles();
        RefreshGlobalStatus();
        MaybeStopAdb();
    }

    // ---- Connect / Disconnect / Open ---------------------------------------

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item != null) await MountItemAsync(item);
    }

    private void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        Selected?.Session.Unmount();
        RefreshGlobalStatus();
        MaybeStopAdb();
    }

    /// <summary>
    /// If no Android drive is mounted anymore, stop the adb background server so
    /// it isn't left running (and holding tools/adb open) when it's not in use.
    /// Runs off the UI thread so kill-server can't stall the window.
    /// </summary>
    private void MaybeStopAdb()
    {
        bool anyAndroidMounted = _servers.Any(s => s.Profile.Provider == ProviderType.Android && s.IsMounted);
        if (!anyAndroidMounted)
            Task.Run(() => { try { AdbService.KillServer(); } catch { /* ignore */ } });
    }

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item?.IsMounted == true) OpenInFileManager(item.Session.MountPoint);
    }

    // ---- Mount all / Unmount all -------------------------------------------

    private async void MountAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _servers.Where(s => s.Enabled && !s.IsMounted).ToList())
            await MountItemAsync(item);
    }

    private void UnmountAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _servers.Where(s => s.IsMounted).ToList())
            item.Session.Unmount();
        RefreshGlobalStatus();
        MaybeStopAdb();
    }

    private async Task AutoMountAsync()
    {
        if (!_settings.AutoMountOnStart) return;   // master switch in Settings
        foreach (var item in _servers.Where(s => s.Enabled && s.Profile.AutoMount && !s.IsMounted).ToList())
        {
            if (!OperatingSystem.IsWindows() && item.Profile.Provider is ProviderType.Android or ProviderType.IPhone)
                continue;   // no adb/AFC mount engine outside Windows yet
            await MountItemAsync(item, interactive: false);
        }
    }

    private async Task MountItemAsync(ServerItem item, bool interactive = true)
    {
        bool ok = await item.Session.MountAsync();
        RefreshGlobalStatus();
        if (ok)
        {
            SaveProfiles();   // persist the host key captured on first connect
            Notify($"{item.Name} mounted", $"Now available on {item.Session.MountPoint}");
        }
        else
        {
            Notify($"{item.Name} couldn't mount", item.Session.LastError ?? "Unknown error");
            // Only pop a modal dialog for an action the user just took — never
            // during auto-mount/tray startup (a modal there can destabilize the app).
            if (interactive && IsVisible)
                await Dialogs.ShowMessageAsync("ZFTP", $"Could not mount '{item.Name}':\n{item.Session.LastError}");
        }
    }

    /// <summary>
    /// WPF used a Windows balloon tip here; Avalonia's cross-platform TrayIcon has
    /// no built-in toast/notification API, so this is a no-op for now — a real,
    /// known gap versus the old build, not silently dropped.
    /// </summary>
    private void Notify(string title, string message) { }

    // ---- live speed + status ------------------------------------------------

    private void SpeedTimer_Tick(object? sender, EventArgs e)
    {
        long totalRead = _servers.Sum(s => s.Session.BytesRead);
        long totalWritten = _servers.Sum(s => s.Session.BytesWritten);

        // Bytes since the last 500ms tick.
        long dRead = Math.Max(0, totalRead - _lastBytesRead);
        long dWrite = Math.Max(0, totalWritten - _lastBytesWritten);
        _lastBytesRead = totalRead;
        _lastBytesWritten = totalWritten;

        // Slide them into the averaging window.
        _readWindow[_windowIdx] = dRead;
        _writeWindow[_windowIdx] = dWrite;
        _windowIdx = (_windowIdx + 1) % _readWindow.Length;

        double windowSeconds = _readWindow.Length * _speedTimer.Interval.TotalSeconds;
        long downRate = (long)(_readWindow.Sum() / windowSeconds);
        long upRate = (long)(_writeWindow.Sum() / windowSeconds);

        DownSpeedText.Text = FormatRate(downRate);
        UpSpeedText.Text = FormatRate(upRate);
        DownTotalText.Text = $"({FormatSize(totalRead)})";
        UpTotalText.Text = $"({FormatSize(totalWritten)})";
    }

    private void RefreshGlobalStatus()
    {
        int mounted = _servers.Count(s => s.IsMounted);
        if (mounted > 0)
        {
            GlobalStatusDot.Fill = Brushes.LimeGreen;
            GlobalStatusText.Text = $"{mounted} drive{(mounted == 1 ? "" : "s")} mounted";
        }
        else
        {
            GlobalStatusDot.Fill = Brushes.Gray;
            GlobalStatusText.Text = "Idle — no drives mounted";
        }
        UpdateTrayText();
        SyncDriveIcons();
        CleanGhosts();
    }

    /// <summary>Purge leftover Network-location ghosts from older network-mode builds.</summary>
    private void CleanGhosts() => PlatformIntegration.Current.CleanupGhosts();

    // ---- custom drive icons in Explorer -------------------------------------

    private void SyncDriveIcons()
    {
        foreach (var s in _servers)
        {
            if (s.IsMounted) PlatformIntegration.Current.OnDriveMounted(s.Profile);
            else PlatformIntegration.Current.OnDriveUnmounted(s.Profile);
        }
        PlatformIntegration.Current.RefreshShell();
    }

    // ---- settings tab --------------------------------------------------------

    private void LoadSettingsToggles()
    {
        _loadingSettings = true;   // suppress the Toggled handlers while we set initial state
        StartWithWindowsToggle.IsChecked = PlatformIntegration.Current.IsStartupEnabled();
        StartMinimizedToggle.IsChecked = _settings.StartMinimized;
        TrayOnCloseToggle.IsChecked = _settings.MinimizeToTrayOnClose;
        AutoMountToggle.IsChecked = _settings.AutoMountOnStart;
        ThemeCombo.SelectedItem = ThemeDefs.Any(t => t.Name == _settings.Theme) ? _settings.Theme : ThemeDefs[0].Name;
        _loadingSettings = false;
    }

    private void StartWithWindows_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = StartWithWindowsToggle.IsChecked == true;
        PlatformIntegration.Current.SetStartupEnabled(on);
        _settings.StartWithWindows = on;
        _settings.Save();
    }

    private void StartMinimized_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
        _settings.Save();
    }

    private void TrayOnClose_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.MinimizeToTrayOnClose = TrayOnCloseToggle.IsChecked == true;
        _settings.Save();
    }

    private void AutoMountOnStart_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AutoMountOnStart = AutoMountToggle.IsChecked == true;
        _settings.Save();
    }

    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var theme = ThemeCombo.SelectedItem as string ?? "Dark Blue";
        ApplyTheme(theme);
        _settings.Theme = theme;
        _settings.Save();
    }

    // Each theme = a base (dark/light) plus an accent colour (R,G,B).
    private static readonly (string Name, bool Dark, byte R, byte G, byte B)[] ThemeDefs =
    {
        ("Dark Blue",        true,  0x2D, 0x7D, 0xD2),
        ("Midnight Purple",  true,  0x8B, 0x5C, 0xF6),
        ("Forest Green",     true,  0x22, 0xC5, 0x5E),
        ("Sunset Orange",    true,  0xF9, 0x73, 0x16),
        ("Crimson Red",      true,  0xEF, 0x44, 0x44),
        ("Ocean Cyan",       true,  0x06, 0xB6, 0xD4),
        ("Rose Pink",        true,  0xEC, 0x48, 0x99),
        ("Amber Gold",       true,  0xF5, 0xB3, 0x00),
        ("Light Blue",       false, 0x25, 0x63, 0xEB),
        ("Light Green",      false, 0x16, 0xA3, 0x4A),
    };

    private static void ApplyTheme(string name)
    {
        var def = ThemeDefs.FirstOrDefault(t => t.Name == name);
        if (def.Name == null) def = ThemeDefs[0];
        var accent = Color.FromRgb(def.R, def.G, def.B);

        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = def.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var faTheme = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (faTheme != null) faTheme.CustomAccentColor = accent;
    }

    // ---- system tray ---------------------------------------------------------

    private void SetupTray()
    {
        _tray = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            IsVisible = true,
            ToolTipText = "ZFTP",
        };
        _tray.Clicked += (_, _) => ShowFromTray();

        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open ZFTP");
        open.Click += (_, _) => ShowFromTray();
        var mountAll = new NativeMenuItem("Mount all");
        mountAll.Click += async (_, _) => { ShowFromTray(); await AutoMountAllEnabled(); };
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        menu.Add(open);
        menu.Add(mountAll);
        menu.Add(exit);
        _tray.Menu = menu;

        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    private static WindowIcon LoadTrayIcon()
    {
        // Authority must be the actual assembly name (AssemblyName=ZFTP in the
        // csproj), not the project/namespace name ZFTP.App.
        using var stream = AssetLoader.Open(new Uri("avares://ZFTP/Assets/logo.png"));
        return new WindowIcon(new Bitmap(stream));
    }

    private static string CurrentVersion =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";

        var result = await Updater.CheckAsync(CurrentVersion);

        CheckUpdateButton.IsEnabled = true;
        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                UpdateStatusText.Text = $"You're on the latest version ({CurrentVersion}).";
                break;

            case UpdateCheckStatus.CouldNotCheck:
                UpdateStatusText.Text = "No update info found — no GitHub release yet, and the CDN couldn't be reached.";
                break;

            case UpdateCheckStatus.UpdateAvailable:
                var info = result.Info!;
                if (string.IsNullOrEmpty(info.Url))
                {
                    // A release exists but nothing is published for this OS yet
                    // (true for Linux/macOS today - CI only publishes a Windows
                    // installer). Say so plainly rather than assuming an
                    // installer exists.
                    UpdateStatusText.Text = $"ZFTP {info.Version} is available, but no build is published for this OS yet. Check github.com/BallisticOK/ZFTP/releases.";
                    break;
                }

                var ask = await Dialogs.ShowConfirmAsync("Update available",
                    $"ZFTP {info.Version} is available (you have {CurrentVersion}) — from {info.Source}.\n\n{info.Notes}\n\nDownload and install it now?");
                if (ask)
                {
                    UpdateStatusText.Text = "Downloading update…";
                    var path = await Updater.DownloadInstallerAsync(info.Url);
                    if (path != null)
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                            ExitApp();   // close ZFTP so the installer can replace it
                        }
                        else
                        {
                            UpdateStatusText.Text = $"Downloaded to {path}. Quit ZFTP, replace the running copy, and relaunch it.";
                        }
                    }
                    else
                    {
                        UpdateStatusText.Text = "Download failed. Try again later.";
                    }
                }
                break;
        }
    }

    private async Task AutoMountAllEnabled()
    {
        foreach (var item in _servers.Where(s => s.Enabled && !s.IsMounted).ToList())
            await MountItemAsync(item, interactive: false);
    }

    private void UpdateTrayText()
    {
        if (_tray == null) return;
        int mounted = _servers.Count(s => s.IsMounted);
        _tray.ToolTipText = mounted > 0 ? $"ZFTP — {mounted} mounted" : "ZFTP";
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        foreach (var item in _servers.Where(s => s.IsMounted).ToList())
            item.Session.Unmount();
        // Stop the adb background server so it stops holding tools/adb open
        // (otherwise an update can't replace it). No-op if adb was never used.
        try { AdbService.KillServer(); } catch { /* ignore */ }
        SaveProfiles();
        // Drop our custom drive icons + Network-location entries (nothing mounted after exit).
        foreach (var s in _servers) PlatformIntegration.Current.OnDriveUnmounted(s.Profile);
        PlatformIntegration.Current.RefreshShell();
        CleanGhosts();
        if (_tray != null) _tray.IsVisible = false;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Free drive letters, plus the profile's own letter so it stays selectable.
    /// Windows only - see EditDriveWindow for the Linux/macOS mount-path equivalent.</summary>
    private IEnumerable<string> DriveOptions(ConnectionProfile p)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var letters = AvailableDriveLetters().ToList();
        var own = p.DriveLetter.TrimEnd(':');
        if (!letters.Contains(own)) letters.Insert(0, own);
        return letters;
    }

    private IEnumerable<string> AvailableDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToHashSet();
        // Letters already claimed by an enabled ZFTP drive count as used too.
        foreach (var s in _servers)
            used.Add(char.ToUpper(s.Profile.DriveLetter.TrimEnd(':')[0]));
        for (char c = 'D'; c <= 'Z'; c++)
            if (!used.Contains(c)) yield return c.ToString();
    }

    private static string FormatRate(long bytesPerSec)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double v = bytesPerSec;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {units[u]}" : $"{v:0.0} {units[u]}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {units[u]}" : $"{v:0.0} {units[u]}";
    }

    private static void OpenInFileManager(string mountPoint)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(mountPoint + "\\") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", mountPoint));
            else
                Process.Start(new ProcessStartInfo("xdg-open", mountPoint));
        }
        catch { /* ignore */ }
    }
}
