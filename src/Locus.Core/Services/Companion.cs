using Locus.Core.Models;
using Locus.Core.Tools;

namespace Locus.Core.Services;

/// <summary>Something a bot has picked up about how to be with the user: their humor, what they're going through, how
/// they like to be talked to, running jokes.</summary>
public sealed class RelationshipNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

/// <summary>Something ongoing in the user's life (a job search, a move, a project, plans for a birthday) and how it's going.</summary>
public sealed class LifeThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public List<ThreadUpdate> Updates { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string? Latest => Updates.LastOrDefault()?.Text;
}

public sealed record ThreadUpdate(DateTimeOffset At, string Text);

/// <summary>
/// What makes a bot feel like it knows you: its own notes on the relationship (per bot, relationship.json) and the
/// threads of your life it keeps up with (threads.json). Both grow on their own after conversations, show on the
/// Memory page, and go into the lead bot's instructions.
/// </summary>
public sealed class Companion(IAppPaths paths)
{
    public const int MaxNotesPerBot = 25;
    public const string LocusKey = "locus";
    /// <summary>Threads untouched this long drop out of the bots' instructions (still listed on the Memory page).</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, List<RelationshipNote>>? _notes;
    private List<LifeThread>? _threads;
    private string NotesPath => Path.Combine(paths.DataDirectory, "relationship.json");
    private string ThreadsPath => Path.Combine(paths.DataDirectory, "threads.json");

    public event Action? Changed;

