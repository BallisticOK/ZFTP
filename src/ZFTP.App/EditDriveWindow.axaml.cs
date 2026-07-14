// ============================================================================
//  ZFTP.App — EditDriveWindow code-behind
//  Create/edit one drive. Shows the right fields for the chosen provider, and
//  for cloud drives runs rclone's browser sign-in.
// ============================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZFTP.Core;

namespace ZFTP.App;

public partial class EditDriveWindow : Window
{
    public ConnectionProfile Result { get; }

    // TypeCombo index <-> ProviderType
    private static readonly ProviderType[] ProviderOrder =
    {
        ProviderType.Sftp, ProviderType.Ftp, ProviderType.Ftps, ProviderType.WebDav,
        ProviderType.S3, ProviderType.GoogleDrive, ProviderType.Dropbox,
        ProviderType.OneDrive, ProviderType.Box,
        ProviderType.Smb, ProviderType.B2, ProviderType.Azure, ProviderType.Mega, ProviderType.Proton,
        ProviderType.Android, ProviderType.IPhone,
    };

    private static readonly (string Name, string Hex)[] Colors =
    {
        ("Blue", "#2D7DD2"), ("Purple", "#8B5CF6"), ("Green", "#22C55E"),
        ("Orange", "#F97316"), ("Red", "#EF4444"), ("Cyan", "#06B6D4"),
        ("Pink", "#EC4899"), ("Gold", "#F5B300"),
    };

    public EditDriveWindow(ConnectionProfile profile, IEnumerable<string> driveLetters)
    {
        InitializeComponent();
        Result = profile;

        if (OperatingSystem.IsWindows())
        {
            var want = (profile.DriveLetter ?? "Z").TrimEnd(':') + ":";
            var letters = driveLetters.Select(l => l.TrimEnd(':') + ":").ToList();
            if (!letters.Contains(want, StringComparer.OrdinalIgnoreCase)) letters.Insert(0, want);
            foreach (var l in letters) DriveCombo.Items.Add(l);
        }
        else
        {
            // No drive-letter concept here - a directory path instead (blank = auto
            // default under ~/ZFTP/mounts, resolved at mount time by MountTarget).
            DriveFieldLabel.Text = "Mount path";
            DriveCombo.IsVisible = false;
            MountPathBox.IsVisible = true;
        }

        BuildColorCombo();
        LoadForm(profile);
    }

    private void BuildColorCombo()
    {
        foreach (var (name, hex) in Colors)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new Rectangle
            {
                Width = 14, Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = Brush.Parse(hex),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            ColorCombo.Items.Add(new ComboBoxItem { Content = sp, Tag = hex });
        }
    }

