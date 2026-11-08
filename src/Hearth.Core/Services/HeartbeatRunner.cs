using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Proactive check-ins (like OpenClaw's heartbeat): every N minutes a bot reviews your checklist.</summary>
public sealed class HeartbeatSettings
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    /// <summary>Who checks in (null = plain Hearth).</summary>
    public string? BotId { get; set; }
    /// <summary>What to keep an eye on — your HEARTBEAT.md.</summary>
    public string Checklist { get; set; } =
        "- Anything due today or overdue that I haven't mentioned (calendar, tasks, reminders I asked for)\n" +
        "- Important new email from real people (not newsletters)\n" +
        "- Long-running tasks that finished, failed or need my answer\n" +
        "- Anything I asked you to watch for";
    /// <summary>No check-ins between these times (24h "HH:mm"; equal = never quiet).</summary>
    public string QuietFrom { get; set; } = "22:00";
    public string QuietUntil { get; set; } = "08:00";

    public bool IsQuiet(DateTime now)
    {
        if (!TimeSpan.TryParse(QuietFrom, out var from) || !TimeSpan.TryParse(QuietUntil, out var until) || from == until) return false;
        var t = now.TimeOfDay;
        return from < until ? t >= from && t < until : t >= from || t < until;
    }
}

public sealed class HeartbeatState
{
    public string? ConversationId { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    public string? LastResult { get; set; }
    public int Alerts { get; set; }
}

public sealed class HeartbeatRunner(SettingsStore settingsStore, BotStore bots, ConversationStore conversations, ChatService chat,
    ModelPicker models, ActivityLog activity, IAppPaths paths) : IDisposable
{
    public const string Ok = "HEARTBEAT_OK";
    private Timer? _timer;
    private int _busy;
    private string StatePath => Path.Combine(paths.DataDirectory, "heartbeat.json");

    /// <summary>Raised with the conversation id when a check-in has something to tell the user.</summary>
    public event Action<string, string>? Alert;
    public event Action? Changed;

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

    public async Task<HeartbeatState> StateAsync() => await Json.ReadAsync<HeartbeatState>(StatePath) ?? new();

    private async Task TickAsync()
    {
        var s = (await settingsStore.GetAsync()).Heartbeat;
        if (!s.Enabled || s.IsQuiet(DateTime.Now)) return;
        var state = await StateAsync();
        if (state.LastRun is { } last && DateTimeOffset.Now - last < TimeSpan.FromMinutes(Math.Max(5, s.IntervalMinutes))) return;
        await RunNowAsync();
    }

    /// <summary>Runs one check-in now. Returns what happened, for the UI.</summary>
    public async Task<string> RunNowAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "A check-in is already running.";
        var state = await StateAsync();
        try
        {
            var settings = await settingsStore.GetAsync();
            var s = settings.Heartbeat;
            var bot = s.BotId is null ? null : await bots.GetAsync(s.BotId);
            var model = await models.PickAsync(bot);
            state.LastRun = DateTimeOffset.Now;
            if (model is null) return state.LastResult = "No model available.";

            var conv = state.ConversationId is { } id ? await conversations.GetAsync(id) : null;
            if (conv is null)
            {
                conv = new Conversation { Title = "💓 Heartbeat", BotIds = bot is null ? [] : [bot.Id] };
                state.ConversationId = conv.Id;
            }
            var prompt = new ChatMessage
            {
                Role = ChatRole.User, RoutineName = "Heartbeat",
                Content =
                    $"Heartbeat check-in ({DateTime.Now:dddd h:mm tt}). Go through this checklist using your tools where useful:\n\n{s.Checklist.Trim()}\n\n" +
                    "Rules: only report concrete things that need the user's attention now (something due, a reply they need " +
                    "to see, something that failed or changed). Skip any item you can't check (e.g. no email access) silently — " +
                    "never report that you checked, that all is well, or what you can't access. Earlier check-ins are above; " +
                    "don't repeat something you already told the user unless it changed.\n" +
                    $"If there's nothing concrete to report, your whole reply must be exactly {Ok}. " +
                    "Otherwise write a short, direct message with just what matters.",
            };
            conv.Messages.Add(prompt);
            var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true), () => Task.CompletedTask, ct,
                autoTitle: false, unattended: true, historyWindow: 16);

            if (reply is null || reply.Error is not null)
            {
                state.LastResult = $"Failed: {reply?.Error ?? "no reply"}";
                await conversations.SaveAsync(conv, notify: false);
                await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error, "Heartbeat failed", reply?.Error, bot?.Id, conv.Id, Ok: false));
                return state.LastResult;
            }
            if (IsOk(reply.Content))
            {
                // Nothing to say: drop the exchange so the chat only holds real alerts.
                conv.Messages.Remove(prompt);
                conv.Messages.Remove(reply);
                await conversations.SaveAsync(conv, notify: false);
                return state.LastResult = "Nothing to report.";
            }
            conv.Unread = true;
            await conversations.SaveAsync(conv);
            state.Alerts++;
            state.LastResult = "Sent you a message.";
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Routine, $"💓 {bot?.Name ?? "Hearth"} checked in",
                reply.Content.Length > 280 ? reply.Content[..280] + "…" : reply.Content, bot?.Id, conv.Id));
            Alert?.Invoke(conv.Id, reply.Content);
            return state.LastResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return state.LastResult = $"Failed: {ex.Message}";
        }
        finally
        {
            await Json.WriteAtomicAsync(StatePath, state);
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    /// <summary>Models sometimes decorate the token; anything short that contains it counts as "nothing to report".</summary>
    public static bool IsOk(string text)
    {
        var t = text.Trim().Trim('*', '`', '.', ' ', '"');
        return t.Equals(Ok, StringComparison.OrdinalIgnoreCase) || (t.Contains(Ok, StringComparison.OrdinalIgnoreCase) && t.Length < Ok.Length + 40);
    }

    public void Dispose() => _timer?.Dispose();
}
