using System.Text.RegularExpressions;
using Locus.Core.Models;

namespace Locus.Core.Services;

public sealed record ChatHit(string ConversationId, string Title, DateTimeOffset When, int MessageIndex, double Score, string Excerpt);

/// <summary>
/// Search over every past chat, so bots can recall what was said weeks ago. The index lives in the memory provider
/// (SQLite by default) and is kept in sync with the chat files: only chats that changed since last time are re-read.
/// </summary>
public sealed partial class ChatHistoryIndex
{
    private readonly ConversationStore _store;
    private readonly IMemoryProvider _provider;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private bool _synced;

    public ChatHistoryIndex(ConversationStore store, IMemoryProvider provider)
    {
        _store = store;
        _provider = provider;
        store.Saved += c => _ = IndexAsync(c);
        store.Changed += () => _synced = false; // deletions: re-check on next search
    }

    private bool _started;

    /// <summary>Indexes chats in the background at startup, so the first search is quick.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { await SyncAsync(); } catch { /* retried on the first search */ }
        });
    }

    private async Task IndexAsync(Conversation c)
    {
        try { await _provider.IndexConversationAsync(c, _store.FileTime(c.Id)); }
        catch { /* the next sync picks it up */ }
    }

    /// <summary>Brings the index up to date with the chat files (new, changed and deleted chats).</summary>
    public async Task SyncAsync()
    {
        if (_synced) return;
        await _sync.WaitAsync();
        try
        {
            if (_synced) return;
            var indexed = await _provider.IndexedConversationsAsync();
            var files = _store.Files();
            foreach (var (id, time) in files)
                if (!indexed.TryGetValue(id, out var t) || Math.Abs((t - time).TotalSeconds) > 1)
                    if (await _store.GetAsync(id) is { } c) await _provider.IndexConversationAsync(c, time);
            foreach (var gone in indexed.Keys.Except(files.Select(f => f.id)))
                await _provider.RemoveConversationAsync(gone);
            _synced = true;
        }
        finally { _sync.Release(); }
    }

    /// <summary>
    /// Best-matching messages, at most one hit per conversation. <paramref name="visibleTo"/> limits a bot to chats it
    /// was part of (null = all chats).
    /// </summary>
    public async Task<List<ChatHit>> SearchAsync(string query, string? visibleTo, int limit = 5, string? excludeConversation = null,
        int? withinDays = null)
    {
        await SyncAsync();
        return await _provider.SearchChatsAsync(query, visibleTo, limit, excludeConversation, withinDays);
    }

    private static readonly HashSet<string> Stop =
    [
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "it", "that", "this",
        "with", "as", "at", "by", "i", "you", "we", "me", "my", "your", "do", "did", "can", "what", "how", "about", "from",
        "have", "has", "had", "not", "but", "if", "so", "just", "please", "let", "its", "our", "us", "they", "them",
    ];

    public static List<string> Tokens(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 1 && !Stop.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w).ToList();

    /// <summary>A window of text around the first matching word.</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int size = 320)
    {
        var flat = Whitespace().Replace(text, " ").Trim();
        if (flat.Length <= size) return flat;
        var lower = flat.ToLowerInvariant();
        var at = terms.Select(t => lower.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - size / 3);
        var end = Math.Min(flat.Length, start + size);
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_-]*")] private static partial Regex Word();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
