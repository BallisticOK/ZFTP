// ============================================================================
//  ZFTP.App — EditDriveWindow code-behind
//  Create/edit one drive. Shows the right fields for the chosen provider, and
//  for cloud drives runs rclone's browser sign-in.
// ============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using ZFTP.Core;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Brush = System.Windows.Media.Brush;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ZFTP.App;

public partial class EditDriveWindow : FluentWindow
{
    public ConnectionProfile Result { get; }

    // TypeCombo index <-> ProviderType
    private static readonly ProviderType[] ProviderOrder =
    {
        ProviderType.Sftp, ProviderType.Ftp, ProviderType.Ftps, ProviderType.WebDav,
        ProviderType.S3, ProviderType.GoogleDrive, ProviderType.Dropbox,
        ProviderType.OneDrive, ProviderType.Box,
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

        var want = (profile.DriveLetter ?? "Z").TrimEnd(':') + ":";
        var letters = driveLetters.Select(l => l.TrimEnd(':') + ":").ToList();
        if (!letters.Contains(want, StringComparer.OrdinalIgnoreCase)) letters.Insert(0, want);
        foreach (var l in letters) DriveCombo.Items.Add(l);

        BuildColorCombo();
        LoadForm(profile);
    }

    private void BuildColorCombo()
    {
        foreach (var (name, hex) in Colors)
        {
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 14, Height = 14, RadiusX = 3, RadiusY = 3,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = (Brush)new BrushConverter().ConvertFromString(hex)!,
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
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
        PasswordBox.Password = p.Password;
        KeyPathBox.Text = p.KeyPath;
        KeyPassBox.Password = p.KeyPassphrase;
        S3KeyBox.Text = p.S3AccessKey;
        S3SecretBox.Password = p.S3Secret;
        S3BucketBox.Text = p.S3Bucket;
        S3RegionBox.Text = p.S3Region;
        S3EndpointBox.Text = p.S3Endpoint;
        ClientIdBox.Text = p.ClientId;
        ClientSecretBox.Password = p.ClientSecret;
        RootBox.Text = string.IsNullOrEmpty(p.RemoteRoot) ? "/" : p.RemoteRoot;
        EnabledToggle.IsChecked = p.Enabled;
        AutoMountToggle.IsChecked = p.AutoMount;
        AccessCombo.SelectedIndex = p.Access == AccessMode.ReadOnly ? 1 : 0;

        int ci = Array.FindIndex(Colors, c => c.Hex.Equals(p.Color, StringComparison.OrdinalIgnoreCase));
        ColorCombo.SelectedIndex = ci >= 0 ? ci : 0;

        foreach (var obj in DriveCombo.Items)
            if (obj is string s && s.Equals((p.DriveLetter ?? "Z").TrimEnd(':') + ":", StringComparison.OrdinalIgnoreCase))
            { DriveCombo.SelectedItem = obj; break; }
        if (DriveCombo.SelectedIndex < 0 && DriveCombo.Items.Count > 0) DriveCombo.SelectedIndex = 0;

        UpdateProviderVisibility();
    }

    private ProviderType SelectedProvider() =>
        ProviderOrder[Math.Max(0, TypeCombo.SelectedIndex)];

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox == null) return; // during init
        var pt = SelectedProvider();
        // sensible default port when switching protocol
        if (pt is ProviderType.Ftp or ProviderType.Ftps && (PortBox.Text == "22" || string.IsNullOrWhiteSpace(PortBox.Text)))
            PortBox.Text = "21";
        else if (pt == ProviderType.Sftp && (PortBox.Text == "21" || string.IsNullOrWhiteSpace(PortBox.Text)))
            PortBox.Text = "22";
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
        bool oauth = RcloneService.RequiresOAuth(pt);

