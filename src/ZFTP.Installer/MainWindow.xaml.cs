using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZFTP.Installer;

public partial class MainWindow : Window
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;
    private const uint LogonWithProfile = 0x00000001;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

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
        {
            if (!LaunchAsDesktopUser(exe))
            {
                MessageBox.Show(
                    "ZFTP was installed successfully, but Setup could not start it with your normal desktop permissions.\n\n" +
                    "Close Setup and launch ZFTP from the Start menu or desktop shortcut instead.",
                    "ZFTP installed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        Close();
    }

    /// <summary>
    /// Setup runs elevated, but ZFTP must run at the same integrity level as
    /// Explorer or its WinFsp drive letters are created in the wrong DOS-device
    /// namespace. Duplicate the shell's token instead of inheriting Setup's token.
    /// </summary>
    private static bool LaunchAsDesktopUser(string exe)
    {
        IntPtr shellToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        ProcessInformation pi = default;

        try
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            using var explorer = Process.GetProcessesByName("explorer")
                .FirstOrDefault(p => p.SessionId == sessionId);
            if (explorer == null) return false;

            if (!OpenProcessToken(explorer.Handle, TokenDuplicate | TokenQuery, out shellToken))
                return false;

            if (!DuplicateTokenEx(shellToken, MaximumAllowed, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
                return false;

            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder($"\"{exe}\"");
            var workingDirectory = Path.GetDirectoryName(exe);

            return CreateProcessWithTokenW(
                primaryToken,
                LogonWithProfile,
                exe,
                commandLine,
                0,
                IntPtr.Zero,
                workingDirectory,
                ref startup,
                out pi);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
        }
    }
}
