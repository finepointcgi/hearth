using System.Security.Cryptography;
using System.Text;

namespace Hearth.Core.Services;

public sealed class SavedLogin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>What bots call it, e.g. "github" or "work email".</summary>
    public string Name { get; set; } = "";
    /// <summary>Site or app it's for, e.g. github.com — shown to bots.</summary>
    public string Site { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>AES-GCM: nonce(12) | tag(16) | ciphertext, base64.</summary>
    public string PasswordSealed { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Logins bots can fill in (browser or desktop) without the password ever going through the model or the chat.
/// Passwords are encrypted with a random key kept in the app data folder (owner-only permissions where the OS
/// supports it). That protects them from casual reading and from chat/log files, not from someone who can read
/// your user account's files.
/// </summary>
public sealed class LoginVault(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SavedLogin>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "logins.json");

    public event Action? Changed;

    public async Task<List<SavedLogin>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).OrderBy(l => l.Name).ToList(); }
        finally { _lock.Release(); }
    }

    public async Task<SavedLogin?> FindAsync(string nameOrSite)
    {
        var all = await ListAsync();
        var q = nameOrSite.Trim();
        return all.FirstOrDefault(l => l.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(l => l.Site.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(l => l.Site.Length > 0 && q.Contains(l.Site, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saves a login. Pass password = null to keep the existing one.</summary>
    public async Task SaveAsync(SavedLogin login, string? password)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            if (password is not null) login.PasswordSealed = Seal(password);
            login.UpdatedAt = DateTimeOffset.Now;
            var i = list.FindIndex(l => l.Id == login.Id);
            if (i >= 0) list[i] = login; else list.Add(login);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.RemoveAll(l => l.Id == id);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    /// <summary>Decrypts a password for a fill tool. Never return this to the model.</summary>
    public string RevealPassword(SavedLogin login) => login.PasswordSealed.Length == 0 ? "" : Open(login.PasswordSealed);

    private async Task<List<SavedLogin>> LoadAsync() => _cache ??= await Json.ReadAsync<List<SavedLogin>>(FilePath) ?? [];

    private readonly SecretBox _box = new(paths);
    private string Seal(string plain) => _box.Seal(plain);
    private string Open(string sealedText) => _box.Open(sealedText);
}
