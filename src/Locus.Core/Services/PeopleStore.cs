using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Tools;

namespace Locus.Core.Services;

/// <summary>Someone (or some company) the user deals with, and what Locus knows about them.</summary>
public sealed class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>"person" or "company".</summary>
    public string Kind { get; set; } = "person";
    public List<EntityNote> Notes { get; set; } = [];
    /// <summary>Names of related people and companies ("works at Shield AI", "recruiter for…").</summary>
    public List<string> Related { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record EntityNote(string Text, DateTimeOffset At);

/// <summary>
/// People and company notes (people.json): who's who in the user's life and work, built up as bots learn. Bots look
/// people up with who_is and add to it with note_about; the Memory page shows and edits it.
/// </summary>
public sealed class PeopleStore(IAppPaths paths)
{
    public const int MaxNotesEach = 40;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Entity>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "people.json");

    public event Action? Changed;

    public async Task<List<Entity>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return (_cache ??= await Json.ReadAsync<List<Entity>>(FilePath) ?? []).OrderByDescending(e => e.UpdatedAt).ToList(); }
        finally { _lock.Release(); }
    }

    private static string Key(string name) => string.Join(' ', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>People and companies whose name contains <paramref name="name"/> (or the other way round).</summary>
    public async Task<List<Entity>> FindAsync(string name)
    {
        var k = Key(name);
        if (k.Length < 2) return [];
        return (await ListAsync()).Where(e => Key(e.Name) == k || Key(e.Name).Contains(k) || k.Contains(Key(e.Name)) && Key(e.Name).Length > 3).ToList();
    }

    /// <summary>Adds a note about someone, creating them if new. Duplicate notes are skipped.</summary>
    public async Task<Entity> NoteAsync(string name, string note, string? kind = null, IEnumerable<string>? related = null)
    {
        name = name.Trim();
        note = note.Trim();
        await _lock.WaitAsync();
        Entity entity;
        try
        {
            _cache ??= await Json.ReadAsync<List<Entity>>(FilePath) ?? [];
            entity = _cache.FirstOrDefault(e => Key(e.Name) == Key(name)) ?? new Entity { Name = name };
            if (!_cache.Contains(entity)) _cache.Add(entity);
            if (kind is "person" or "company") entity.Kind = kind;
            if (note.Length > 0 && entity.Notes.All(n => !n.Text.Equals(note, StringComparison.OrdinalIgnoreCase)))
                entity.Notes.Add(new EntityNote(note.Length > 400 ? note[..400] : note, DateTimeOffset.Now));
            if (entity.Notes.Count > MaxNotesEach) entity.Notes.RemoveRange(0, entity.Notes.Count - MaxNotesEach);
            foreach (var r in related ?? [])
                if (r.Trim() is { Length: > 1 } rn && !rn.Equals(name, StringComparison.OrdinalIgnoreCase) && !entity.Related.Contains(rn, StringComparer.OrdinalIgnoreCase))
                    entity.Related.Add(rn);
            entity.UpdatedAt = DateTimeOffset.Now;
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        return entity;
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await Json.ReadAsync<List<Entity>>(FilePath) ?? [];
            _cache.RemoveAll(e => e.Id == id);
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task DeleteNoteAsync(string id, EntityNote note)
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await Json.ReadAsync<List<Entity>>(FilePath) ?? [];
            _cache.FirstOrDefault(e => e.Id == id)?.Notes.Remove(note);
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    /// <summary>What a bot sees for someone: their notes, newest last, and who they're connected to.</summary>
    public static string Describe(Entity e) =>
        $"{e.Name} ({e.Kind})" + (e.Related.Count > 0 ? $" — related: {string.Join(", ", e.Related)}" : "") + "\n" +
        string.Join("\n", e.Notes.Select(n => $"- {n.At:yyyy-MM-dd}: {n.Text}"));
}

/// <summary>who_is and note_about: the people and company notes.</summary>
public sealed class PeopleToolProvider(PeopleStore people) : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) => Task.FromResult<IReadOnlyList<ITool>>(
    [
        new GTool("who_is", "Who is", "🪪",
            "Look up what you know about a person or company the user deals with (recruiters, colleagues, companies, contacts): notes and who they're connected to.",
            Schema.Object(("name", "string", "Person or company name", true)), false,
            async a =>
            {
                var found = await people.FindAsync(a.Str("name") ?? "");
                return found.Count == 0 ? $"No notes about \"{a.Str("name")}\" yet." : string.Join("\n\n", found.Take(5).Select(PeopleStore.Describe));
            }),
        new GTool("note_about", "Note about someone", "📝",
            "Remember something about a person or company in the user's life or work: their role, what was discussed, where things stand. One fact per call.",
            Schema.Object(("name", "string", "Full name of the person or company", true), ("note", "string", "The fact, e.g. 'Recruiter at Shield AI; reached out Nov 5 about the Director SE&A role'", true),
                ("kind", "string", "person or company", false), ("related", "string", "Related people or companies, comma-separated (optional)", false)), false,
            async a =>
            {
                var e = await people.NoteAsync(a.Str("name") ?? "", a.Str("note") ?? "", a.Str("kind")?.ToLowerInvariant(),
                    (a.Str("related") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                return $"Noted about {e.Name}.";
            }),
    ]);
}
