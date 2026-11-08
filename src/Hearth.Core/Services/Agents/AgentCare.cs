using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Hearth.Core.Services.Agents;

/// <summary>An agent's memory file, as found on disk.</summary>
public sealed record AgentMemoryFile(string Name, string Path, bool Exists, long Bytes);

/// <summary>
/// Looking after the agents from the Agents page, on this computer or another one, in any of their profiles: their
/// status and updates, what they remember (their own memory files, which can be edited, and a section of what
/// Hearth knows about you), and sharing Hearth's skills with them. Nothing in an agent's folder changes unless the
/// user asks, and edited files keep a backup.
/// </summary>
public sealed class AgentCare(SettingsStore settingsStore, SkillPorter porter, SkillStore skills, MemoryStore memories, AgentHub hub)
{
    public const string SectionStart = "<!-- hearth:start -->", SectionEnd = "<!-- hearth:end -->";
    /// <summary>Marks skill folders Hearth copied to another computer, so only those are ever replaced or removed.</summary>
    public const string Marker = ".hearth-managed";
    private const int SectionChars = 1200;

    /// <summary>For tests: the home folder the agents live in on this computer.</summary>
    public string Home { get; set; } = AgentCatalog.HomeFolder;

    private bool _watching;

    /// <summary>Agents' files belong to whoever this computer is signed in as: only accounts allowed to use it.</summary>
    private void CheckAllowed()
    {
        if (!hub.Available) throw new InvalidOperationException("This account isn't allowed to use this computer, so it can't look after other agents. The owner can allow it in Settings → Accounts.");
    }

    public string HomeOf(AgentDef def) => HomeOf(new AgentPlace(def));
    private string HomeOf(AgentPlace place) => Path.Combine(Home, place.RelativeHome);
    private static string RemotePath(AgentPlace place, string rel) => Tools.Ssh.QuotePath($"~/{place.RelativeHome}/{rel}");

    // ------------------------------------------------------------------ status and updates

    public static Task<(int exit, string output)> StatusAsync(AgentStatus a) =>
        a is { Path: { } cli, Def.StatusArgs: { } args } ? AgentCatalog.RunAsync(cli, args, TimeSpan.FromMinutes(1))
            : Task.FromResult((1, "This agent has no status command."));

    public static Task<(int exit, string output)> UpdateAsync(AgentStatus a) =>
        a is { Path: { } cli, Def.UpdateArgs: { } args } ? AgentCatalog.RunAsync(cli, args, TimeSpan.FromMinutes(10))
            : Task.FromResult((1, "Update this agent the way you installed it."));

    // ------------------------------------------------------------------ memory

    public IReadOnlyList<AgentMemoryFile> MemoryFiles(AgentDef def) => LocalFiles(new AgentPlace(def));

    private IReadOnlyList<AgentMemoryFile> LocalFiles(AgentPlace place) => place.Def.MemoryFiles.Select(rel =>
    {
        var full = Path.Combine(HomeOf(place), rel);
        var info = new FileInfo(full);
        return new AgentMemoryFile(rel, full, info.Exists, info.Exists ? info.Length : 0);
    }).ToList();

    public async Task<IReadOnlyList<AgentMemoryFile>> MemoryFilesAsync(AgentPlace place)
    {
        CheckAllowed();
        if (place.Host is not { } host) return LocalFiles(place);
        var script = string.Concat(place.Def.MemoryFiles.Select(rel =>
            $"f={RemotePath(place, rel)}; if [ -f \"$f\" ]; then echo \"@@{rel}|$(wc -c < \"$f\")\"; else echo \"@@{rel}|-\"; fi; ")) + "echo @@end";
        var (_, output) = await hub.NodeRunner(host, script, TimeSpan.FromSeconds(30));
        if (!output.Contains("@@end")) throw new InvalidOperationException($"Couldn't reach {host.Name}: {Clip(output.Trim(), 300)}");
        var sizes = output.Split('\n').Where(l => l.StartsWith("@@") && l.Contains('|')).Select(l => l[2..].Split('|', 2))
            .ToDictionary(p => p[0], p => p[1].Trim());
        return place.Def.MemoryFiles.Select(rel => sizes.TryGetValue(rel, out var size) && long.TryParse(size, out var n)
            ? new AgentMemoryFile(rel, $"{host.Name}:~/{place.RelativeHome}/{rel}", true, n)
            : new AgentMemoryFile(rel, $"{host.Name}:~/{place.RelativeHome}/{rel}", false, 0)).ToList();
    }

    private static void CheckName(AgentPlace place, string name)
    {
        if (!place.Def.MemoryFiles.Contains(name)) throw new InvalidOperationException($"{name} isn't one of {place.Def.Name}'s memory files.");
    }

    public Task<string> ReadAsync(AgentDef def, string name) => ReadAsync(new AgentPlace(def), name);