        HostPanel.Visibility = (sftp || ftpish) ? Visibility.Visible : Visibility.Collapsed;
        UrlPanel.Visibility = webdav ? Visibility.Visible : Visibility.Collapsed;
        UserPanel.Visibility = (sftp || ftpish || webdav) ? Visibility.Visible : Visibility.Collapsed;
        AuthPanel.Visibility = sftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = s3 ? Visibility.Visible : Visibility.Collapsed;
        OAuthPanel.Visibility = oauth ? Visibility.Visible : Visibility.Collapsed;
        if (oauth) UpdateOAuthStatus();

        if (sftp)
            UpdateAuthVisibility();
        else
        {
            KeyPanel.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = (ftpish || webdav) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthVisibility();

    private void UpdateAuthVisibility()
    {
        if (PasswordPanel == null || KeyPanel == null) return;
        if (SelectedProvider() != ProviderType.Sftp) return;
        bool useKey = AuthCombo.SelectedIndex == 1;
        PasswordPanel.Visibility = useKey ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = useKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Select your SSH private key" };
        if (dlg.ShowDialog() == true) KeyPathBox.Text = dlg.FileName;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        // Commit current fields to Result first so the rclone remote name is stable.
        ReadInto(Result);
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
            MessageBox.Show("Couldn't start sign-in: " + ex.Message, "ZFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SignInButton.IsEnabled = true;
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var pt = SelectedProvider();

        // light validation per provider
        if ((pt is ProviderType.Sftp or ProviderType.Ftp or ProviderType.Ftps) && string.IsNullOrWhiteSpace(HostBox.Text))
        { Warn("Please enter a host."); return; }
        if (pt == ProviderType.WebDav && string.IsNullOrWhiteSpace(UrlBox.Text))
        { Warn("Please enter the WebDAV URL."); return; }
        if (pt == ProviderType.S3 && string.IsNullOrWhiteSpace(S3BucketBox.Text))
        { Warn("Please enter the S3 bucket."); return; }

        ReadInto(Result);
        DialogResult = true;
    }

    private void ReadInto(ConnectionProfile p)
    {
        p.Provider = SelectedProvider();
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Server" : NameBox.Text.Trim();
        p.Host = HostBox.Text.Trim();
        p.Port = int.TryParse(PortBox.Text, out var port) ? port : (p.Provider is ProviderType.Ftp or ProviderType.Ftps ? 21 : 22);
        p.Username = UserBox.Text.Trim();
        p.Url = UrlBox.Text.Trim();
        p.Auth = AuthCombo.SelectedIndex == 1 ? AuthMethod.PrivateKey : AuthMethod.Password;
        p.Password = PasswordBox.Password;
        p.KeyPath = KeyPathBox.Text.Trim();
        p.KeyPassphrase = KeyPassBox.Password;
        p.S3AccessKey = S3KeyBox.Text.Trim();
        p.S3Secret = S3SecretBox.Password;
        p.S3Bucket = S3BucketBox.Text.Trim();
        p.S3Region = S3RegionBox.Text.Trim();
        p.S3Endpoint = S3EndpointBox.Text.Trim();
        p.ClientId = ClientIdBox.Text.Trim();
        p.ClientSecret = ClientSecretBox.Password;
        p.RemoteRoot = string.IsNullOrWhiteSpace(RootBox.Text) ? "/" : RootBox.Text.Trim();
        p.DriveLetter = (DriveCombo.SelectedItem as string ?? "Z:").TrimEnd(':');
        p.Access = AccessCombo.SelectedIndex == 1 ? AccessMode.ReadOnly : AccessMode.ReadWrite;
        if (ForgetHostKeyCheck.IsChecked == true) p.KnownHostKey = "";   // re-trust on next connect
        p.Enabled = EnabledToggle.IsChecked == true;
        p.AutoMount = AutoMountToggle.IsChecked == true;
        p.Color = (ColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "#2D7DD2";
    }

    private static void Warn(string msg) =>
        MessageBox.Show(msg, "ZFTP", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
