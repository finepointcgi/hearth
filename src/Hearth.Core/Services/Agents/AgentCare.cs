using System.Text;

namespace Hearth.Core.Services.Agents;

/// <summary>An agent's memory file, as found on disk.</summary>
public sealed record AgentMemoryFile(string Name, string Path, bool Exists, long Bytes);

/// <summary>
/// Looking after the agents from the Agents page: their status and updates, what they remember (their own memory
/// files, which can be edited, and a section of what Hearth knows about you), and sharing Hearth's skills with them.
/// Nothing in another agent's folder changes unless the user asks, and edited files keep a backup.
/// </summary>
public sealed class AgentCare(SettingsStore settingsStore, SkillPorter porter, SkillStore skills, MemoryStore memories)
{
    public const string SectionStart = "<!-- hearth:start -->", SectionEnd = "<!-- hearth:end -->";
    private const int SectionChars = 1200;

    /// <summary>For tests: the home folder the agents live in.</summary>
    public string Home { get; set; } = AgentCatalog.HomeFolder;

    private bool _watching;

    public string HomeOf(AgentDef def) => Path.Combine(Home, def.Home);

    // ------------------------------------------------------------------ status and updates

    public static Task<(int exit, string output)> StatusAsync(AgentStatus a) =>
        a is { Path: { } cli, Def.StatusArgs: { } args } ? AgentCatalog.RunAsync(cli, args, TimeSpan.FromMinutes(1))
            : Task.FromResult((1, "This agent has no status command."));

    public static Task<(int exit, string output)> UpdateAsync(AgentStatus a) =>
        a is { Path: { } cli, Def.UpdateArgs: { } args } ? AgentCatalog.RunAsync(cli, args, TimeSpan.FromMinutes(10))
            : Task.FromResult((1, "Update this agent the way you installed it."));

    // ------------------------------------------------------------------ memory

    public IReadOnlyList<AgentMemoryFile> MemoryFiles(AgentDef def) => def.MemoryFiles.Select(rel =>
    {
        var full = Path.Combine(HomeOf(def), rel);
        var info = new FileInfo(full);
        return new AgentMemoryFile(rel, full, info.Exists, info.Exists ? info.Length : 0);
    }).ToList();

    private AgentMemoryFile FileOf(AgentDef def, string name) =>
        MemoryFiles(def).FirstOrDefault(f => f.Name == name) ?? throw new InvalidOperationException($"{name} isn't one of {def.Name}'s memory files.");

    public async Task<string> ReadAsync(AgentDef def, string name)
    {
        var f = FileOf(def, name);
        return f.Exists ? await File.ReadAllTextAsync(f.Path) : "";
    }

