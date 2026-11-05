using System.Text.RegularExpressions;

namespace Locus.Core.Services;

/// <summary>Another agent's data found on this computer.</summary>
public sealed record ImportSource(string Name, string Root, List<string> SkillRoots, List<string> MemoryFiles, string? SoulFile);

public sealed record ImportSkill(string Name, string Description, string Path, List<string> Warnings, bool Exists);

public sealed record ImportPreview(ImportSource Source, List<ImportSkill> Skills, List<string> Memories, string? Soul);

/// <summary>
/// Brings skills, memories and the personality file over from other agents on this computer — Hermes
/// (~/.hermes), OpenClaw (~/.openclaw), Claude Code (~/.claude) and Codex (~/.codex) — or any folder.
/// Skills arrive waiting for review (and scanned for risky instructions, as every import is); memories that
/// Locus already knows are skipped; nothing is imported without a preview first.
/// </summary>
public sealed partial class AgentImporter(SkillPorter porter, SkillStore skills, MemoryStore memory, ProjectContext soul)
{
    public const int MaxSkills = 500;
    private const int MaxSkillFileBytes = 200_000;
    private const int MaxMemoryChars = 600;

    /// <summary>Folders inside an agent's data that hold its own code or caches, not the user's skills.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "hermes-agent", "venv", ".venv", "cache", "sessions", "logs", "sandboxes", "bin", "node",
    };

    /// <summary>The agents found in the user's home folder (only ones with something to import).</summary>
    public List<ImportSource> Detect(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<ImportSource>();
        void Add(string name, string dir, string[] skillDirs, string[] memoryFiles, string[] soulFiles)
        {
            var root = Path.Combine(home, dir);
            if (!Directory.Exists(root)) return;
            var source = new ImportSource(name, root,
                skillDirs.Select(d => Path.Combine(root, d)).Where(Directory.Exists).ToList(),
                memoryFiles.Select(f => Path.Combine(root, f)).Where(File.Exists).ToList(),
                soulFiles.Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists));
            if (source.SkillRoots.Count > 0 || source.MemoryFiles.Count > 0 || source.SoulFile is not null) found.Add(source);
        }
        Add("Hermes", ".hermes", ["skills"], ["memories/MEMORY.md", "memories/USER.md"], ["SOUL.md"]);
        Add("OpenClaw", ".openclaw", ["skills", "workspace/skills"],
            ["workspace/MEMORY.md", "workspace/USER.md", "workspace/memory.md"], ["workspace/SOUL.md", "SOUL.md"]);
        Add("Claude Code", ".claude", ["skills"], [], []);
        Add("Codex", ".codex", ["skills"], [], []);
        return found;
    }

    /// <summary>A folder picked by the user: skills anywhere under it, memory and SOUL files at its top.</summary>
    public static ImportSource FromFolder(string folder) => new(Path.GetFileName(folder.TrimEnd('/')), folder, [folder],
        new[] { "MEMORY.md", "USER.md", "memories/MEMORY.md", "memories/USER.md" }.Select(f => Path.Combine(folder, f)).Where(File.Exists).ToList(),
        new[] { "SOUL.md" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists));

    public async Task<ImportPreview> PreviewAsync(ImportSource source)
    {
        var existing = (await skills.ListAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<ImportSkill>();
        foreach (var file in SkillFiles(source).Take(MaxSkills))
        {
            try
            {
                var parsed = SkillPorter.Parse(await File.ReadAllTextAsync(file), Path.GetFileName(Path.GetDirectoryName(file)));
                parsed.Files = BundledFiles(file);
                list.Add(new ImportSkill(parsed.Name, parsed.Description, file, SkillPorter.Scan(parsed), existing.Contains(parsed.Name)));
            }
            catch (Exception) { /* unreadable skill: skip */ }
        }
        var memories = new List<string>();
        foreach (var f in source.MemoryFiles)
            foreach (var fact in ParseMemories(await File.ReadAllTextAsync(f)))
                if (!await memory.KnowsAsync(fact, null) && !memories.Contains(fact)) memories.Add(fact);
        var soulText = source.SoulFile is null ? null : (await File.ReadAllTextAsync(source.SoulFile)).Trim();
        return new ImportPreview(source, list.OrderBy(s => s.Warnings.Count > 0).ThenBy(s => s.Name).ToList(), memories,
            string.IsNullOrWhiteSpace(soulText) ? null : soulText);
    }

    /// <summary>Imports what the user ticked. Returns (skills, memories, soul imported).</summary>
    public async Task<(int skills, int memories, bool soul)> ImportAsync(ImportPreview preview, IEnumerable<string> skillPaths,
        IEnumerable<string> facts, bool takeSoul)
    {
        var allowed = preview.Skills.Select(s => s.Path).ToHashSet();
        var skillCount = 0;
        foreach (var path in skillPaths.Where(allowed.Contains))
        {
            var text = await File.ReadAllTextAsync(path);
            await porter.ImportTextAsync(text, $"{preview.Source.Name}: {path}", BundledFiles(path), Path.GetFileName(Path.GetDirectoryName(path)));
            skillCount++;
        }
        var memoryCount = 0;
        foreach (var fact in facts.Where(preview.Memories.Contains))
        {
            if (await memory.KnowsAsync(fact, null)) continue;
            await memory.AddAsync(fact);
            memoryCount++;
        }
        var soulDone = false;
        if (takeSoul && preview.Soul is not null)
        {
            await soul.SaveSoulAsync(preview.Soul);
            soulDone = true;
        }
        return (skillCount, memoryCount, soulDone);
    }

    private static IEnumerable<string> SkillFiles(ImportSource source)
    {
        var seen = new HashSet<string>();
        foreach (var root in source.SkillRoots)
        {
            var stack = new Stack<(string dir, int depth)>([(root, 0)]);
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                var skill = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skill) && new FileInfo(skill).Length < MaxSkillFileBytes && seen.Add(skill)) { yield return skill; continue; }
                if (depth >= 4) continue;
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir).Order().Reverse().ToList(); } catch { continue; }
                foreach (var d in subdirs)
                    if (!SkipDirs.Contains(Path.GetFileName(d)) && new DirectoryInfo(d).LinkTarget is null) stack.Push((d, depth + 1));
            }
        }
    }

    /// <summary>Text files that come with a skill (scripts, references), by relative path.</summary>
    private static Dictionary<string, string> BundledFiles(string skillFile)
    {
        var dir = Path.GetDirectoryName(skillFile)!;
        var files = new Dictionary<string, string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
        {
            if (f == skillFile || files.Count >= 40) continue;
            var info = new FileInfo(f);
            if (info.Length > MaxSkillFileBytes || !TextExtractor.IsSupported(f) && Path.GetExtension(f) is not (".sh" or ".ps1")) continue;
            files[Path.GetRelativePath(dir, f).Replace('\\', '/')] = File.ReadAllText(f);
        }
        return files;
    }

    /// <summary>
    /// Facts from a MEMORY.md / USER.md: bullet points, "§"-separated entries or short paragraphs; headings, empty
    /// lines and very long entries are left out.
    /// </summary>
    public static List<string> ParseMemories(string text)
    {
        var parts = text.Contains('§') ? text.Split('§') : Paragraphs().Split(text);
        var facts = new List<string>();
        foreach (var part in parts)
        {
            var lines = part.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l != "---").ToList();
            if (lines.Count == 0) continue;
            var bullets = lines.Where(l => Bullet().IsMatch(l)).ToList();
            var items = bullets.Count > 0 ? bullets.Select(l => Bullet().Replace(l, "")) : [string.Join(" ", lines)];
            foreach (var item in items.Select(i => i.Trim()))
                if (item.Length is >= 4 and <= MaxMemoryChars && !facts.Contains(item)) facts.Add(item);
        }
        return facts;
    }

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex Paragraphs();
    [GeneratedRegex(@"^([-*•]|\d+[.)])\s+")] private static partial Regex Bullet();
}