    public async Task<string> ReadAsync(AgentPlace place, string name)
    {
        CheckAllowed();
        CheckName(place, name);
        if (place.Host is not { } host)
        {
            var full = Path.Combine(HomeOf(place), name);
            return File.Exists(full) ? await File.ReadAllTextAsync(full) : "";
        }
        var (_, output) = await hub.NodeRunner(host, $"f={RemotePath(place, name)}; echo @@start; [ -f \"$f\" ] && cat \"$f\"; echo; echo @@end", TimeSpan.FromSeconds(30));
        var start = output.IndexOf("@@start\n", StringComparison.Ordinal);
        var end = output.LastIndexOf("\n@@end", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException($"Couldn't read it from {host.Name}: {Clip(output.Trim(), 300)}");
        var text = output[(start + 8)..end];
        return text.EndsWith('\n') ? text[..^1] : text; // the echo that ends a file without a newline
    }

    public Task SaveAsync(AgentDef def, string name, string text) => SaveAsync(new AgentPlace(def), name, text);

    /// <summary>Saves an edited memory file; the previous version is kept next to it (.hearth-backup).</summary>
    public async Task SaveAsync(AgentPlace place, string name, string text)
    {
        CheckAllowed();
        CheckName(place, name);
        if (place.Host is not { } host)
        {
            var full = Path.Combine(HomeOf(place), name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full)) File.Copy(full, full + ".hearth-backup", overwrite: true);
            var tmp = full + ".hearth-tmp";
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            return;
        }
        var f = RemotePath(place, name);
        var command = $"f={f}; mkdir -p \"$(dirname \"$f\")\" && {{ [ ! -f \"$f\" ] || cp \"$f\" \"$f.hearth-backup\"; }} && cat > \"$f.hearth-tmp\" && mv \"$f.hearth-tmp\" \"$f\" && echo @@saved";
        var (_, output) = await hub.NodeSendAsync(host, command, new UTF8Encoding(false).GetBytes(text), TimeSpan.FromMinutes(1));
        if (!output.Contains("@@saved")) throw new InvalidOperationException($"Couldn't save it on {host.Name}: {Clip(output, 300)}");
    }

    public Task<int> ShareMemoriesAsync(AgentDef def) => ShareMemoriesAsync(new AgentPlace(def));

    /// <summary>
    /// Writes what Hearth knows about the user (its shared memories, newest first, kept short) into the agent's first
    /// memory file, between markers so doing it again replaces the old section. Returns how many facts went in.
    /// </summary>
    public async Task<int> ShareMemoriesAsync(AgentPlace place)
    {
        CheckAllowed();
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
        var name = place.Def.MemoryFiles[0];
        var current = await ReadAsync(place, name);
        await SaveAsync(place, name, WithSection(current, $"## About the user (from Hearth)\n{body}"));
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

    private string? LocalSkillsDir(AgentPlace place) => place.Def.SkillsDir is null ? null : Path.Combine(HomeOf(place), place.Def.SkillsDir);

    public bool SkillsShared(AgentDef def) => LocalShared(new AgentPlace(def));

    private bool LocalShared(AgentPlace place)
    {
        if (LocalSkillsDir(place) is not { } dir || !Directory.Exists(dir)) return false;
        return place.Def.Skills == SkillLink.Folder ? IsOurLink(Path.Combine(dir, "hearth"))
            : Directory.EnumerateDirectories(dir, "hearth-*").Any(IsOurLink);
    }

    /// <summary>Whether Hearth's skills are shared with the agent there.</summary>
    public async Task<bool> SkillsSharedAsync(AgentPlace place) =>
        place.Remote ? (await settingsStore.GetAsync()).Agents.For(place.Def.Id).SharedSkillsAt.Contains(place.Key) : LocalShared(place);

    private bool IsOurLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.LinkTarget is { } target
               && Path.GetFullPath(target, Path.GetDirectoryName(path)!).StartsWith(Path.GetFullPath(porter.FolderPath), StringComparison.Ordinal);
    }

    public Task<int> ShareSkillsAsync(AgentDef def, bool share) => ShareSkillsAsync(new AgentPlace(def), share);

    /// <summary>
    /// Shares Hearth's approved skills with the agent there, or stops. On this computer they're linked into its skills
    /// folder; on another computer they're copied (and copied again whenever they change). Returns how many are shared.
    /// </summary>
    public async Task<int> ShareSkillsAsync(AgentPlace place, bool share)
    {
        CheckAllowed();
        if (place.Def.Skills == SkillLink.None || place.Def.SkillsDir is null)
            throw new InvalidOperationException($"{place.Def.Name} doesn't read skills from a folder.");
        int count;
        if (place.Host is null)
        {
            if (!Directory.Exists(HomeOf(place))) throw new InvalidOperationException($"{place.Label} hasn't been set up yet (no {HomeOf(place)}).");
            count = Link(place, LocalSkillsDir(place)!, share);
        }
        else count = await PushAsync(place, share);
        var settings = await settingsStore.GetAsync();
        var a = settings.Agents.ByAgent.TryGetValue(place.Def.Id, out var x) ? x : settings.Agents.ByAgent[place.Def.Id] = new();
        a.SharedSkillsAt.Remove(place.Key);
        if (share) a.SharedSkillsAt.Add(place.Key);
        if (place.Key == "local") a.ShareSkills = share;
        await settingsStore.SaveAsync(settings);
        return count;
    }

