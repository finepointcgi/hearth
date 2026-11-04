using System.Collections.Concurrent;
using System.Text.Json;
using Locus.Core.Models;

namespace Locus.Core.Services;

public enum ActivityKind { Routine, Trigger, Message, Approval, Channel, Error, Tool, Task }

public sealed record Activity(
    DateTimeOffset Time,
    ActivityKind Kind,
    string Title,
    string? Detail = null,
    string? BotId = null,
    string? ConversationId = null,
    bool Ok = true);

/// <summary>What bots did and what went wrong, for the dashboard. Appended to activity.jsonl (last 2000 kept).</summary>
public sealed class ActivityLog(IAppPaths paths)
{
    private const int Keep = 2000;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Activity>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "activity.jsonl");

    public event Action<Activity>? Added;

    public async Task AddAsync(Activity a)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.Add(a);
            Directory.CreateDirectory(paths.DataDirectory);
            if (list.Count > Keep * 1.25)
            {
                list.RemoveRange(0, list.Count - Keep);
                await File.WriteAllLinesAsync(FilePath, list.Select(x => JsonSerializer.Serialize(x)));
            }
            else await File.AppendAllTextAsync(FilePath, JsonSerializer.Serialize(a) + "\n");
        }
        catch (IOException) { /* activity is best-effort */ }
        finally { _lock.Release(); }
        Added?.Invoke(a);
    }

    public async Task<List<Activity>> SinceAsync(DateTimeOffset since)
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).Where(a => a.Time >= since).OrderByDescending(a => a.Time).ToList(); }
        finally { _lock.Release(); }
    }

    private async Task<List<Activity>> LoadAsync()
    {
        if (_cache is not null) return _cache;
        _cache = [];
        if (!File.Exists(FilePath)) return _cache;
        foreach (var line in await File.ReadAllLinesAsync(FilePath))
            try { if (JsonSerializer.Deserialize<Activity>(line) is { } a) _cache.Add(a); }
            catch (JsonException) { }
        return _cache;
    }
}

/// <summary>An action waiting for the user's OK, from any chat, bot or routine.</summary>
public sealed record PendingApproval(
    string Id,
    DateTimeOffset Since,
    string ConversationId,
    string ConversationTitle,
    string? BotId,
    string ToolName,
    string ArgumentsJson,
    TaskCompletionSource<bool> Decision);

/// <summary>
/// Every tool call waiting for approval, so the dashboard, the chat and Telegram/Discord can all answer it.
/// The first answer wins.
/// </summary>
public sealed class ApprovalCenter
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    public event Action<PendingApproval>? Requested;
    public event Action<PendingApproval, bool>? Resolved;

    public IReadOnlyList<PendingApproval> Pending => _pending.Values.OrderBy(p => p.Since).ToList();

    public PendingApproval Register(Conversation conv, Bot? bot, ToolCall call, TaskCompletionSource<bool> decision)
    {
        var p = new PendingApproval(call.Id, DateTimeOffset.Now, conv.Id, conv.Title, bot?.Id, call.Name, call.ArgumentsJson, decision);
        _pending[p.Id] = p;
        decision.Task.ContinueWith(t =>
        {
            if (_pending.TryRemove(p.Id, out _))
                Resolved?.Invoke(p, t.IsCompletedSuccessfully && t.Result);
        }, TaskScheduler.Default);
        Requested?.Invoke(p);
        return p;
    }

    /// <summary>Answers a pending approval. Returns false if it was already answered or cancelled.</summary>
    public bool Decide(string id, bool allow) =>
        _pending.TryGetValue(id, out var p) && p.Decision.TrySetResult(allow);

    public void Cancel(string id)
    {
        if (_pending.TryGetValue(id, out var p)) p.Decision.TrySetCanceled();
    }
}
