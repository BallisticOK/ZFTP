using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZFTP.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine = new();
    private readonly bool _uninstallMode;
    private bool _busy;
    private string _installedPath = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _uninstallMode = Environment.GetCommandLineArgs().Any(arg =>
            arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        InstallPathBox.Text = InstallerEngine.DefaultInstallPath;
        VersionBadgeText.Text = $"ZFTP {InstallerEngine.Version} · Windows x64";
        InstallButton.Content = $"Install ZFTP {InstallerEngine.Version}  →";
        Loaded += (_, _) =>
        {
            if (_uninstallMode)
            {
                WelcomePanel.Visibility = Visibility.Collapsed;
                UninstallPanel.Visibility = Visibility.Visible;
                return;
            }
            RefreshPrerequisiteBadges();
        };
    }

    private void RefreshPrerequisiteBadges()
    {
        SetBadge(DotNetBadge, DotNetBadgeText, ".NET 8", _engine.IsDotNetDesktopInstalled());
        SetBadge(WinFspBadge, WinFspBadgeText, "WinFsp", _engine.IsWinFspInstalled());
    }

    private static void SetBadge(System.Windows.Controls.Border badge, System.Windows.Controls.TextBlock text, string name, bool installed)
    {
        text.Text = installed ? $"{name} · ready" : $"{name} · will install";
        text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#72E6AA" : "#FFD36B"));
        badge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#285B45" : "#574A27"));
        badge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#10271E" : "#28220F"));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to install ZFTP",
            InitialDirectory = Directory.Exists(InstallPathBox.Text)
                ? InstallPathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) == true)
            InstallPathBox.Text = Path.Combine(dialog.FolderName, "ZFTP");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var installPath = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
            return;

        WelcomePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CloseTopButton.IsEnabled = false;
        _busy = true;

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgress.Value = p.Percent;
            ProgressPercentText.Text = $"{p.Percent}%";
            ProgressStatusText.Text = p.Status;
            ProgressDetailText.Text = p.Detail;
        });

        try
        {
            await _engine.InstallAsync(new InstallOptions(
                installPath,
                DesktopShortcutCheck.IsChecked == true,
                StartupCheck.IsChecked == true), progress);

            _installedPath = installPath;
            _busy = false;
            CloseTopButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _busy = false;
            CloseTopButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        UninstallPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CloseTopButton.IsEnabled = false;
        _busy = true;

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgress.Value = p.Percent;
            ProgressPercentText.Text = $"{p.Percent}%";
            ProgressStatusText.Text = p.Status;
            ProgressDetailText.Text = p.Detail;
        });

        try
        {
            await _engine.UninstallAsync(progress);
            _busy = false;
            CloseTopButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            DoneSubtitle.Text = "ZFTP has been removed. Your saved profiles and themes were left untouched.";
            LaunchButton.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _busy = false;
            CloseTopButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        if (_uninstallMode)
            UninstallPanel.Visibility = Visibility.Visible;
        else
        {
            WelcomePanel.Visibility = Visibility.Visible;
            RefreshPrerequisiteBadges();
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(_installedPath, "ZFTP.exe");
        if (File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Close();
    }
}
