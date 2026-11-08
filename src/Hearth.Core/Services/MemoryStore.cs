namespace Hearth.Core.Services;

/// <param name="Archived">Long-term: folded into the profile or past the recent list. Not listed in every prompt, but
/// brought up when relevant to what the user asks, and found with recall_memory. Never deleted automatically.</param>
public sealed record Memory(string Id, string Text, DateTimeOffset CreatedAt, bool Archived = false);

/// <summary>Long-term facts about the user and each bot's own notes. Stored by the memory provider (SQLite by default).</summary>
public sealed class MemoryStore
{
    private readonly IMemoryProvider _provider;

    public MemoryStore(IMemoryProvider provider) => _provider = provider;

    /// <summary>Keyword-only store in the given data folder (tests and tools).</summary>
    public static MemoryStore Local(IAppPaths paths) => new(new SqliteMemoryProvider(paths, NoEmbedder.Instance));

    public IMemoryProvider Provider => _provider;
    public event Action? Changed;

    public Task<List<Memory>> ListAsync(string? scope = null) => _provider.ListAsync(scope);

    public async Task<Memory> AddAsync(string text, string? scope = null)
    {
        var m = await _provider.AddAsync(text, scope);
        Changed?.Invoke();
        return m;
    }

    public async Task<bool> UpdateAsync(string id, string text, string? scope = null)
    {
        var ok = await _provider.UpdateAsync(id, text, scope);
        if (ok) Changed?.Invoke();
        return ok;
    }

    /// <summary>Best matches by keywords and, when an embedding model is available, by meaning.</summary>
    public Task<List<Memory>> SearchAsync(string query, string? scope, int max) => _provider.SearchAsync(query, scope, max);

    /// <summary>Moves memories to long-term storage (or back to the recent list).</summary>
    public async Task SetArchivedAsync(IReadOnlyCollection<string> ids, bool archived, string? scope = null)
    {
        if (ids.Count == 0) return;
        await _provider.SetArchivedAsync(ids, archived, scope);
        Changed?.Invoke();
    }

    /// <summary>True when an equivalent memory already exists (case/punctuation-insensitive containment).</summary>
    public async Task<bool> KnowsAsync(string text, string? scope)
    {
        var n = Normalize(text);
        return (await ListAsync(scope)).Any(m => { var e = Normalize(m.Text); return e == n || e.Contains(n) || n.Contains(e); });
    }

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public async Task<bool> DeleteAsync(string id, string? scope = null)
    {
        var ok = await _provider.DeleteAsync(id, scope);
        if (ok) Changed?.Invoke();
        return ok;
    }

    public async Task ClearAsync(string? scope = null)
    {
        await _provider.ClearAsync(scope);
        Changed?.Invoke();
    }
}
