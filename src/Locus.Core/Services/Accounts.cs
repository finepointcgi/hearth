using System.Security.Cryptography;
using System.Text;

namespace Locus.Core.Services;

/// <summary>Someone with their own Locus: their own bots, chats, memories, documents and connections.</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>PBKDF2-SHA256 of the password (base64), with <see cref="Salt"/>.</summary>
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    /// <summary>The first account: it keeps the original data folder and manages the others.</summary>
    public bool IsOwner { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AccountsConfig
{
    /// <summary>Separate accounts are on: each person signs in with their name and password.</summary>
    public bool Enabled { get; set; }
    public List<Account> Accounts { get; set; } = [];
    /// <summary>Signs sign-in cookies (random, made once).</summary>
    public string CookieKey { get; set; } = "";
}

/// <summary>Who is using this page: the signed-in account (null when accounts are off).</summary>
public interface ICurrentUser
{
    Account? Account { get; }
}

/// <summary>
/// Accounts (accounts.json in the main data folder). Optional: while off, Locus is one person's, as before. When on,
/// the owner keeps the original data folder and everyone else gets their own under accounts/&lt;id&gt;.
/// </summary>
public sealed class AccountStore(string rootDataDirectory)
{
    private const int Iterations = 120_000;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccountsConfig? _cache;

    public string RootDataDirectory => rootDataDirectory;
    private string FilePath => Path.Combine(rootDataDirectory, "accounts.json");

    public event Action? Changed;

    public AccountsConfig Get()
    {
        if (_cache is not null) return _cache;
        _lock.Wait();
        try { return _cache ??= Load(); }
        finally { _lock.Release(); }
    }

    private AccountsConfig Load()
    {
        var c = File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<AccountsConfig>(File.ReadAllText(FilePath), Json.Options) ?? new() : new();
        if (c.CookieKey.Length == 0) c.CookieKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return c;
    }

    public bool Enabled => Get() is { Enabled: true, Accounts.Count: > 0 };
    public Account? Owner => Get().Accounts.FirstOrDefault(a => a.IsOwner);
    public Account? Find(string id) => Get().Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>Where an account's data lives: the owner keeps the original folder.</summary>
    public string DataDirectoryFor(Account a) => a.IsOwner ? rootDataDirectory : Path.Combine(rootDataDirectory, "accounts", a.Id);

    private async Task UpdateAsync(Action<AccountsConfig> change)
    {
        await _lock.WaitAsync();
        try
        {
            var c = _cache ??= Load();
            change(c);
            await Json.WriteAtomicAsync(FilePath, c);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public static string? Problem(string name, string password, IEnumerable<Account> existing, string? exceptId = null)
    {
        if (name.Trim().Length is < 1 or > 40) return "Give a name (up to 40 characters).";
        if (existing.Any(a => a.Id != exceptId && a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))) return $"There's already an account called {name.Trim()}.";
        if (password.Length < 8) return "Use a password of at least 8 characters.";
        return null;
    }

    /// <summary>Turns accounts on with the owner's name and password (the current data becomes theirs).</summary>
    public async Task<string?> EnableAsync(string ownerName, string ownerPassword)
    {
        var c = Get();
        if (c.Accounts.Count == 0)
        {
            if (Problem(ownerName, ownerPassword, []) is { } p) return p;
            var owner = new Account { Name = ownerName.Trim(), IsOwner = true };
            SetPassword(owner, ownerPassword);
            await UpdateAsync(x => { x.Accounts.Add(owner); x.Enabled = true; });
        }
        else await UpdateAsync(x => x.Enabled = true);
        return null;
    }

    /// <summary>Back to one person: everyone else's data stays in its folder for when accounts are turned on again.</summary>
    public Task DisableAsync() => UpdateAsync(x => x.Enabled = false);

    public async Task<(Account? account, string? problem)> AddAsync(string name, string password)
    {
        if (Problem(name, password, Get().Accounts) is { } p) return (null, p);
        var a = new Account { Name = name.Trim() };
        SetPassword(a, password);
        await UpdateAsync(x => x.Accounts.Add(a));
        return (a, null);
    }

    public async Task<string?> ChangePasswordAsync(string id, string password)
    {
        if (password.Length < 8) return "Use a password of at least 8 characters.";
        await UpdateAsync(x => { if (x.Accounts.FirstOrDefault(a => a.Id == id) is { } a) SetPassword(a, password); });
        return null;
    }

    public async Task<string?> RenameAsync(string id, string name)
    {
        if (Problem(name, "longenough", Get().Accounts, id) is { } p) return p;
        await UpdateAsync(x => { if (x.Accounts.FirstOrDefault(a => a.Id == id) is { } a) a.Name = name.Trim(); });
        return null;
    }

    /// <summary>Removes an account (never the owner). Its data folder is left on disk.</summary>
    public Task RemoveAsync(string id) => UpdateAsync(x => x.Accounts.RemoveAll(a => a.Id == id && !a.IsOwner));

    private static void SetPassword(Account a, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        a.Salt = Convert.ToBase64String(salt);
        a.PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
    }

    /// <summary>The account with this name and password, or null.</summary>
    public Account? Verify(string name, string password)
    {
        var a = Get().Accounts.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        // Hash anyway when there's no such name, so the time taken doesn't reveal which names exist.
        var salt = a is null ? new byte[16] : Convert.FromBase64String(a.Salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return a is not null && CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(a.PasswordHash)) ? a : null;
    }

    /// <summary>Sign-in cookie: the account id and a signature that also covers the password, so changing it signs
    /// that person out everywhere.</summary>
    public string CookieFor(Account a) => $"{a.Id}.{Sign(a)}";

    public Account? FromCookie(string? cookie)
    {
        if (cookie is null || cookie.Split('.') is not [var id, var sig]) return null;
        var a = Find(id);
        return a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(a)), Encoding.UTF8.GetBytes(sig)) ? a : null;
    }

    private string Sign(Account a) =>
        Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(Get().CookieKey), Encoding.UTF8.GetBytes($"{a.Id}|{a.PasswordHash}")));
}
