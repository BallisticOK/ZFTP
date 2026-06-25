// ============================================================================
//  ZFTP — ConnectionProfile
//  A saved server: everything ZFTP needs to connect and where to mount it.
//  This is what we'll later save to disk so your servers persist between runs.
// ============================================================================

namespace ZFTP.Core;

public enum AuthMethod
{
    Password,
    PrivateKey,
}

public enum AccessMode
{
    ReadWrite,   // default — full read & write
    ReadOnly,    // browse/copy out only; writes are blocked
}

/// <summary>
/// Which kind of storage this drive connects to. SFTP uses ZFTP's own native
/// engine; everything else is mounted through the bundled rclone engine.
/// </summary>
public enum ProviderType
{
    Sftp,          // native engine
    Ftp,           // rclone
    Ftps,          // rclone (FTP over TLS)
    WebDav,        // rclone
    S3,            // rclone (Amazon S3 + compatible: Wasabi, B2, DO Spaces, ...)
    GoogleDrive,   // rclone (browser sign-in)
    Dropbox,       // rclone (browser sign-in)
    OneDrive,      // rclone (browser sign-in)
    Box,           // rclone (browser sign-in)
    Android,       // native engine (adb over USB)
    IPhone,        // native engine (Apple AFC over USB) - LIMITED: photos + file-sharing apps only
}

public sealed class ConnectionProfile
{
    /// <summary>Stable unique id so the app can track a profile across edits/saves.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friendly name shown in the app AND as the drive label in Explorer.</summary>
    public string Name { get; set; } = "New Server";

    /// <summary>What kind of storage this is. SFTP = native; others go through rclone.</summary>
    public ProviderType Provider { get; set; } = ProviderType.Sftp;

    /// <summary>Accent colour for this drive in the list, as a hex string like "#2D7DD2".</summary>
    public string Color { get; set; } = "#2D7DD2";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    // NOTE: for now the password lives here in memory. A later step moves it
    // into the Windows Credential Manager so it's never stored in plain text.
    public string Password { get; set; } = "";

    public string KeyPath { get; set; } = "";
    public string KeyPassphrase { get; set; } = "";

    /// <summary>SFTP server's host-key fingerprint (SHA-256), remembered on first
    /// connect. If it ever changes, the connection is refused (possible MITM).</summary>
    public string KnownHostKey { get; set; } = "";

    /// <summary>Remote folder to show as the drive root. "/" means "my home folder".</summary>
    public string RemoteRoot { get; set; } = "/";

    // ---- provider-specific (only used by the relevant Provider) ------------

    /// <summary>WebDAV server URL, e.g. https://dav.example.com/remote.php/webdav.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional personal OAuth app credentials (cloud drives). Blank = use
    /// rclone's built-in sign-in. Providing your own makes logins permanent.</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>S3-compatible settings.</summary>
    public string S3AccessKey { get; set; } = "";
    public string S3Secret { get; set; } = "";
    public string S3Region { get; set; } = "";
    public string S3Endpoint { get; set; } = "";   // blank = Amazon AWS; set for Wasabi/B2/DO/etc.
    public string S3Bucket { get; set; } = "";

    /// <summary>ADB serial of the chosen Android device (Provider == Android). Blank
    /// means "use the only connected device" and is resolved at mount time.</summary>
    public string DeviceSerial { get; set; } = "";

    /// <summary>Drive letter to mount on, e.g. "Z".</summary>
    public string DriveLetter { get; set; } = "Z";

    /// <summary>Read &amp; write (default) or read-only.</summary>
    public AccessMode Access { get; set; } = AccessMode.ReadWrite;

    /// <summary>If false, this drive is shown but skipped by "Mount all" / auto-mount.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Mount this drive automatically when ZFTP starts.</summary>
    public bool AutoMount { get; set; } = false;

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    /// <summary>Copy every editable field from another profile (keeps this object's identity).</summary>
    public void CopyFrom(ConnectionProfile o)
    {
        Name = o.Name; Host = o.Host; Port = o.Port; Username = o.Username;
        Auth = o.Auth; Password = o.Password; KeyPath = o.KeyPath; KeyPassphrase = o.KeyPassphrase;
        KnownHostKey = o.KnownHostKey;
        DeviceSerial = o.DeviceSerial;
        RemoteRoot = o.RemoteRoot; DriveLetter = o.DriveLetter; Enabled = o.Enabled; AutoMount = o.AutoMount;
        Access = o.Access; Provider = o.Provider; Color = o.Color;
        Url = o.Url; S3AccessKey = o.S3AccessKey; S3Secret = o.S3Secret;
        S3Region = o.S3Region; S3Endpoint = o.S3Endpoint; S3Bucket = o.S3Bucket;
        ClientId = o.ClientId; ClientSecret = o.ClientSecret;
    }
}
