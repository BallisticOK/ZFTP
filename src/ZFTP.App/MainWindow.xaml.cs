// ============================================================================
//  ZFTP.App — MainWindow code-behind
//  ---------------------------------------------------------------------------
//  The drives table and everything around it: load/save servers, mount/unmount,
//  auto-mount on startup, the system-tray icon, and the live speed display.
// ============================================================================

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using ZFTP.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ZFTP.App;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<ServerItem> _servers = new();
    // Sample 2x/second and average over a short window so bursty transfers
    // (Windows buffers writes then flushes in bursts) show as a steady rate.
    private readonly DispatcherTimer _speedTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly long[] _readWindow = new long[6];   // 6 * 500ms = 3s window
    private readonly long[] _writeWindow = new long[6];
    private int _windowIdx;
    private System.Windows.Forms.NotifyIcon? _tray;
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
        // before the window has rendered leaves a blank/gray surface when it's
        // later restored from the tray.
        if (App.StartHidden || _settings.StartMinimized)
            ContentRendered += HideToTrayOnce;
    }

    private void HideToTrayOnce(object? sender, EventArgs e)
    {
        ContentRendered -= HideToTrayOnce;
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
        catch (Exception ex) { MessageBox.Show("Could not save: " + ex.Message, "ZFTP"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveProfiles();
        GlobalStatusText.Text = "Saved.";
    }

    // ---- selection helper --------------------------------------------------

    private ServerItem? Selected => DrivesList.SelectedItem as ServerItem;

    // ---- New / Edit / Duplicate / Delete -----------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var profile = new ConnectionProfile
        {
            Name = "New Server",
            DriveLetter = AvailableDriveLetters().LastOrDefault() ?? "Z",
        };
        var dlg = new EditDriveWindow(profile, DriveOptions(profile)) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var item = new ServerItem(dlg.Result);
            _servers.Add(item);
            DrivesList.SelectedItem = item;
            SaveProfiles();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void DrivesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var item = Selected;
        if (item == null) return;

        // Edit a copy; only commit if the user saves.
        var working = item.Profile.Clone();
        var dlg = new EditDriveWindow(working, DriveOptions(item.Profile)) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            bool wasMounted = item.IsMounted;
            if (wasMounted) item.Session.Unmount();   // settings changed — remount fresh
            item.Profile.CopyFrom(dlg.Result);
            item.RefreshAll();
            DrivesList.Items.Refresh();
            SaveProfiles();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        var copy = item.Profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " (copy)";
        copy.DriveLetter = AvailableDriveLetters().LastOrDefault() ?? copy.DriveLetter;
        var newItem = new ServerItem(copy);
        _servers.Add(newItem);
        DrivesList.SelectedItem = newItem;
        SaveProfiles();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        if (MessageBox.Show($"Delete '{item.Name}'?", "ZFTP",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (item.IsMounted) item.Session.Unmount();
        _servers.Remove(item);
        SaveProfiles();
        RefreshGlobalStatus();
    }

    // ---- Connect / Disconnect / Open ---------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item != null) await MountItemAsync(item);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        Selected?.Session.Unmount();
        DrivesList.Items.Refresh();
        RefreshGlobalStatus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item?.IsMounted == true) OpenInExplorer(item.Session.MountPoint);
    }

    // ---- Mount all / Unmount all -------------------------------------------

    private async void MountAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _servers.Where(s => s.Enabled && !s.IsMounted).ToList())
            await MountItemAsync(item);
    }

    private void UnmountAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _servers.Where(s => s.IsMounted).ToList())
            item.Session.Unmount();
        DrivesList.Items.Refresh();
        RefreshGlobalStatus();
    }

    private async Task AutoMountAsync()
    {
        if (!_settings.AutoMountOnStart) return;   // master switch in Settings
        foreach (var item in _servers.Where(s => s.Enabled && s.Profile.AutoMount && !s.IsMounted).ToList())
            await MountItemAsync(item, interactive: false);
    }

    private async Task MountItemAsync(ServerItem item, bool interactive = true)
    {
        bool ok = await item.Session.MountAsync();
        DrivesList.Items.Refresh();
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
                MessageBox.Show($"Could not mount '{item.Name}':\n{item.Session.LastError}", "ZFTP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Show a Windows toast/balloon via the tray icon.</summary>
    private void Notify(string title, string message)
    {
        try { _tray?.ShowBalloonTip(4000, title, message, System.Windows.Forms.ToolTipIcon.Info); }
        catch { /* ignore */ }
    }

    // ---- live speed + status ----------------------------------------------

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
    private void CleanGhosts() => NetworkDriveCleanup.CleanGhosts();

    // ---- custom drive icons in Explorer ------------------------------------

    private void SyncDriveIcons()
    {
        var icon = (Process.GetCurrentProcess().MainModule?.FileName ?? "") + ",0";
        foreach (var s in _servers)
        {
            if (s.IsMounted) DriveIconManager.SetIcon(s.Profile.DriveLetter, icon);
            else DriveIconManager.ClearIcon(s.Profile.DriveLetter);
        }
        DriveIconManager.Refresh();
    }

    // ---- settings tab ------------------------------------------------------

    private void LoadSettingsToggles()
    {
        _loadingSettings = true;   // suppress the Toggled handlers while we set initial state
        StartWithWindowsToggle.IsChecked = StartupManager.IsEnabled();
        StartMinimizedToggle.IsChecked = _settings.StartMinimized;
        TrayOnCloseToggle.IsChecked = _settings.MinimizeToTrayOnClose;
        AutoMountToggle.IsChecked = _settings.AutoMountOnStart;
        ThemeCombo.SelectedItem = ThemeDefs.Any(t => t.Name == _settings.Theme) ? _settings.Theme : ThemeDefs[0].Name;
        _loadingSettings = false;
    }

    private void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        bool on = StartWithWindowsToggle.IsChecked == true;
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        StartupManager.Set(on, exe, "--minimized");
        _settings.StartWithWindows = on;
        _settings.Save();
    }

    private void StartMinimized_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
        _settings.Save();
    }

    private void TrayOnClose_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.MinimizeToTrayOnClose = TrayOnCloseToggle.IsChecked == true;
        _settings.Save();
    }

    private void AutoMountOnStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AutoMountOnStart = AutoMountToggle.IsChecked == true;
        _settings.Save();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var theme = ThemeCombo.SelectedItem as string ?? "Dark";
        ApplyTheme(theme);
        _settings.Theme = theme;
        _settings.Save();
    }

    // Each theme = a base (Dark/Light) plus an accent colour (R,G,B).
    private static readonly (string Name, Wpf.Ui.Appearance.ApplicationTheme Base, byte R, byte G, byte B)[] ThemeDefs =
    {
        ("Dark Blue",        Wpf.Ui.Appearance.ApplicationTheme.Dark,  0x2D, 0x7D, 0xD2),
        ("Midnight Purple",  Wpf.Ui.Appearance.ApplicationTheme.Dark,  0x8B, 0x5C, 0xF6),
        ("Forest Green",     Wpf.Ui.Appearance.ApplicationTheme.Dark,  0x22, 0xC5, 0x5E),
        ("Sunset Orange",    Wpf.Ui.Appearance.ApplicationTheme.Dark,  0xF9, 0x73, 0x16),
        ("Crimson Red",      Wpf.Ui.Appearance.ApplicationTheme.Dark,  0xEF, 0x44, 0x44),
        ("Ocean Cyan",       Wpf.Ui.Appearance.ApplicationTheme.Dark,  0x06, 0xB6, 0xD4),
        ("Rose Pink",        Wpf.Ui.Appearance.ApplicationTheme.Dark,  0xEC, 0x48, 0x99),
        ("Amber Gold",       Wpf.Ui.Appearance.ApplicationTheme.Dark,  0xF5, 0xB3, 0x00),
        ("Light Blue",       Wpf.Ui.Appearance.ApplicationTheme.Light, 0x25, 0x63, 0xEB),
        ("Light Green",      Wpf.Ui.Appearance.ApplicationTheme.Light, 0x16, 0xA3, 0x4A),
    };

    private static void ApplyTheme(string name)
    {
        var def = ThemeDefs.FirstOrDefault(t => t.Name == name);
        if (def.Name == null) def = ThemeDefs[0];
        var accent = System.Windows.Media.Color.FromRgb(def.R, def.G, def.B);
        // Apply the base theme WITHOUT resetting the accent, then set our accent.
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(def.Base, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: false);
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(accent, def.Base);
    }

    // ---- system tray -------------------------------------------------------

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "ZFTP",
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open ZFTP", null, (_, _) => ShowFromTray());
        menu.Items.Add("Mount all", null, async (_, _) => { ShowFromTray(); await AutoMountAllEnabled(); });
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private static string CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
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
                var ask = MessageBox.Show(
                    $"ZFTP {info.Version} is available (you have {CurrentVersion}) — from {info.Source}.\n\n{info.Notes}\n\nDownload and install it now?",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes)
                {
                    UpdateStatusText.Text = "Downloading update…";
                    var path = await Updater.DownloadInstallerAsync(info.Url);
                    if (path != null)
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        ExitApp();   // close ZFTP so the installer can replace it
                    }
                    else
                    {
                        UpdateStatusText.Text = "Download failed. Try again later.";
                    }
                }
                break;
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { /* fall back below */ }
        return System.Drawing.SystemIcons.Application;
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
        _tray.Text = mounted > 0 ? $"ZFTP — {mounted} mounted" : "ZFTP";
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide(); // tuck into the tray
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit && _settings.MinimizeToTrayOnClose)
        {
            // Clicking X hides to tray instead of quitting (drives stay mounted).
            e.Cancel = true;
            Hide();
            return;
        }
        // Otherwise fully exit: unmount everything and remove the tray icon.
        if (!_reallyExit) { e.Cancel = true; ExitApp(); return; }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        foreach (var item in _servers.Where(s => s.IsMounted).ToList())
            item.Session.Unmount();
        SaveProfiles();
        // Drop our custom drive icons + Network-location entries (nothing mounted after exit).
        foreach (var s in _servers) DriveIconManager.ClearIcon(s.Profile.DriveLetter);
        DriveIconManager.Refresh();
        CleanGhosts();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Free drive letters, plus the profile's own letter so it stays selectable.</summary>
    private IEnumerable<string> DriveOptions(ConnectionProfile p)
    {
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

    private static void OpenInExplorer(string mountPoint)
    {
        try { Process.Start(new ProcessStartInfo(mountPoint + "\\") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
