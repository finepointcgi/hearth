namespace Hearth.Core.Services.Channels;

/// <summary>A Telegram or Discord chat that's allowed to talk to Hearth.</summary>
public sealed class ChannelLink
{
    public string Channel { get; set; } = "";          // "telegram" | "discord" | "slack"
    public string ChatId { get; set; } = "";
    /// <summary>Only this user may talk to Hearth in this chat (matters in groups/servers).</summary>
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Bot that answers by default (null = plain Hearth).</summary>
    public string? ActiveBotId { get; set; }
    /// <summary>When set, messages go to this Hearth group chat (conversation id) instead of one bot.</summary>
    public string? ActiveGroupId { get; set; }
    /// <summary>bot id (or "hearth") → conversation id used for this chat.</summary>
    public Dictionary<string, string> Conversations { get; set; } = [];
    /// <summary>Results of routines (each routine can also opt out).</summary>
    public bool NotifyRoutines { get; set; } = true;
    /// <summary>"A bot wants to run…" with Allow/Deny buttons.</summary>
    public bool NotifyApprovals { get; set; } = true;
    // A new link starts quiet: approvals, routine results, and replies/progress for what you start from the chat.
    // Everything else is opt-in (/notify on, or Connections → Chat apps).
    /// <summary>Progress of background tasks started elsewhere (tasks started from this chat always report here).</summary>
    public bool NotifyTasks { get; set; }
    /// <summary>Heartbeat check-ins that found something.</summary>
    public bool NotifyHeartbeat { get; set; }
    /// <summary>"The model server is down / back up".</summary>
    public bool NotifyHealth { get; set; }
    /// <summary>Quiet hours, local HH:mm (empty = none): nothing is sent on its own then, except approval requests.</summary>
    public string QuietFrom { get; set; } = "";
    public string QuietTo { get; set; } = "";
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>News that arrived during quiet hours, sent together when they end (newest <see cref="MaxHeld"/> kept).</summary>
    public List<HeldMessage> Held { get; set; } = [];
    public const int MaxHeld = 20;
    /// <summary>Answer voice notes with a voice note too (/voice on).</summary>
    public bool VoiceReplies { get; set; }
    /// <summary>Send the documents a reply made (cover letters, notes, spreadsheets) as files (/files off to stop).</summary>
    public bool SendFiles { get; set; } = true;

    public enum Kind { Routine, Approval, Task, Heartbeat, Health }

    /// <summary>Whether to send this kind of message to this chat now.</summary>
    public bool Wants(Kind kind, DateTimeOffset now)
    {
        var on = kind switch
        {
            Kind.Routine => NotifyRoutines, Kind.Approval => NotifyApprovals, Kind.Task => NotifyTasks,
            Kind.Heartbeat => NotifyHeartbeat, _ => NotifyHealth,
        };
        return on && (kind == Kind.Approval || !IsQuiet(now));
    }

    /// <summary>Inside quiet hours (which may run past midnight, e.g. 22:00–07:00).</summary>
    public bool IsQuiet(DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(QuietFrom, out var from) || !TimeOnly.TryParse(QuietTo, out var to) || from == to) return false;
        var t = TimeOnly.FromDateTime(now.DateTime);
        return from < to ? t >= from && t < to : t >= from || t < to;
    }
}

/// <summary>A message kept back during quiet hours.</summary>
public sealed record HeldMessage(DateTimeOffset At, string Text);

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
