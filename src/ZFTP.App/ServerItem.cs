// ============================================================================
//  ZFTP.App — ServerItem
//  One row in the drives table. Wraps a ConnectionProfile plus its live
//  MountSession, and exposes everything the drives list binds to.
// ============================================================================

using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using ZFTP.Core;

namespace ZFTP.App;

public sealed class ServerItem : INotifyPropertyChanged
{
    public ConnectionProfile Profile { get; }
    public MountSession Session { get; }

    public ServerItem(ConnectionProfile profile)
    {
        Profile = profile;
        Session = new MountSession(profile);
        Session.StateChanged += _ => RaiseStatusChanged();
    }

    // ---- columns bound by the drives list ----------------------------------
    public string Name => Profile.Name;
    public string Host => Profile.Provider is ProviderType.Android or ProviderType.IPhone
        ? (string.IsNullOrEmpty(Profile.DeviceSerial) ? "USB device" : Profile.DeviceSerial)
        : (string.IsNullOrEmpty(Profile.Host) ? Profile.Url : Profile.Host);
    public string RemoteRoot => string.IsNullOrEmpty(Profile.RemoteRoot) ? "/" : Profile.RemoteRoot;

    /// <summary>Drive letter on Windows; the mount directory (or "(auto)" if
    /// unset) on Linux/macOS, where there's no drive-letter concept.</summary>
    public string DriveLabel => OperatingSystem.IsWindows()
        ? Profile.DriveLetter.TrimEnd(':') + ":"
        : (string.IsNullOrEmpty(Profile.MountPath) ? "(auto)" : Profile.MountPath);

    /// <summary>Friendly provider name shown under the server name.</summary>
    public string ProviderLabel => Profile.Provider switch
    {
        ProviderType.Sftp => "SFTP",
        ProviderType.Ftp => "FTP",
        ProviderType.Ftps => "FTPS",
        ProviderType.WebDav => "WebDAV",
        ProviderType.S3 => "S3",
        ProviderType.GoogleDrive => "Google Drive",
        ProviderType.Dropbox => "Dropbox",
        ProviderType.OneDrive => "OneDrive",
        ProviderType.Box => "Box",
        ProviderType.Smb => "SMB",
        ProviderType.B2 => "Backblaze B2",
        ProviderType.Azure => "Azure Blob",
        ProviderType.Mega => "Mega.nz",
        ProviderType.Proton => "Proton Drive",
        ProviderType.Android => "Android (USB)",
        ProviderType.IPhone => "iPhone/iPad (USB)",
        _ => "",
    };

    /// <summary>Per-server accent colour (from the profile).</summary>
    public IBrush TagBrush
    {
        get
        {
            try { return Brush.Parse(Profile.Color); }
            catch { return Brushes.SteelBlue; }
        }
    }

    public bool Enabled
    {
        get => Profile.Enabled;
        set { Profile.Enabled = value; OnChanged(nameof(Enabled)); }
    }

    public bool IsMounted => Session.IsMounted;

    public string StatusText => Session.State switch
    {
        MountState.Mounted => "Running",
        MountState.Connecting => "Connecting…",
        MountState.Reconnecting => "Reconnecting…",
        MountState.Error => "Error",
        _ => "Stopped",
    };

    public string StatusDetail => Session.State switch
    {
        MountState.Mounted => $"Mounted on {Session.MountPoint}",
        MountState.Connecting => "Connecting…",
        MountState.Error => "Error: " + (Session.LastError ?? "unknown"),
        _ => "Not connected",
    };

    public IBrush StatusColor => Session.State switch
    {
        MountState.Mounted => Brushes.LimeGreen,
        MountState.Connecting => Brushes.Goldenrod,
        MountState.Reconnecting => Brushes.Goldenrod,
        MountState.Error => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    private void RaiseStatusChanged()
    {
        OnChanged(nameof(StatusText));
        OnChanged(nameof(StatusDetail));
        OnChanged(nameof(StatusColor));
        OnChanged(nameof(IsMounted));
    }

    /// <summary>Call after the profile is edited so every column refreshes.</summary>
    public void RefreshAll()
    {
        OnChanged(nameof(Name));
        OnChanged(nameof(Host));
        OnChanged(nameof(RemoteRoot));
        OnChanged(nameof(DriveLabel));
        OnChanged(nameof(Enabled));
        OnChanged(nameof(ProviderLabel));
        OnChanged(nameof(TagBrush));
        RaiseStatusChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            // Post (async) — never block the caller's thread (e.g. the reconnect
            // watchdog holding a lock) waiting on the UI thread.
            Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
