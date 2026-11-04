using System.Security.Cryptography;
using System.Text;

namespace Locus.Core.Services;

/// <summary>
/// Encrypts small secrets (passwords, OAuth tokens, API keys) with AES-GCM using a random key kept in the app data
/// folder with owner-only permissions. Keeps secrets out of settings, chats and logs; it does not protect them
/// from someone who can read your user account's files.
/// </summary>
public sealed class SecretBox(IAppPaths paths)
{
    private byte[]? _key;
    private string KeyPath => Path.Combine(paths.DataDirectory, "vault.key");

    private byte[] Key()
    {
        if (_key is not null) return _key;
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            // CreateNew so two instances racing on first run can't end up with different keys.
            var key = RandomNumberGenerator.GetBytes(32);
            using (var fs = new FileStream(KeyPath, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs)) w.Write(Convert.ToBase64String(key));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) when (File.Exists(KeyPath)) { /* already created */ }
        return _key = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
    }

    /// <summary>nonce(12) | tag(16) | ciphertext, base64.</summary>
    public string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Open(string sealedText)
    {
        if (string.IsNullOrEmpty(sealedText)) return "";
        var all = Convert.FromBase64String(sealedText);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
