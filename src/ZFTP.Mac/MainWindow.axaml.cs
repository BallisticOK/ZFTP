using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZFTP.Portable;

namespace ZFTP.Mac;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PortableProfile> _profiles = [];

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (!OperatingSystem.IsMacOS())
            SetStatus("This GUI is the macOS build. The existing WPF app remains the Windows GUI.");

        ReloadProfiles();
        await RefreshRemotesAsync();

        foreach (var profile in _profiles.Where(p => p.AutoMount))
        {
            var result = await RcloneMountService.MountAsync(profile);
            if (!result.Success) SetStatus(result.Output);
        }
    }

    private void ReloadProfiles()
    {
        _profiles.Clear();
        foreach (var profile in PortableProfileStore.Load()) _profiles.Add(profile);
    }

    private async Task RefreshRemotesAsync()
    {
        try
        {
            var remotes = await RcloneMountService.ListRemotesAsync();
            RemoteBox.ItemsSource = remotes;
            if (RemoteBox.SelectedIndex < 0 && remotes.Count > 0) RemoteBox.SelectedIndex = 0;
            SetStatus(remotes.Count == 0 ? "No rclone remotes found. Use Setup help to get started." : $"Ready - {remotes.Count} rclone remote(s) available.");
        }
        catch (Exception ex)
        {
            SetStatus($"rclone is not ready: {ex.Message}");
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        ReloadProfiles();
        await RefreshRemotesAsync();
    }

    private void SetupHelp_Click(object? sender, RoutedEventArgs e)
    {
        SetStatus("Install rclone and macFUSE, run 'rclone config' in Terminal, then click Refresh. ZFTP will detect the new remote.");
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var remote = RemoteBox.SelectedItem?.ToString()?.Trim() ?? "";
        if (name.Length == 0 || remote.Length == 0)
        {
            SetStatus("A drive name and rclone remote are required.");
            return;
        }

        if (_profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A drive named '{name}' already exists.");
            return;
        }

        var profile = new PortableProfile
        {
            Name = name,
            RemoteName = remote.TrimEnd(':'),
            RemotePath = RemotePathBox.Text?.Trim() ?? "",
            MountPath = RcloneMountService.ExpandHome(string.IsNullOrWhiteSpace(MountPathBox.Text)
                ? PortableProfileStore.DefaultMountPath(name)
                : MountPathBox.Text.Trim()),
            ReadOnly = ReadOnlyBox.IsChecked == true,
            AutoMount = AutoMountBox.IsChecked == true,
        };
        _profiles.Add(profile);
        SaveProfiles();
        ProfilesList.SelectedItem = profile;
        NameBox.Text = "";
        RemotePathBox.Text = "";
        MountPathBox.Text = "";
        ReadOnlyBox.IsChecked = false;
        AutoMountBox.IsChecked = false;
        SetStatus($"Added {profile.Name}.");
    }

    private async void Mount_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not PortableProfile profile) { SetStatus("Choose a drive first."); return; }
        SetStatus($"Mounting {profile.Name}...");
        var result = await RcloneMountService.MountAsync(profile);
        SetStatus(result.Output);
    }

    private async void Unmount_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not PortableProfile profile) { SetStatus("Choose a drive first."); return; }
        var result = await RcloneMountService.UnmountAsync(profile);
        SetStatus(result.Output);
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not PortableProfile profile) { SetStatus("Choose a drive first."); return; }
        if (!await RcloneMountService.IsMountedAsync(ResolvedMount(profile)))
        {
            SetStatus($"{profile.Name} is not mounted yet.");
            return;
        }
        var result = await RcloneMountService.OpenMountAsync(profile);
        if (!result.Success) SetStatus(result.Output);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not PortableProfile profile) { SetStatus("Choose a drive first."); return; }
        if (await RcloneMountService.IsMountedAsync(ResolvedMount(profile)))
        {
            SetStatus("Unmount the drive before deleting its ZFTP profile.");
            return;
        }
        _profiles.Remove(profile);
        SaveProfiles();
        SetStatus($"Deleted {profile.Name}. The rclone remote was kept.");
    }

    private void ProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is PortableProfile profile)
            SetStatus($"{profile.Name}: {profile.RemoteSpec} -> {ResolvedMount(profile)}");
    }

    private static string ResolvedMount(PortableProfile profile) => RcloneMountService.ExpandHome(
        string.IsNullOrWhiteSpace(profile.MountPath) ? PortableProfileStore.DefaultMountPath(profile.Name) : profile.MountPath);

    private void SaveProfiles() => PortableProfileStore.Save(_profiles);
    private void SetStatus(string text) => StatusText.Text = text;
}
