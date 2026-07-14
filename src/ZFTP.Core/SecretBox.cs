// ============================================================================
//  ZFTP — SecretBox
//  ---------------------------------------------------------------------------
//  Cross-platform replacement for the old DPAPI-only encryption in
//  ProfileStore. A single random AES-256 key is generated once and stored in
//  the OS's native credential vault (Windows Credential Manager / macOS
//  Keychain / Linux Secret Service) via Devlooped.CredentialManager - the same
//  cross-platform store git-credential-manager itself uses. Every saved
//  profile secret is then AES-GCM-encrypted locally with that one key, so the
//  actual profile data still lives in one plain drives.json file; only the
//  key that unlocks it is OS-vault-protected.
//
//  If the OS vault is unavailable (e.g. headless Linux with no Secret Service
//  daemon running), falls back to a key file next to drives.json, protected
//  only by filesystem permissions - degraded, but keeps the app working.
// ============================================================================

using System.Security.Cryptography;
using System.Text;
using GitCredentialManager;

namespace ZFTP.Core;

internal static class SecretBox
{
    private const string TargetUri = "zftp://profile-secrets";
    private const string Account = "ZFTP";

    private static readonly Lazy<byte[]> Key = new(LoadOrCreateKey);

    public static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipherBytes = new byte[plainBytes.Length];

        using var aes = new AesGcm(Key.Value, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var packed = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, packed, nonce.Length + tag.Length, cipherBytes.Length);
        return Convert.ToBase64String(packed);
    }

    /// <summary>Null (not "") on failure, so callers can fall back to trying an
    /// older encryption scheme instead of treating a failed decrypt as "blank".</summary>
    public static string? TryDecrypt(string cipherB64)
    {
        if (string.IsNullOrEmpty(cipherB64)) return "";
        try
        {
            var packed = Convert.FromBase64String(cipherB64);
            int nonceLen = AesGcm.NonceByteSizes.MaxSize, tagLen = AesGcm.TagByteSizes.MaxSize;
            if (packed.Length < nonceLen + tagLen) return null;

            var nonce = packed.AsSpan(0, nonceLen);
            var tag = packed.AsSpan(nonceLen, tagLen);
            var cipherBytes = packed.AsSpan(nonceLen + tagLen);
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(Key.Value, tagLen);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] LoadOrCreateKey()
    {
        try
        {
            var store = CredentialManager.Create("ZFTP");
            var existing = TryGetKeyFromStore(store);
            if (existing != null) return existing;

            var key = RandomNumberGenerator.GetBytes(32);
            store.AddOrUpdate(TargetUri, Account, Convert.ToBase64String(key));
            return key;
        }
        catch
        {
            // OS credential vault unavailable - degrade to a key file instead
            // of failing to load/save profiles at all.
            return LoadOrCreateFallbackKeyFile();
        }
    }

    private static byte[]? TryGetKeyFromStore(ICredentialStore store)
    {
        try
        {
            var cred = store.Get(TargetUri, Account);
            if (cred?.Password is { Length: > 0 } b64) return Convert.FromBase64String(b64);
        }
        catch { /* no existing entry, or store errored reading it - fall through */ }
        return null;
    }

    private static byte[] LoadOrCreateFallbackKeyFile()
    {
        var path = Path.Combine(ProfileStore.FolderPath, ".secretkey");
        try
        {
            if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        catch { /* corrupt/unreadable - regenerate below */ }

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(ProfileStore.FolderPath);
            File.WriteAllText(path, Convert.ToBase64String(key));
        }
        catch { /* best effort - key still works for this run */ }
        return key;
    }
}