    /// <summary>Saves an edited memory file; the previous version is kept next to it (.hearth-backup).</summary>
    public async Task SaveAsync(AgentDef def, string name, string text)
    {
        var f = FileOf(def, name);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Path)!);
        if (f.Exists) File.Copy(f.Path, f.Path + ".hearth-backup", overwrite: true);
        var tmp = f.Path + ".hearth-tmp";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, f.Path, overwrite: true);
    }

    /// <summary>
    /// Writes what Hearth knows about the user (its shared memories, newest first, kept short) into the agent's first
    /// memory file, between markers so doing it again replaces the old section. Returns how many facts went in.
    /// </summary>
    public async Task<int> ShareMemoriesAsync(AgentDef def)
    {
        var facts = (await memories.ListAsync()).Where(m => !m.Archived).OrderByDescending(m => m.CreatedAt).Select(m => m.Text.Trim()).ToList();
        var body = new StringBuilder();
        var n = 0;
        foreach (var f in facts)
        {
            var line = "- " + f.ReplaceLineEndings(" ") + "\n";
            if (body.Length + line.Length > SectionChars) break;
            body.Append(line);
            n++;
        }
        if (n == 0) throw new InvalidOperationException("Hearth doesn't know anything about you yet.");
        var name = def.MemoryFiles[0];
        var current = await ReadAsync(def, name);
        await SaveAsync(def, name, WithSection(current, $"## About the user (from Hearth)\n{body}"));
        return n;
    }

    /// <summary>Puts <paramref name="section"/> between the Hearth markers, replacing an earlier one.</summary>
    public static string WithSection(string text, string section)
    {
        var block = $"{SectionStart}\n{section.TrimEnd()}\n{SectionEnd}";
        var start = text.IndexOf(SectionStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(SectionEnd, start, StringComparison.Ordinal);
        if (start >= 0 && end > start) return text[..start] + block + text[(end + SectionEnd.Length)..];
        return text.TrimEnd() + (text.Trim().Length > 0 ? "\n\n" : "") + block + "\n";
    }

    // ------------------------------------------------------------------ skills

    private string? SkillsDir(AgentDef def) => def.SkillsDir is null ? null : Path.Combine(HomeOf(def), def.SkillsDir);

    /// <summary>Whether Hearth's skills are linked into the agent's skills folder.</summary>
    public bool SkillsShared(AgentDef def)
    {
        if (SkillsDir(def) is not { } dir || !Directory.Exists(dir)) return false;
        return def.Skills == SkillLink.Folder ? IsOurLink(Path.Combine(dir, "hearth"))
            : Directory.EnumerateDirectories(dir, "hearth-*").Any(IsOurLink);
    }

    private bool IsOurLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.LinkTarget is { } target
               && Path.GetFullPath(target, Path.GetDirectoryName(path)!).StartsWith(Path.GetFullPath(porter.FolderPath), StringComparison.Ordinal);
    }

    /// <summary>Links Hearth's approved skills into the agent's skills folder (or removes the links). Returns how many are shared.</summary>
    public async Task<int> ShareSkillsAsync(AgentDef def, bool share)
    {
        if (def.Skills == SkillLink.None || SkillsDir(def) is not { } dir)
            throw new InvalidOperationException($"{def.Name} doesn't read skills from a folder.");
        if (!Directory.Exists(HomeOf(def))) throw new InvalidOperationException($"{def.Name} hasn't been set up yet (no {HomeOf(def)}).");
        var settings = await settingsStore.GetAsync();
        var a = settings.Agents.ByAgent.TryGetValue(def.Id, out var x) ? x : settings.Agents.ByAgent[def.Id] = new();
        a.ShareSkills = share;
        await settingsStore.SaveAsync(settings);
        return Link(def, dir, share);
    }

    private int Link(AgentDef def, string dir, bool share)
    {
        var source = porter.FolderPath;
        Directory.CreateDirectory(dir);
        if (def.Skills == SkillLink.Folder)
        {
            var link = Path.Combine(dir, "hearth");
            if (IsOurLink(link) && !share) Directory.Delete(link);
            if (!share) return 0;
            if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, source);
            else if (!IsOurLink(link)) throw new InvalidOperationException($"{link} already exists and isn't Hearth's, so it was left alone.");
            return Directory.Exists(source) ? Directory.EnumerateDirectories(source).Count() : 0;
        }
        var wanted = share && Directory.Exists(source)
            ? Directory.EnumerateDirectories(source).Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .ToDictionary(d => Path.Combine(dir, "hearth-" + Path.GetFileName(d)), d => d)
            : [];
        foreach (var existing in Directory.EnumerateDirectories(dir, "hearth-*").Where(IsOurLink))
            if (!wanted.ContainsKey(existing)) Directory.Delete(existing);
        foreach (var (link, target) in wanted)
            if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, target);
        return wanted.Count;
    }

    /// <summary>Keeps shared skills current: new skills get linked, deleted ones unlinked.</summary>
    public void EnsureWatching()
    {
        if (_watching) return;
        _watching = true;
        skills.Changed += () => _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // let the skill files be written first
            await ResyncAsync();
        });
        _ = ResyncAsync();
    }

    public async Task ResyncAsync()
    {
        try
        {
            var settings = (await settingsStore.GetAsync()).Agents;
            foreach (var def in AgentCatalog.All.Where(d => d.Skills == SkillLink.PerSkill && settings.For(d.Id).ShareSkills))
                if (SkillsDir(def) is { } dir && Directory.Exists(HomeOf(def)))
                    try { Link(def, dir, true); } catch { /* next change tries again */ }
        }
        catch { /* best effort */ }
    }
}