    private void LoadForm(ConnectionProfile p)
    {
        TypeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ProviderOrder, p.Provider));
        NameBox.Text = p.Name;
        HostBox.Text = p.Host;
        PortBox.Text = p.Port.ToString();
        UserBox.Text = p.Username;
        UrlBox.Text = p.Url;
        AuthCombo.SelectedIndex = p.Auth == AuthMethod.PrivateKey ? 1 : 0;
        PasswordBox.Text = p.Password;
        KeyPathBox.Text = p.KeyPath;
        KeyPassBox.Text = p.KeyPassphrase;
        S3KeyBox.Text = p.S3AccessKey;
        S3SecretBox.Text = p.S3Secret;
        S3BucketBox.Text = p.S3Bucket;
        S3RegionBox.Text = p.S3Region;
        S3EndpointBox.Text = p.S3Endpoint;
        SmbShareBox.Text = p.SmbShare;
        SmbDomainBox.Text = p.SmbDomain;
        B2AccountBox.Text = p.B2AccountId;
        B2KeyBox.Text = p.B2ApplicationKey;
        B2BucketBox.Text = p.B2Bucket;
        AzureAccountBox.Text = p.AzureAccount;
        AzureKeyBox.Text = p.AzureKey;
        AzureContainerBox.Text = p.AzureContainer;
        ProtonTwoFactorBox.Text = p.ProtonTwoFactorCode;
        ProtonMailboxPasswordBox.Text = p.ProtonMailboxPassword;
        ClientIdBox.Text = p.ClientId;
        ClientSecretBox.Text = p.ClientSecret;
        RootBox.Text = string.IsNullOrEmpty(p.RemoteRoot) ? "/" : p.RemoteRoot;
        EnabledToggle.IsChecked = p.Enabled;
        AutoMountToggle.IsChecked = p.AutoMount;
        AccessCombo.SelectedIndex = p.Access == AccessMode.ReadOnly ? 1 : 0;
        MountPathBox.Text = p.MountPath;

        int ci = Array.FindIndex(Colors, c => c.Hex.Equals(p.Color, StringComparison.OrdinalIgnoreCase));
        ColorCombo.SelectedIndex = ci >= 0 ? ci : 0;

        if (OperatingSystem.IsWindows())
        {
            var want = (p.DriveLetter ?? "Z").TrimEnd(':') + ":";
            foreach (var obj in DriveCombo.Items)
                if (obj is string s && s.Equals(want, StringComparison.OrdinalIgnoreCase))
                { DriveCombo.SelectedItem = obj; break; }
            if (DriveCombo.SelectedIndex < 0 && DriveCombo.Items.Count > 0) DriveCombo.SelectedIndex = 0;
        }

        UpdateProviderVisibility();
    }

    private ProviderType SelectedProvider() =>
        ProviderOrder[Math.Max(0, TypeCombo.SelectedIndex)];

    private void TypeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PortBox == null) return; // during init
        var pt = SelectedProvider();
        // sensible default port when switching protocol
        if (pt is ProviderType.Ftp or ProviderType.Ftps && (PortBox.Text == "22" || PortBox.Text == "445" || string.IsNullOrWhiteSpace(PortBox.Text)))
            PortBox.Text = "21";
        else if (pt == ProviderType.Sftp && (PortBox.Text == "21" || PortBox.Text == "445" || string.IsNullOrWhiteSpace(PortBox.Text)))
            PortBox.Text = "22";
        else if (pt == ProviderType.Smb && (PortBox.Text == "21" || PortBox.Text == "22" || string.IsNullOrWhiteSpace(PortBox.Text)))
            PortBox.Text = "445";

        // Android shared storage lives under /sdcard; iPhone's AFC root is "/".
        if (RootBox != null && pt == ProviderType.Android &&
            (string.IsNullOrWhiteSpace(RootBox.Text) || RootBox.Text.Trim() == "/"))
            RootBox.Text = "/sdcard";
        else if (RootBox != null && pt == ProviderType.IPhone &&
            (string.IsNullOrWhiteSpace(RootBox.Text) || RootBox.Text.Trim() == "/sdcard"))
            RootBox.Text = "/";

        UpdateProviderVisibility();
    }

    private void UpdateProviderVisibility()
    {
        if (HostPanel == null) return;
        var pt = SelectedProvider();
        bool sftp = pt == ProviderType.Sftp;
        bool ftpish = pt is ProviderType.Ftp or ProviderType.Ftps;
        bool webdav = pt == ProviderType.WebDav;
        bool s3 = pt == ProviderType.S3;
        bool smb = pt == ProviderType.Smb;
        bool b2 = pt == ProviderType.B2;
        bool azure = pt == ProviderType.Azure;
        bool mega = pt == ProviderType.Mega;
        bool proton = pt == ProviderType.Proton;
        bool oauth = RcloneService.RequiresOAuth(pt);
        bool android = pt == ProviderType.Android;
        bool apple = pt == ProviderType.IPhone;

        HostPanel.IsVisible = sftp || ftpish || smb;
        UrlPanel.IsVisible = webdav;
        UserPanel.IsVisible = sftp || ftpish || webdav || smb || mega || proton;
        AuthPanel.IsVisible = sftp;
        S3Panel.IsVisible = s3;
        SmbPanel.IsVisible = smb;
        B2Panel.IsVisible = b2;
        AzurePanel.IsVisible = azure;
        ProtonPanel.IsVisible = proton;
        OAuthPanel.IsVisible = oauth;
        AndroidPanel.IsVisible = android;
        ApplePanel.IsVisible = apple;
        if (pt != ProviderType.OneDrive) { OnedriveDriveCard.IsVisible = false; _onedriveResumeState = null; }
        if (oauth) UpdateOAuthStatus();
        if (android) LoadDevices(Result.DeviceSerial);
        if (apple) LoadAppleDevices(Result.DeviceSerial);

        if (sftp)
            UpdateAuthVisibility();
        else
        {
            KeyPanel.IsVisible = false;
            PasswordPanel.IsVisible = ftpish || webdav || smb || mega || proton;
        }
    }

    private void AuthCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateAuthVisibility();

    private void UpdateAuthVisibility()
    {
        if (PasswordPanel == null || KeyPanel == null) return;
        if (SelectedProvider() != ProviderType.Sftp) return;
        bool useKey = AuthCombo.SelectedIndex == 1;
        PasswordPanel.IsVisible = !useKey;
        KeyPanel.IsVisible = useKey;
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select your SSH private key",
            AllowMultiple = false,
        });
        if (files.Count > 0) KeyPathBox.Text = files[0].TryGetLocalPath() ?? files[0].Name;
    }

    private async void SignIn_Click(object? sender, RoutedEventArgs e)
    {
        // Commit current fields to Result first so the rclone remote name is stable.
        ReadInto(Result);

        if (Result.Provider == ProviderType.OneDrive)
        {
            await SignInOnedriveAsync();
            return;
        }

        try
        {
            OAuthStatusText.Text = "Signing in… finish in the window and browser that opened.";
            SignInButton.IsEnabled = false;
            var proc = RcloneService.StartOAuthSetup(Result);
            await proc.WaitForExitAsync();   // completes once the sign-in finishes
            UpdateOAuthStatus();             // now shows "Signed in as …"
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync("ZFTP", "Couldn't start sign-in: " + ex.Message);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    // ---- OneDrive sign-in (needs its own flow - see RcloneService) --------

    private string? _onedriveResumeState;

    private async Task SignInOnedriveAsync()
    {
        OnedriveDriveCard.IsVisible = false;
        SignInButton.IsEnabled = false;
        try
        {
            OAuthStatusText.Text = "Signing in… finish in the browser window that opened.";
            var token = await RcloneService.AuthorizeOnedriveAsync(Result, url =>
                Dispatcher.UIThread.Post(() => OAuthStatusText.Text = "Browser didn't open? Copy this link: " + url));
            if (token == null)
            {
                OAuthStatusText.Text = "Not signed in.";
                await Dialogs.ShowMessageAsync("ZFTP", "Sign-in didn't complete. Try again.");
                return;
            }

            OAuthStatusText.Text = "Finding your OneDrive…";
            var wizard = await Task.Run(() => RcloneService.BeginOnedriveWizard(Result, token));
            if (wizard.Error != null)
            {
                OAuthStatusText.Text = "Not signed in.";
                await Dialogs.ShowMessageAsync("ZFTP", wizard.Error);
                return;
            }

            _onedriveResumeState = wizard.ResumeState;
            OnedriveDriveCombo.Items.Clear();
            foreach (var c in wizard.Choices!)
                OnedriveDriveCombo.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Value });
            OnedriveDriveCombo.SelectedIndex = RcloneService.GuessDefaultOnedriveDrive(wizard.Choices!);
            OnedriveDriveCard.IsVisible = true;
            OAuthStatusText.Text = "Pick your OneDrive below, then click \"Use this drive\".";
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync("ZFTP", "Couldn't start sign-in: " + ex.Message);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private async void OnedriveChoose_Click(object? sender, RoutedEventArgs e)
    {
        if (_onedriveResumeState == null || OnedriveDriveCombo.SelectedItem is not ComboBoxItem item) return;
        var driveId = item.Tag as string ?? "";

        OnedriveChooseButton.IsEnabled = false;
        try
        {
            OAuthStatusText.Text = "Finishing setup…";
            var result = await Task.Run(() => RcloneService.FinishOnedriveWizard(Result, _onedriveResumeState, driveId));
            if (result.Error != null)
            {
                await Dialogs.ShowMessageAsync("ZFTP", result.Error);
                OAuthStatusText.Text = "Not signed in.";
                return;
            }

            OnedriveDriveCard.IsVisible = false;
            _onedriveResumeState = null;
            UpdateOAuthStatus();   // now shows "Signed in as …"
        }
        finally
        {
            OnedriveChooseButton.IsEnabled = true;
        }
    }

    /// <summary>Reflect the real cloud sign-in state (and account) in the dialog.</summary>
    private async void UpdateOAuthStatus()
    {
        if (OAuthStatusText == null) return;

        if (!RcloneService.IsSignedIn(Result))
        {
            OAuthStatusText.Text = "Not signed in.";
            SignInButton.Content = "Sign in with browser";
            return;
        }

        OAuthStatusText.Text = "Signed in.";
        SignInButton.Content = "Sign in again";

        var account = await RcloneService.GetAccountAsync(Result);
        if (!string.IsNullOrEmpty(account) && RcloneService.IsSignedIn(Result))
            OAuthStatusText.Text = $"Signed in as {account}";
    }

    // ---- Android device picker --------------------------------------------

    private bool _loadingDevices;

    private string SelectedDeviceSerial() =>
        (DeviceCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void RefreshDevices_Click(object? sender, RoutedEventArgs e) => LoadDevices(SelectedDeviceSerial());

    /// <summary>Fill the device combo from `adb devices` (off the UI thread), keeping
    /// the wanted serial selected if it's present.</summary>
    private async void LoadDevices(string? desiredSerial)
    {
        if (DeviceCombo == null || _loadingDevices) return;
        _loadingDevices = true;
        var want = (desiredSerial ?? "").Trim();
        try
        {
            RefreshDevicesButton.IsEnabled = false;
            DeviceCombo.Items.Clear();
            DeviceCombo.Items.Add(new ComboBoxItem { Content = "Searching for devices...", Tag = "", IsEnabled = false });
            DeviceCombo.SelectedIndex = 0;

            if (!AdbService.Available)
            {
                DeviceCombo.Items.Clear();
                DeviceCombo.Items.Add(new ComboBoxItem { Content = "Android engine (adb) not found - reinstall ZFTP", Tag = "", IsEnabled = false });
                DeviceCombo.SelectedIndex = 0;
                return;
            }

            var devices = await Task.Run(() => { AdbService.EnsureServer(); return AdbService.ListDevices(); });

            DeviceCombo.Items.Clear();
            foreach (var d in devices)
                DeviceCombo.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Serial });

            // Keep a saved-but-currently-disconnected device selectable.
            if (!string.IsNullOrEmpty(want) &&
                !devices.Any(d => d.Serial.Equals(want, StringComparison.OrdinalIgnoreCase)))
                DeviceCombo.Items.Add(new ComboBoxItem { Content = want + " (not connected)", Tag = want });

            if (DeviceCombo.Items.Count == 0)
            {
                // adb might see the phone but stuck unauthorized/offline/blocked - say
                // exactly what's wrong instead of a generic "not detected".
                var why = await Task.Run(() => AdbService.ExplainNoReadyDevice())
                    ?? "No device detected - plug in and allow USB debugging";
                DeviceCombo.Items.Add(new ComboBoxItem { Content = why, Tag = "", IsEnabled = false });
                DeviceCombo.SelectedIndex = 0;
                return;
            }

            int sel = 0;
            if (want.Length > 0)
                for (int i = 0; i < DeviceCombo.Items.Count; i++)
                    if (DeviceCombo.Items[i] is ComboBoxItem it &&
                        (it.Tag as string ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
                    { sel = i; break; }
            DeviceCombo.SelectedIndex = sel;
        }
        catch { /* leave whatever is in the combo */ }
        finally { _loadingDevices = false; RefreshDevicesButton.IsEnabled = true; }
    }

    // ---- Apple (iPhone/iPad) device picker --------------------------------

    private bool _loadingAppleDevices;

    private string SelectedAppleSerial() =>
        (AppleDeviceCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void RefreshAppleDevices_Click(object? sender, RoutedEventArgs e) => LoadAppleDevices(SelectedAppleSerial());

    /// <summary>Fill the Apple device combo (off the UI thread), keeping the wanted
    /// UDID selected if it's present. Windows only - see the #else stub.</summary>
    private async void LoadAppleDevices(string? desiredUdid)
    {
        if (AppleDeviceCombo == null || _loadingAppleDevices) return;
        _loadingAppleDevices = true;
#if WINDOWS
        var want = (desiredUdid ?? "").Trim();
        try
        {
            RefreshAppleButton.IsEnabled = false;
            AppleDeviceCombo.Items.Clear();
            AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = "Searching for devices...", Tag = "", IsEnabled = false });
            AppleDeviceCombo.SelectedIndex = 0;

            if (!AppleDeviceService.Available)
            {
                AppleDeviceCombo.Items.Clear();
                AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = "Apple device engine unavailable - install Apple Devices / iTunes", Tag = "", IsEnabled = false });
                AppleDeviceCombo.SelectedIndex = 0;
                return;
            }

            var devices = await Task.Run(() => AppleDeviceService.ListDevices());

            AppleDeviceCombo.Items.Clear();
            foreach (var d in devices)
                AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Udid });

            if (!string.IsNullOrEmpty(want) &&
                !devices.Any(d => d.Udid.Equals(want, StringComparison.OrdinalIgnoreCase)))
                AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = want + " (not connected)", Tag = want });

            if (AppleDeviceCombo.Items.Count == 0)
            {
                AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = "No device detected - plug in, unlock, and tap Trust", Tag = "", IsEnabled = false });
                AppleDeviceCombo.SelectedIndex = 0;
                return;
            }

            int sel = 0;
            if (want.Length > 0)
                for (int i = 0; i < AppleDeviceCombo.Items.Count; i++)
                    if (AppleDeviceCombo.Items[i] is ComboBoxItem it &&
                        (it.Tag as string ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
                    { sel = i; break; }
            AppleDeviceCombo.SelectedIndex = sel;
        }
        catch { /* leave whatever is in the combo */ }
        finally { _loadingAppleDevices = false; RefreshAppleButton.IsEnabled = true; }
#else
        // No AppleDeviceService on this OS yet (iMobileDevice-net only ships
        // Windows binaries) - iPhone/iPad stays a Windows-only provider for now,
        // same as MountSession's PlatformNotSupportedException at mount time.
        await Task.Yield();
        AppleDeviceCombo.Items.Clear();
        AppleDeviceCombo.Items.Add(new ComboBoxItem { Content = "iPhone/iPad isn't supported on this OS yet", Tag = "", IsEnabled = false });
        AppleDeviceCombo.SelectedIndex = 0;
        RefreshAppleButton.IsEnabled = false;
        _loadingAppleDevices = false;
#endif
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var pt = SelectedProvider();

        // light validation per provider
        if ((pt is ProviderType.Sftp or ProviderType.Ftp or ProviderType.Ftps or ProviderType.Smb) && string.IsNullOrWhiteSpace(HostBox.Text))
        { await Warn("Please enter a host."); return; }
        if (pt == ProviderType.WebDav && string.IsNullOrWhiteSpace(UrlBox.Text))
        { await Warn("Please enter the WebDAV URL."); return; }
        if (pt == ProviderType.S3 && string.IsNullOrWhiteSpace(S3BucketBox.Text))
        { await Warn("Please enter the S3 bucket."); return; }
        if (pt == ProviderType.Smb && string.IsNullOrWhiteSpace(SmbShareBox.Text))
        { await Warn("Please enter the share name."); return; }
        if (pt == ProviderType.B2 && (string.IsNullOrWhiteSpace(B2AccountBox.Text) || string.IsNullOrWhiteSpace(B2BucketBox.Text)))
        { await Warn("Please enter the B2 Application Key ID and bucket."); return; }
        if (pt == ProviderType.Azure && (string.IsNullOrWhiteSpace(AzureAccountBox.Text) || string.IsNullOrWhiteSpace(AzureContainerBox.Text)))
        { await Warn("Please enter the Azure storage account and container."); return; }
        if (pt == ProviderType.Mega && string.IsNullOrWhiteSpace(UserBox.Text))
        { await Warn("Please enter your Mega account email."); return; }
        if (pt == ProviderType.Proton && string.IsNullOrWhiteSpace(UserBox.Text))
        { await Warn("Please enter your Proton account username."); return; }

        ReadInto(Result);
        Close(true);
    }

    private void ReadInto(ConnectionProfile p)
    {
        p.Provider = SelectedProvider();
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Server" : NameBox.Text!.Trim();
        p.Host = HostBox.Text?.Trim() ?? "";
        p.Port = int.TryParse(PortBox.Text, out var port) ? port
            : p.Provider is ProviderType.Ftp or ProviderType.Ftps ? 21
            : p.Provider == ProviderType.Smb ? 445
            : 22;
        p.Username = UserBox.Text?.Trim() ?? "";
        p.Url = UrlBox.Text?.Trim() ?? "";
        p.Auth = AuthCombo.SelectedIndex == 1 ? AuthMethod.PrivateKey : AuthMethod.Password;
        p.Password = PasswordBox.Text ?? "";
        p.KeyPath = KeyPathBox.Text?.Trim() ?? "";
        p.KeyPassphrase = KeyPassBox.Text ?? "";
        p.S3AccessKey = S3KeyBox.Text?.Trim() ?? "";
        p.S3Secret = S3SecretBox.Text ?? "";
        p.S3Bucket = S3BucketBox.Text?.Trim() ?? "";
        p.S3Region = S3RegionBox.Text?.Trim() ?? "";
        p.S3Endpoint = S3EndpointBox.Text?.Trim() ?? "";
        p.SmbShare = SmbShareBox.Text?.Trim() ?? "";
        p.SmbDomain = SmbDomainBox.Text?.Trim() ?? "";
        p.B2AccountId = B2AccountBox.Text?.Trim() ?? "";
        p.B2ApplicationKey = B2KeyBox.Text ?? "";
        p.B2Bucket = B2BucketBox.Text?.Trim() ?? "";
        p.AzureAccount = AzureAccountBox.Text?.Trim() ?? "";
        p.AzureKey = AzureKeyBox.Text ?? "";
        p.AzureContainer = AzureContainerBox.Text?.Trim() ?? "";
        p.ProtonTwoFactorCode = ProtonTwoFactorBox.Text?.Trim() ?? "";
        p.ProtonMailboxPassword = ProtonMailboxPasswordBox.Text ?? "";
        p.ClientId = ClientIdBox.Text?.Trim() ?? "";
        p.ClientSecret = ClientSecretBox.Text ?? "";
        if (p.Provider == ProviderType.Android) p.DeviceSerial = SelectedDeviceSerial();
        else if (p.Provider == ProviderType.IPhone) p.DeviceSerial = SelectedAppleSerial();
        p.RemoteRoot = string.IsNullOrWhiteSpace(RootBox.Text) ? "/" : RootBox.Text!.Trim();
        if (OperatingSystem.IsWindows())
            p.DriveLetter = (DriveCombo.SelectedItem as string ?? "Z:").TrimEnd(':');
        else
            p.MountPath = MountPathBox.Text?.Trim() ?? "";
        p.Access = AccessCombo.SelectedIndex == 1 ? AccessMode.ReadOnly : AccessMode.ReadWrite;
        if (ForgetHostKeyCheck.IsChecked == true) p.KnownHostKey = "";   // re-trust on next connect
        p.Enabled = EnabledToggle.IsChecked == true;
        p.AutoMount = AutoMountToggle.IsChecked == true;
        p.Color = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#2D7DD2";
    }

    private static Task Warn(string msg) => Dialogs.ShowMessageAsync("ZFTP", msg);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
