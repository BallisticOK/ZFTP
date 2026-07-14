// ============================================================================
//  ZFTP — ProfileStore
//  ---------------------------------------------------------------------------
//  Saves and loads your list of servers to:
//      %AppData%\ZFTP\drives.json
//
//  Passwords / key passphrases are NEVER written in plain text. They're
//  AES-GCM encrypted with a key that lives in the OS's native credential
//  vault (see SecretBox.cs) — Windows Credential Manager / macOS Keychain /
//  Linux Secret Service — so the ciphertext is tied to your account on any of
//  the three OSes, not just Windows.
// ============================================================================

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZFTP.Core;

public static class ProfileStore
{
    // ZFTP keeps all its data in its own folder under the user's AppData:
    //   %AppData%\ZFTP   (e.g. C:\Users\<you>\AppData\Roaming\ZFTP)
    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZFTP");

    public static string FilePath { get; } = Path.Combine(FolderPath, "drives.json");

    /// <summary>
    /// One-time recovery: if an older build left config in C:\ProgramData\ZFTP,
    /// move it into the AppData\ZFTP folder, then remove the ProgramData copy.
    /// </summary>
    public static void MigrateOldLocation()
    {
        try
        {
            var oldFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZFTP");
            if (!Directory.Exists(oldFolder) || string.Equals(oldFolder, FolderPath, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.CreateDirectory(FolderPath);
            foreach (var file in Directory.GetFiles(oldFolder))
            {
                var dest = Path.Combine(FolderPath, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
            try { Directory.Delete(oldFolder, recursive: true); } catch { /* may be locked; harmless */ }
        }
        catch { /* best effort — never block startup */ }
    }

    // Extra entropy mixed into the DPAPI encryption — a little defense in depth.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZFTP.v1.secret");

    /// <summary>What actually gets written to disk (secrets already encrypted).</summary>
    private sealed class StoredProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public AuthMethod Auth { get; set; }
        public string PasswordEnc { get; set; } = "";       // DPAPI ciphertext (base64)
        public string KeyPath { get; set; } = "";
        public string KeyPassphraseEnc { get; set; } = "";  // DPAPI ciphertext (base64)
        public string KnownHostKey { get; set; } = "";
        public string DeviceSerial { get; set; } = "";
        public string RemoteRoot { get; set; } = "/";
        public string DriveLetter { get; set; } = "Z";
        public bool Enabled { get; set; } = true;
        public bool AutoMount { get; set; }
        public AccessMode Access { get; set; } = AccessMode.ReadWrite;
        public ProviderType Provider { get; set; } = ProviderType.Sftp;
        public string Color { get; set; } = "#2D7DD2";
        public string Url { get; set; } = "";
        public string S3AccessKey { get; set; } = "";
        public string S3SecretEnc { get; set; } = "";   // DPAPI ciphertext (base64)
        public string S3Region { get; set; } = "";
        public string S3Endpoint { get; set; } = "";
        public string S3Bucket { get; set; } = "";
        public string SmbShare { get; set; } = "";
        public string SmbDomain { get; set; } = "";
        public string B2AccountId { get; set; } = "";
        public string B2ApplicationKeyEnc { get; set; } = "";  // DPAPI ciphertext (base64)
        public string B2Bucket { get; set; } = "";
        public string AzureAccount { get; set; } = "";
        public string AzureKeyEnc { get; set; } = "";  // DPAPI ciphertext (base64)
        public string AzureContainer { get; set; } = "";
        public string ProtonTwoFactorCodeEnc { get; set; } = "";     // DPAPI ciphertext (base64)
        public string ProtonMailboxPasswordEnc { get; set; } = "";   // DPAPI ciphertext (base64)
        public string ClientId { get; set; } = "";
        public string ClientSecretEnc { get; set; } = "";
    }

    public static void Save(IEnumerable<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(FolderPath);

        var stored = profiles.Select(p => new StoredProfile
        {
            Id = p.Id,
            Name = p.Name,
            Host = p.Host,
            Port = p.Port,
            Username = p.Username,
            Auth = p.Auth,
            PasswordEnc = Encrypt(p.Password),
            KeyPath = p.KeyPath,
            KeyPassphraseEnc = Encrypt(p.KeyPassphrase),
            KnownHostKey = p.KnownHostKey,
            DeviceSerial = p.DeviceSerial,
            RemoteRoot = p.RemoteRoot,
            DriveLetter = p.DriveLetter,
            Enabled = p.Enabled,
            AutoMount = p.AutoMount,
            Access = p.Access,
            Provider = p.Provider,
            Color = p.Color,
            Url = p.Url,
            S3AccessKey = p.S3AccessKey,
            S3SecretEnc = Encrypt(p.S3Secret),
            S3Region = p.S3Region,
            S3Endpoint = p.S3Endpoint,
            S3Bucket = p.S3Bucket,
            SmbShare = p.SmbShare,
            SmbDomain = p.SmbDomain,
            B2AccountId = p.B2AccountId,
            B2ApplicationKeyEnc = Encrypt(p.B2ApplicationKey),
            B2Bucket = p.B2Bucket,
            AzureAccount = p.AzureAccount,
            AzureKeyEnc = Encrypt(p.AzureKey),
            AzureContainer = p.AzureContainer,
            ProtonTwoFactorCodeEnc = Encrypt(p.ProtonTwoFactorCode),
            ProtonMailboxPasswordEnc = Encrypt(p.ProtonMailboxPassword),
            ClientId = p.ClientId,
            ClientSecretEnc = Encrypt(p.ClientSecret),
        }).ToList();

        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    public static List<ConnectionProfile> Load()
    {
        if (!File.Exists(FilePath)) return new List<ConnectionProfile>();

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredProfile>>(File.ReadAllText(FilePath))
                         ?? new List<StoredProfile>();

            return stored.Select(s => new ConnectionProfile
            {
                Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString("N") : s.Id,
                Name = s.Name,
                Host = s.Host,
                Port = s.Port,
                Username = s.Username,
                Auth = s.Auth,
                Password = Decrypt(s.PasswordEnc),
                KeyPath = s.KeyPath,
                KeyPassphrase = Decrypt(s.KeyPassphraseEnc),
                KnownHostKey = s.KnownHostKey,
                DeviceSerial = s.DeviceSerial,
                RemoteRoot = s.RemoteRoot,
                DriveLetter = s.DriveLetter,
                Enabled = s.Enabled,
                AutoMount = s.AutoMount,
                Access = s.Access,
                Provider = s.Provider,
                Color = string.IsNullOrWhiteSpace(s.Color) ? "#2D7DD2" : s.Color,
                Url = s.Url,
                S3AccessKey = s.S3AccessKey,
                S3Secret = Decrypt(s.S3SecretEnc),
                S3Region = s.S3Region,
                S3Endpoint = s.S3Endpoint,
                S3Bucket = s.S3Bucket,
                SmbShare = s.SmbShare,
                SmbDomain = s.SmbDomain,
                B2AccountId = s.B2AccountId,
                B2ApplicationKey = Decrypt(s.B2ApplicationKeyEnc),
                B2Bucket = s.B2Bucket,
                AzureAccount = s.AzureAccount,
                AzureKey = Decrypt(s.AzureKeyEnc),
                AzureContainer = s.AzureContainer,
                ProtonTwoFactorCode = Decrypt(s.ProtonTwoFactorCodeEnc),
                ProtonMailboxPassword = Decrypt(s.ProtonMailboxPasswordEnc),
                ClientId = s.ClientId,
                ClientSecret = Decrypt(s.ClientSecretEnc),
            }).ToList();
        }
        catch
        {
            // Corrupt/unreadable file — start fresh rather than crash.
            return new List<ConnectionProfile>();
        }
    }

    private static string Encrypt(string plain) => SecretBox.Encrypt(plain);

    private static string Decrypt(string cipher)
    {
        var viaCurrentScheme = SecretBox.TryDecrypt(cipher);
        if (viaCurrentScheme != null) return viaCurrentScheme;

        // Migration: profiles saved by older ZFTP builds were DPAPI-protected
        // (Windows-only). Still honor them so upgrading doesn't wipe saved
        // passwords - the next Save() re-encrypts through SecretBox instead.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { /* fall through */ }
        }
        return "";
    }
}
