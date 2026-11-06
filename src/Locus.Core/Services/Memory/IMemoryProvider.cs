using Locus.Core.Models;

namespace Locus.Core.Services;

public sealed record MemoryStats(string Provider, int Facts, int FactsEmbedded, int Chats, int ChatMessages, int MessagesEmbedded,
    string Recall, string? Location);

/// <summary>
/// Where Locus keeps what it remembers: facts (shared, and each bot's own), the user profile, and searchable chat
/// history. The built-in provider is a local SQLite database; others (e.g. a hosted memory service) can replace it.
/// </summary>
public interface IMemoryProvider
{
    string Name { get; }

    /// <param name="scope">null = shared facts about the user; otherwise a bot id (that bot's own notes).</param>
    Task<List<Memory>> ListAsync(string? scope);
    Task<Memory> AddAsync(string text, string? scope);
    Task<bool> UpdateAsync(string id, string text, string? scope);
    Task<bool> DeleteAsync(string id, string? scope);
    Task ClearAsync(string? scope);
    /// <summary>Best matches by keywords and (when available) by meaning.</summary>
    Task<List<Memory>> SearchAsync(string query, string? scope, int max);
    /// <summary>Moves memories to long-term storage (archived = true) or back to the recent list.</summary>
    Task SetArchivedAsync(IReadOnlyCollection<string> ids, bool archived, string? scope);

    Task<UserProfile> GetProfileAsync();
    Task SaveProfileAsync(UserProfile profile);

    Task IndexConversationAsync(Conversation conversation, DateTime fileTime);
    Task RemoveConversationAsync(string id);
    /// <summary>Conversation id → the file time it was indexed at.</summary>
    Task<Dictionary<string, DateTime>> IndexedConversationsAsync();
    Task<List<ChatHit>> SearchChatsAsync(string query, string? visibleTo, int limit, string? excludeConversation, int? withinDays);

    Task<MemoryStats> StatsAsync();
}