    private async Task<T> LockedAsync<T>(Func<Task<T>> work)
    {
        await _lock.WaitAsync();
        try { return await work(); }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, List<RelationshipNote>>> NotesAsync() =>
        _notes ??= await Json.ReadAsync<Dictionary<string, List<RelationshipNote>>>(NotesPath) ?? [];

    private async Task<List<LifeThread>> ThreadsAsync() => _threads ??= await Json.ReadAsync<List<LifeThread>>(ThreadsPath) ?? [];

    private static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');

    // ------------------------------------------------------------------ relationship notes

    public Task<List<RelationshipNote>> NotesForAsync(string? botId) =>
        LockedAsync(async () => (await NotesAsync()).TryGetValue(botId ?? LocusKey, out var l) ? l.ToList() : []);

    /// <summary>Adds a note (skipping one it already has); the oldest go once there are <see cref="MaxNotesPerBot"/>.</summary>
    public async Task<bool> AddNoteAsync(string? botId, string text) => await NoteAsync(botId, text) is not null;

    /// <summary>Like <see cref="AddNoteAsync"/>, returning the new note (null when skipped).</summary>
    public async Task<RelationshipNote?> NoteAsync(string? botId, string text)
    {
        text = text.Trim();
        if (text.Length is < 4 or > 300) return null;
        var added = await LockedAsync(async () =>
        {
            var all = await NotesAsync();
            var list = all.TryGetValue(botId ?? LocusKey, out var l) ? l : all[botId ?? LocusKey] = [];
            var n = Norm(text);
            if (list.Any(x => Norm(x.Text) == n || Norm(x.Text).Contains(n))) return null;
            var note = new RelationshipNote { Text = text };
            list.Add(note);
            if (list.Count > MaxNotesPerBot) list.RemoveRange(0, list.Count - MaxNotesPerBot);
            await Json.WriteAtomicAsync(NotesPath, all);
            return note;
        });
        if (added is not null) Changed?.Invoke();
        return added;
    }

    public async Task RemoveNoteAsync(string? botId, string id)
    {
        await LockedAsync(async () =>
        {
            var all = await NotesAsync();
            if (all.TryGetValue(botId ?? LocusKey, out var l) && l.RemoveAll(x => x.Id == id) > 0) await Json.WriteAtomicAsync(NotesPath, all);
            return true;
        });
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ life threads

    public Task<List<LifeThread>> ThreadsListAsync() =>
        LockedAsync(async () => (await ThreadsAsync()).OrderBy(t => t.Done).ThenByDescending(t => t.UpdatedAt).ToList());

    /// <summary>Open threads touched recently, newest first: what the bots keep in mind.</summary>
    public async Task<List<LifeThread>> ActiveAsync(int max = 8) =>
        (await ThreadsListAsync()).Where(t => !t.Done && DateTimeOffset.Now - t.UpdatedAt < Quiet).Take(max).ToList();

    private static LifeThread? Match(List<LifeThread> list, string title)
    {
        var n = Norm(title);
        return list.FirstOrDefault(t => Norm(t.Title) == n)
            ?? list.Where(t => !t.Done).FirstOrDefault(t => Norm(t.Title).Contains(n) || n.Contains(Norm(t.Title)) && Norm(t.Title).Length > 4);
    }

    /// <summary>Adds an update to the thread with this title (started if new, reopened if it was done).</summary>
    public async Task<LifeThread?> UpdateAsync(string title, string update)
    {
        title = title.Trim().Trim('"', '.');
        update = update.Trim();
        if (title.Length is < 2 or > 80 || update.Length is < 3 or > 400) return null;
        var thread = await LockedAsync(async () =>
        {
            var list = await ThreadsAsync();
            var t = Match(list, title);
            if (t is null) list.Add(t = new LifeThread { Title = title });
            if (t.Updates.LastOrDefault() is { } last && Norm(last.Text) == Norm(update)) return t;
            t.Updates.Add(new ThreadUpdate(DateTimeOffset.Now, update));
            if (t.Updates.Count > 30) t.Updates.RemoveRange(0, t.Updates.Count - 30);
            t.Done = false;
            t.UpdatedAt = DateTimeOffset.Now;
            await Json.WriteAtomicAsync(ThreadsPath, list);
            return t;
        });
        Changed?.Invoke();
        return thread;
    }

    /// <summary>Marks a thread done (the job search ended, the trip happened), with how it ended.</summary>
    public async Task<LifeThread?> CloseAsync(string titleOrId, string? outcome = null)
    {
        var thread = await LockedAsync(async () =>
        {
            var list = await ThreadsAsync();
            var t = list.FirstOrDefault(x => x.Id == titleOrId) ?? Match(list, titleOrId);
            if (t is null) return null;
            if (!string.IsNullOrWhiteSpace(outcome)) t.Updates.Add(new ThreadUpdate(DateTimeOffset.Now, outcome.Trim()));
            t.Done = true;
            t.UpdatedAt = DateTimeOffset.Now;
            await Json.WriteAtomicAsync(ThreadsPath, list);
            return t;
        });
        Changed?.Invoke();
        return thread;
    }

    /// <summary>Takes back one update (the newest with this text); a thread left with no updates goes.</summary>
    public async Task<bool> RemoveUpdateAsync(string threadId, string update)
    {
        var removed = await LockedAsync(async () =>
        {
            var list = await ThreadsAsync();
            if (list.FirstOrDefault(x => x.Id == threadId) is not { } t) return false;
            var i = t.Updates.FindLastIndex(u => Norm(u.Text) == Norm(update));
            if (i < 0) return false;
            t.Updates.RemoveAt(i);
            if (t.Updates.Count == 0) list.Remove(t);
            await Json.WriteAtomicAsync(ThreadsPath, list);
            return true;
        });
        if (removed) Changed?.Invoke();
        return removed;
    }

    public async Task SetDoneAsync(string id, bool done)
    {
        await LockedAsync(async () =>
        {
            var list = await ThreadsAsync();
            if (list.FirstOrDefault(x => x.Id == id) is { } t) { t.Done = done; t.UpdatedAt = DateTimeOffset.Now; await Json.WriteAtomicAsync(ThreadsPath, list); }
            return true;
        });
        Changed?.Invoke();
    }

    public async Task RemoveThreadAsync(string id)
    {
        await LockedAsync(async () =>
        {
            var list = await ThreadsAsync();
            if (list.RemoveAll(x => x.Id == id) > 0) await Json.WriteAtomicAsync(ThreadsPath, list);
            return true;
        });
        Changed?.Invoke();
    }

    /// <summary>How long ago, for the bots: "today", "3 days ago".</summary>
    public static string Ago(DateTimeOffset at)
    {
        var days = (int)(DateTime.Today - at.LocalDateTime.Date).TotalDays;
        return days switch { <= 0 => "today", 1 => "yesterday", < 14 => $"{days} days ago", < 60 => $"{days / 7} weeks ago", _ => $"{days / 30} months ago" };
    }

    /// <summary>The part of a lead bot's instructions that makes it feel like it knows the user.</summary>
    public async Task<string> PromptAsync(string? botId)
    {
        var sb = new System.Text.StringBuilder();
        var notes = await NotesForAsync(botId);
        if (notes.Count > 0)
        {
            sb.AppendLine("How you are with the user (your own notes on your relationship; let them shape how you talk, don't recite them):");
            foreach (var n in notes.TakeLast(15)) sb.AppendLine($"- {n.Text}");
        }
        var threads = await ActiveAsync();
        if (threads.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("What's going on in the user's life (ongoing threads; when it fits, ask how one is going or connect it to what " +
                          "they're saying — like a friend who remembers. Keep them current with life_threads):");
            foreach (var t in threads) sb.AppendLine($"- {t.Title}: {t.Latest} ({Ago(t.UpdatedAt)})");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>life_threads: the bots keep the user's ongoing threads up to date.</summary>
public sealed class LifeThreadToolProvider(Companion companion) : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) => Task.FromResult<IReadOnlyList<ITool>>(
    [
        new GTool("life_threads", "Life threads", "🧵",
            "The ongoing things in the user's life you keep up with (a job search, a move, a project, a trip, someone's health). " +
            "action=list shows them; action=update adds what's new to a thread (starting it if new); action=close marks one finished.",
            Schema.Object(("action", "string", "list, update or close", true), ("title", "string", "Short name of the thread, e.g. 'Job search' or 'Kitchen remodel'", false),
                ("update", "string", "What's new, in one sentence (for update), or how it ended (for close)", false)), false,
            async a =>
            {
                switch ((a.Str("action") ?? "list").ToLowerInvariant())
                {
                    case "update":
                        var t = await companion.UpdateAsync(a.Str("title") ?? "", a.Str("update") ?? "");
                        return t is null ? "Give a short title and what's new." : $"Updated \"{t.Title}\".";
                    case "close":
                        var c = await companion.CloseAsync(a.Str("title") ?? "", a.Str("update"));
                        return c is null ? $"No thread called \"{a.Str("title")}\"." : $"Closed \"{c.Title}\".";
                    default:
                        var all = await companion.ThreadsListAsync();
                        return all.Count == 0 ? "No threads yet." : string.Join("\n", all.Take(30).Select(x =>
                            $"- {x.Title}{(x.Done ? " (done)" : "")}: {x.Latest} ({Companion.Ago(x.UpdatedAt)})"));
                }
            }),
    ]);
}
