using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Something a bot promised to come back to: at <see cref="DueAt"/> it picks the chat up again on its own.</summary>
public sealed class FollowUp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string ConversationId { get; set; } = "";
    public string? BotId { get; set; }
    /// <summary>What to do then, in the bot's words ("check for new director-level postings").</summary>
    public string Note { get; set; } = "";
    public DateTimeOffset DueAt { get; set; }
    /// <summary>null = once; "daily" or "weekly" = again after each run.</summary>
    public string? Repeat { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRun { get; set; }
    public string? LastResult { get; set; }
    public int Runs { get; set; }
}

/// <summary>Follow-ups bots scheduled, in followups.json.</summary>
public sealed class FollowUpStore(IAppPaths paths)
{
    public const int MaxPerChat = 10;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string FilePath => Path.Combine(paths.DataDirectory, "followups.json");
    public event Action? Changed;

    public async Task<List<FollowUp>> ListAsync() => await Json.ReadAsync<List<FollowUp>>(FilePath) ?? [];

    public async Task<FollowUp> AddAsync(FollowUp f)
    {
        await UpdateAsync(list =>
        {
            if (list.Count(x => x.ConversationId == f.ConversationId) >= MaxPerChat)
                throw new InvalidOperationException($"This chat already has {MaxPerChat} follow-ups scheduled. Cancel one first.");
            list.Add(f);
        });
        return f;
    }

    public Task<bool> RemoveAsync(string id) => UpdateAsync(list => list.RemoveAll(f => f.Id == id) > 0);

    public async Task<T> UpdateAsync<T>(Func<List<FollowUp>, T> change)
    {
        T result;
        await _lock.WaitAsync();
        try
        {
            var list = await ListAsync();
            result = change(list);
            await Json.WriteAtomicAsync(FilePath, list.OrderBy(f => f.DueAt).ToList());
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        return result;
    }

    public Task UpdateAsync(Action<List<FollowUp>> change) => UpdateAsync(list => { change(list); return true; });
}

/// <summary>
/// Runs follow-ups when they're due: the bot gets a note in the same chat, does what it said it would (unattended,
/// so anything that changes things is skipped and mentioned), and posts only when there's something to tell you.
/// </summary>
public sealed class FollowUpRunner(FollowUpStore store, ConversationStore conversations, ChatService chat, ChatRuns runs,
    BotStore bots, ModelPicker models, ActivityLog activity) : IDisposable
{
    public const string NothingNew = "NOTHING_NEW";
    private Timer? _timer;
    private int _busy;

    /// <summary>A follow-up posted something (conversation id, text): for phone notifications.</summary>
    public event Action<string, string>? Posted;

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));

    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var due in (await store.ListAsync()).Where(f => f.DueAt <= DateTimeOffset.Now).ToList())
                await RunAsync(due);
        }
        catch { /* try again next minute */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Runs one follow-up now (also used by "Run now").</summary>
    public async Task RunAsync(FollowUp f)
    {
        var conv = await conversations.GetAsync(f.ConversationId);
        if (conv is null) { await store.RemoveAsync(f.Id); return; } // the chat was deleted
        if (runs.IsRunning(conv.Id))
        {
            // The chat is busy: come back in a few minutes.
            await store.UpdateAsync(list => { if (list.Find(x => x.Id == f.Id) is { } x) x.DueAt = DateTimeOffset.Now.AddMinutes(5); });
            return;
        }

        // Move it on (or remove it) before running, so a crash can't make it fire over and over.
        var next = f.Repeat switch { "daily" => f.DueAt.AddDays(1), "weekly" => f.DueAt.AddDays(7), _ => (DateTimeOffset?)null };
        while (next is { } n && n <= DateTimeOffset.Now) next = f.Repeat == "daily" ? n.AddDays(1) : n.AddDays(7);
        await store.UpdateAsync(list =>
        {
            if (list.Find(x => x.Id == f.Id) is not { } x) return;
            if (next is null) list.Remove(x);
            else { x.DueAt = next.Value; x.LastRun = DateTimeOffset.Now; x.Runs++; }
        });

        var bot = f.BotId is null ? null : await bots.GetAsync(f.BotId);
        var model = await models.PickAsync(bot);
        if (model is null) return;
        var prompt = new ChatMessage
        {
            Role = ChatRole.User, RoutineName = "Follow-up",
            Content =
                $"Follow-up you scheduled earlier ({DateTime.Now:dddd h:mm tt}): {f.Note}\n\n" +
                "Do it now using your tools, building on the conversation above. Then tell the user briefly what you found " +
                "or did and what (if anything) they need to decide. " +
                (f.Repeat is null ? "" : "This repeats, so only report what's new since last time. ") +
                $"If there's nothing new or worth telling them, your whole reply must be exactly {NothingNew}.",
        };

        string? posted = null;
        await runs.StartAsync(conv, async (update, ct) =>
        {
            conv.Messages.Add(prompt);
            var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true), update, ct,
                autoTitle: false, unattended: true, historyWindow: 30);
            if (reply is null || reply.Error is not null)
            {
                await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error, "Follow-up failed", reply?.Error, bot?.Id, conv.Id, Ok: false));
                return;
            }
            if (HeartbeatRunner.IsOk(reply.Content.Replace(NothingNew, HeartbeatRunner.Ok)))
            {
                conv.Messages.Remove(prompt);
                conv.Messages.Remove(reply);
                await conversations.SaveAsync(conv, notify: false);
                return;
            }
            conv.Unread = true;
            await conversations.SaveAsync(conv);
            posted = reply.Content;
        });

        await store.UpdateAsync(list => { if (list.Find(x => x.Id == f.Id) is { } x) x.LastResult = posted is null ? "Nothing new." : "Posted an update."; });
        if (posted is not null)
        {
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Routine, $"{bot?.Name ?? "Hearth"} followed up",
                posted.Length > 280 ? posted[..280] + "…" : posted, bot?.Id, conv.Id));
            Posted?.Invoke(conv.Id, posted);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
