using System.Text;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Something a reply taught Hearth, with where it came from.</summary>
public sealed record LearnedEntry(string ConversationId, string ConversationTitle, string MessageId, DateTimeOffset At, int Index, LearnedItem Item);

/// <summary>
/// What Hearth learned lately, across all chats (the "What Hearth learned" page), and once a week a short recap of it
/// (Settings → Teach Hearth): in the activity feed, as a push notification and in chat apps that get routine results,
/// each with Undo.
/// </summary>
public sealed class LearningRecap(SettingsStore settingsStore, ConversationStore store, ActivityLog activity)
{
    private Timer? _timer;
    private int _busy;

    /// <summary>A recap is ready: its text and the items in it (numbered in the same order).</summary>
    public event Action<string, IReadOnlyList<LearnedEntry>>? Ready;

    /// <summary>Everything replies taught Hearth since <paramref name="since"/>, newest first.</summary>
    public async Task<List<LearnedEntry>> SinceAsync(DateTimeOffset since, bool includeUndone = false)
    {
        var found = new List<LearnedEntry>();
        foreach (var (id, modified) in store.Files())
        {
            if (modified < since.UtcDateTime) continue;
            if (await store.GetAsync(id) is not { } conv) continue;
            foreach (var m in conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.CreatedAt >= since && m.LearnedItems.Count > 0))
                for (var i = 0; i < m.LearnedItems.Count; i++)
                    if (includeUndone || !m.LearnedItems[i].Undone)
                        found.Add(new LearnedEntry(conv.Id, ConversationStore.DisplayTitle(conv), m.Id, m.CreatedAt, i, m.LearnedItems[i]));
        }
        return found.OrderByDescending(e => e.At).ToList();
    }

    /// <summary>Finds one learned item again (it lives in its chat file).</summary>
    public async Task<(Conversation conv, LearnedItem item)?> FindAsync(string conversationId, string messageId, int index)
    {
        if (await store.GetAsync(conversationId) is not { } conv) return null;
        var m = conv.Messages.FirstOrDefault(x => x.Id == messageId);
        return m is not null && index >= 0 && index < m.LearnedItems.Count ? (conv, m.LearnedItems[index]) : null;
    }

    /// <summary>The recap's text; the items are numbered so chat apps can offer "Undo 1", "Undo 2"…</summary>
    public static string Text(IReadOnlyList<LearnedEntry> items, int shown = 12)
    {
        var sb = new StringBuilder($"🧠 This week Hearth learned {items.Count} thing{(items.Count == 1 ? "" : "s")}:");
        for (var i = 0; i < Math.Min(shown, items.Count); i++)
            sb.Append($"\n{i + 1}. {items[i].Item.Icon} {items[i].Item.Label}");
        if (items.Count > shown) sb.Append($"\n…and {items.Count - shown} more.");
        sb.Append("\n\nAnything wrong? Undo it here, or on the What Hearth learned page.");
        return sb.ToString();
    }

    /// <summary>In a routine's prompt, replaced when it runs by what Hearth learned since the routine last ran.</summary>
    public const string LearnedToken = "{what-hearth-learned}";

    /// <summary>Fills in <see cref="LearnedToken"/>: the list for the bot to pass on, or a note to skip that part.</summary>
    public async Task<string> ExpandAsync(string prompt, DateTimeOffset? lastRun)
    {
        if (!prompt.Contains(LearnedToken)) return prompt;
        var since = lastRun is { } l && DateTimeOffset.Now - l < TimeSpan.FromDays(7) ? l : DateTimeOffset.Now.AddDays(-1);
        var items = await SinceAsync(since);
        string part;
        if (items.Count == 0) part = "What I learned: nothing new was learned since the last brief, so leave this part out.";
        else
        {
            var sb = new StringBuilder($"What I learned: end with a short \"What I learned\" part listing these {items.Count} things Hearth " +
                "picked up from the user's chats since the last brief, word for word and numbered, then one line saying anything wrong can be " +
                "undone on the What Hearth learned page or fixed by just saying so:");
            foreach (var (e, i) in items.Take(12).Select((e, i) => (e, i))) sb.Append($"\n  {i + 1}. {e.Item.Icon} {e.Item.Label}");
            if (items.Count > 12) sb.Append($"\n  …and {items.Count - 12} more.");
            part = sb.ToString();
        }
        return prompt.Replace(LearnedToken, part);
    }

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = AutoAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20));

    /// <summary>When the recap is due this week (the set day and time).</summary>
    public static DateTimeOffset DueThisWeek(TeachingSettings t, DateTimeOffset now)
    {
        var time = TimeOnly.TryParse(t.RecapTime, out var parsed) ? parsed : new TimeOnly(18, 0);
        var back = ((int)now.DayOfWeek - (int)t.RecapDay + 7) % 7;
        var day = now.Date.AddDays(-back);
        return new DateTimeOffset(day + time.ToTimeSpan(), now.Offset);
    }

    private async Task AutoAsync()
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            var t = settings.Teaching;
            var now = DateTimeOffset.Now;
            var due = DueThisWeek(t, now);
            if (!t.WeeklyRecap || now < due || now - due > TimeSpan.FromHours(12) || t.LastRecap >= due) return;
            await SendAsync();
        }
        catch { /* best effort; tried again on the next tick */ }
    }

    /// <summary>Builds and sends the recap now (the last 7 days). Returns how many items it had (0 = nothing to send).</summary>
    public async Task<int> SendAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return 0;
        try
        {
            var items = await SinceAsync(DateTimeOffset.Now.AddDays(-7));
            var settings = await settingsStore.GetAsync();
            settings.Teaching.LastRecap = DateTimeOffset.Now;
            await settingsStore.SaveAsync(settings);
            if (items.Count == 0) return 0;
            var text = Text(items);
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, $"This week Hearth learned {items.Count} things",
                string.Join(" · ", items.Take(6).Select(i => i.Item.Label))));
            Ready?.Invoke(text, items);
            return items.Count;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
