namespace Locus.Core.Services.Phone;

/// <summary>Phone calls through Twilio (stored in phone.json; the auth token is encrypted).</summary>
public sealed class PhoneConfig
{
    public bool Enabled { get; set; }
    /// <summary>Twilio Account SID (AC…).</summary>
    public string AccountSid { get; set; } = "";
    public string? AuthTokenSealed { get; set; }
    /// <summary>The Twilio number calls come from and go to, E.164 (+13135550100).</summary>
    public string Number { get; set; } = "";
    /// <summary>Public https address Twilio reaches Locus at (Tailscale Funnel, Cloudflare Tunnel…), no trailing slash.</summary>
    public string PublicUrl { get; set; } = "";
    /// <summary>The bot that answers calls to the number (null = Locus).</summary>
    public string? AnswerBotId { get; set; }
    /// <summary>Who may call in (E.164). Anyone else hears that the line is private. Empty = nobody.</summary>
    public List<string> AllowedCallers { get; set; } = [];
    public string Language { get; set; } = "en-US";
    /// <summary>Twilio's text-to-speech provider (Google, Amazon, ElevenLabs) and voice; empty = Twilio's default.</summary>
    public string TtsProvider { get; set; } = "";
    public string Voice { get; set; } = "";
    /// <summary>Longest a bot-placed call may last.</summary>
    public int MaxCallMinutes { get; set; } = 10;
}

public sealed class PhoneStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SecretBox _box = new(paths);
    private PhoneConfig? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "phone.json");

    public event Action? Changed;

    public async Task<PhoneConfig> GetAsync()
    {
        await _lock.WaitAsync();
        try { return _cache ??= await Json.ReadAsync<PhoneConfig>(FilePath) ?? new(); }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(Action<PhoneConfig> change)
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await Json.ReadAsync<PhoneConfig>(FilePath) ?? new();
            change(_cache);
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task<string?> AuthTokenAsync()
    {
        var c = await GetAsync();
        return string.IsNullOrEmpty(c.AuthTokenSealed) ? null : _box.Open(c.AuthTokenSealed);
    }

    public Task SetAuthTokenAsync(string? token) => UpdateAsync(c =>
        c.AuthTokenSealed = string.IsNullOrWhiteSpace(token) ? null : _box.Seal(token.Trim()));

    /// <summary>Everything needed to place and take calls is filled in.</summary>
    public async Task<string?> MissingAsync()
    {
        var c = await GetAsync();
        if (!c.Enabled) return "Phone calls are turned off (Connections → Phone).";
        if (string.IsNullOrWhiteSpace(c.AccountSid) || await AuthTokenAsync() is null) return "Add your Twilio Account SID and Auth Token (Connections → Phone).";
        if (Twilio.Normalize(c.Number) is null) return "Add your Twilio phone number (Connections → Phone).";
        if (!c.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Add Locus's public https address (Connections → Phone).";
        return null;
    }
}