    private int Link(AgentPlace place, string dir, bool share)
    {
        var source = porter.FolderPath;
        Directory.CreateDirectory(dir);
        if (place.Def.Skills == SkillLink.Folder)
        {
            var link = Path.Combine(dir, "hearth");
            if (IsOurLink(link) && !share) Directory.Delete(link);
            if (!share) return 0;
            if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, source);
            else if (!IsOurLink(link)) throw new InvalidOperationException($"{link} already exists and isn't Hearth's, so it was left alone.");
            return Directory.Exists(source) ? Directory.EnumerateDirectories(source).Count() : 0;
        }
        var wanted = share ? SkillFolders().ToDictionary(d => Path.Combine(dir, "hearth-" + Path.GetFileName(d)), d => d) : [];
        foreach (var existing in Directory.EnumerateDirectories(dir, "hearth-*").Where(IsOurLink))
            if (!wanted.ContainsKey(existing)) Directory.Delete(existing);
        foreach (var (link, target) in wanted)
            if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, target);
        return wanted.Count;
    }

    private IEnumerable<string> SkillFolders() => Directory.Exists(porter.FolderPath)
        ? Directory.EnumerateDirectories(porter.FolderPath).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).Order()
        : [];

    /// <summary>Copies the skills to another computer (replacing Hearth's earlier copies there), or removes them.</summary>
    private async Task<int> PushAsync(AgentPlace place, bool share)
    {
        var host = place.Host!;
        var home = Tools.Ssh.QuotePath("~/" + place.RelativeHome);
        var dir = RemotePath(place, place.Def.SkillsDir!);
        var clear = $"[ -d {home} ] || {{ echo @@nohome; exit 0; }}; mkdir -p {dir} && cd {dir} && " +
                    $"for d in hearth hearth-*; do [ -f \"$d/{Marker}\" ] && rm -rf -- \"$d\"; done; ";
        if (!share)
        {
            var (_, gone) = await hub.NodeRunner(host, clear + "echo @@done", TimeSpan.FromMinutes(1));
            if (gone.Contains("@@nohome") || gone.Contains("@@done")) return 0;
            throw new InvalidOperationException($"Couldn't reach {host.Name}: {Clip(gone, 300)}");
        }
        var (bundle, count) = Bundle(place.Def.Skills);
        var (_, output) = await hub.NodeSendAsync(host, clear + "tar -xzf - && echo @@done", bundle, TimeSpan.FromMinutes(2));
        if (output.Contains("@@nohome")) throw new InvalidOperationException($"{place.Label} hasn't been set up yet (no ~/{place.RelativeHome} there).");
        if (!output.Contains("@@done")) throw new InvalidOperationException($"Couldn't copy the skills to {host.Name}: {Clip(output, 300)}");
        return count;
    }

    /// <summary>The skills as a .tar.gz laid out the way the agent reads them, each folder marked as Hearth's.</summary>
    public (byte[] bundle, int count) Bundle(SkillLink style)
    {
        using var buffer = new MemoryStream();
        var count = 0;
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            void Marked(string folder)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{folder}/{Marker}") { DataStream = new MemoryStream("Copied by Hearth; replaced when skills change.\n"u8.ToArray()) };
                tar.WriteEntry(entry);
            }
            if (style == SkillLink.Folder) Marked("hearth");
            foreach (var skill in SkillFolders())
            {
                var folder = style == SkillLink.Folder ? $"hearth/{Path.GetFileName(skill)}" : $"hearth-{Path.GetFileName(skill)}";
                if (style == SkillLink.PerSkill) Marked(folder);
                foreach (var file in Directory.EnumerateFiles(skill, "*", SearchOption.AllDirectories))
                    tar.WriteEntry(file, $"{folder}/{Path.GetRelativePath(skill, file).Replace('\\', '/')}");
                count++;
            }
        }
        return (buffer.ToArray(), count);
    }

    /// <summary>Keeps shared skills current: new skills get linked or copied, deleted ones removed.</summary>
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
        if (!hub.Available) return;
        try
        {
            var settings = await settingsStore.GetAsync();
            foreach (var def in AgentCatalog.All.Where(d => d.Skills != SkillLink.None))
            {
                var a = settings.Agents.For(def.Id);
                var places = a.SharedSkillsAt.ToList();
                if (a.ShareSkills && !places.Contains("local")) places.Add("local"); // shared before computers and profiles existed
                foreach (var key in places)
                    try
                    {
                        var slash = key.IndexOf('/');
                        var where = slash < 0 ? key : key[..slash];
                        var profile = slash < 0 ? null : key[(slash + 1)..];
                        var host = where == "local" ? null : settings.RemoteHosts.FirstOrDefault(h => h.Id == where);
                        if (where != "local" && host is null) continue; // that computer was removed
                        var place = new AgentPlace(def, host, profile);
                        if (host is null)
                        {
                            if (def.Skills == SkillLink.PerSkill && Directory.Exists(HomeOf(place))) Link(place, LocalSkillsDir(place)!, true);
                        }
                        else await PushAsync(place, true);
                    }
                    catch { /* next change tries again */ }
            }
        }
        catch { /* best effort */ }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
