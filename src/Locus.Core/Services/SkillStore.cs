using Locus.Core.Models;

namespace Locus.Core.Services;

public sealed class SkillStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Skill>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "skills.json");

    public event Action? Changed;

    public async Task<List<Skill>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return (_cache ??= await Json.ReadAsync<List<Skill>>(FilePath) ?? []).OrderBy(s => s.Name).ToList(); }
        finally { _lock.Release(); }
    }

    /// <summary>Skills bots may see and use (approved ones).</summary>
    public async Task<List<Skill>> ActiveAsync() => (await ListAsync()).Where(s => !s.Pending).ToList();

    public async Task<Skill?> FindAsync(string name)
    {
        var n = Skill.Slug(name.TrimStart('/'));
        return (await ListAsync()).FirstOrDefault(s => s.Name == n);
    }

    public async Task RecordUseAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var list = _cache ??= await Json.ReadAsync<List<Skill>>(FilePath) ?? [];
            if (list.FirstOrDefault(s => s.Id == id) is not { } skill) return;
            skill.Uses++;
            skill.LastUsed = DateTimeOffset.Now;
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
    }

    /// <summary>A reply that followed the skill got 👎: counts it and keeps what was said, until the skill is changed.</summary>
    public async Task RecordWrongAsync(string id, string feedback)
    {
        await _lock.WaitAsync();
        try
        {
            var list = _cache ??= await Json.ReadAsync<List<Skill>>(FilePath) ?? [];
            if (list.FirstOrDefault(s => s.Id == id) is not { } skill) return;
            skill.Wrong++;
            skill.WrongNotes.Add(feedback.Length <= 300 ? feedback : feedback[..300] + "…");
            if (skill.WrongNotes.Count > 5) skill.WrongNotes.RemoveRange(0, skill.WrongNotes.Count - 5);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
    }

    /// <summary>"Keep it": stops asking about an unused skill for a while.</summary>
    public async Task KeepAsync(Skill skill)
    {
        skill.KeptAt = DateTimeOffset.Now;
        await SaveAsync(skill);
    }

    /// <summary>"Looks fine": clears the 👎 notes without changing the skill.</summary>
    public async Task ClearWrongAsync(Skill skill)
    {
        skill.WrongNotes.Clear();
        await SaveAsync(skill);
    }

    /// <summary>Replaces a skill's instructions, keeping the old version so it can be restored.</summary>
    public async Task ReviseAsync(Skill skill, string instructions, string? description, string? note, string? by, bool pending)
    {
        if (skill.Instructions.Trim() != instructions.Trim())
        {
            skill.Revisions.Insert(0, new SkillRevision(skill.Instructions, skill.UpdatedAt, note, by));
            if (skill.Revisions.Count > 5) skill.Revisions.RemoveRange(5, skill.Revisions.Count - 5);
        }
        skill.Instructions = instructions.Trim();
        skill.WrongNotes.Clear(); // a new version answers the complaints about the old one
        if (!string.IsNullOrWhiteSpace(description)) skill.Description = description.Trim();
        skill.UpdatedAt = DateTimeOffset.Now;
        skill.Pending = skill.Pending || pending;
        await SaveAsync(skill);
    }

    /// <summary>Puts back the most recent earlier version.</summary>
    public async Task UndoAsync(Skill skill)
    {
        if (skill.Revisions.Count == 0) return;
        var last = skill.Revisions[0];
        skill.Revisions.RemoveAt(0);
        skill.Instructions = last.Instructions;
        skill.UpdatedAt = DateTimeOffset.Now;
        await SaveAsync(skill);
    }

    public async Task SaveAsync(Skill skill)
    {
        skill.Name = Skill.Slug(skill.Name);
        if (skill.Name.Length == 0) throw new ArgumentException("Give the skill a name (letters, digits, dashes).");
        if (skill.Name is "imagine") throw new ArgumentException("/imagine is reserved.");
        await _lock.WaitAsync();
        try
        {
            var list = _cache ??= await Json.ReadAsync<List<Skill>>(FilePath) ?? [];
            if (list.Any(s => s.Id != skill.Id && s.Name == skill.Name))
                throw new ArgumentException($"There's already a /{skill.Name} skill.");
            var i = list.FindIndex(s => s.Id == skill.Id);
            if (i >= 0) list[i] = skill; else list.Add(skill);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var list = _cache ??= await Json.ReadAsync<List<Skill>>(FilePath) ?? [];
            list.RemoveAll(s => s.Id == id);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }
}
