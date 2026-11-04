namespace Locus.Core.Services.Channels;

/// <summary>A Telegram or Discord chat that's allowed to talk to Locus.</summary>
public sealed class ChannelLink
{
    public string Channel { get; set; } = "";          // "telegram" | "discord" | "slack"
    public string ChatId { get; set; } = "";
    /// <summary>Only this user may talk to Locus in this chat (matters in groups/servers).</summary>
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Bot that answers by default (null = plain Locus).</summary>
    public string? ActiveBotId { get; set; }
    /// <summary>When set, messages go to this Locus group chat (conversation id) instead of one bot.</summary>
    public string? ActiveGroupId { get; set; }
    /// <summary>bot id (or "locus") → conversation id used for this chat.</summary>
    public Dictionary<string, string> Conversations { get; set; } = [];
    public bool NotifyRoutines { get; set; } = true;
    public bool NotifyApprovals { get; set; } = true;
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ChannelConfig
{
    public string? TelegramTokenSealed { get; set; }
    public string? DiscordTokenSealed { get; set; }
    /// <summary>"xapp-…|xoxb-…", sealed.</summary>
    public string? SlackTokenSealed { get; set; }
    /// <summary>"on" when iMessage is switched on (no token needed).</summary>
    public string? IMessageTokenSealed { get; set; }
    /// <summary>"http://bridge:8080|+15551234567", sealed.</summary>
    public string? SignalTokenSealed { get; set; }
    public List<ChannelLink> Links { get; set; } = [];
    /// <summary>Testing only.</summary>
    public string? TelegramApiBase { get; set; }
    public string? DiscordApiBase { get; set; }
    public string? SlackApiBase { get; set; }
}

public sealed class ChannelStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SecretBox _box = new(paths);
    private ChannelConfig? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "channels.json");

    public event Action? Changed;

    public async Task<ChannelConfig> GetAsync()
    {
        await _lock.WaitAsync();
        try { return _cache ??= await Json.ReadAsync<ChannelConfig>(FilePath) ?? new(); }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(Action<ChannelConfig> change)
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await Json.ReadAsync<ChannelConfig>(FilePath) ?? new();
            change(_cache);
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public string? Token(string channel)
    {
        var c = _cache;
        var sealedToken = channel switch
        {
            "telegram" => c?.TelegramTokenSealed, "slack" => c?.SlackTokenSealed, "imessage" => c?.IMessageTokenSealed,
            "signal" => c?.SignalTokenSealed, _ => c?.DiscordTokenSealed,
        };
        return string.IsNullOrEmpty(sealedToken) ? null : _box.Open(sealedToken);
    }

    public Task SetTokenAsync(string channel, string? token) => UpdateAsync(c =>
    {
        var sealedToken = string.IsNullOrWhiteSpace(token) ? null : _box.Seal(token.Trim());
        switch (channel)
        {
            case "telegram": c.TelegramTokenSealed = sealedToken; break;
            case "slack": c.SlackTokenSealed = sealedToken; break;
            case "imessage": c.IMessageTokenSealed = sealedToken; break;
            case "signal": c.SignalTokenSealed = sealedToken; break;
            default: c.DiscordTokenSealed = sealedToken; break;
        }
    });
}
