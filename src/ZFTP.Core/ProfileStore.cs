// ============================================================================
//  ZFTP — ProfileStore
//  ---------------------------------------------------------------------------
//  Saves and loads your list of servers to:
//      %AppData%\ZFTP\drives.json
//
//  Passwords / key passphrases are NEVER written in plain text. They're
//  encrypted with Windows DPAPI (ProtectedData), which ties the ciphertext to
//  your Windows user account — another user (or another PC) can't decrypt it.
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

    private static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Decrypt(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
