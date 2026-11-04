using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Reads and writes skills in the portable SKILL.md format (YAML frontmatter with name/description, then Markdown
/// instructions) used by Claude, Hermes and OpenClaw, so skills move between them. Imported skills always wait for
/// review and are scanned for risky instructions.
/// </summary>
public sealed partial class SkillPorter(SkillStore store, IHttpClientFactory http, IAppPaths paths)
{
    private bool _started;

    /// <summary>Keeps the skills folder in sync from now on.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        store.Changed += () => _ = MirrorSafeAsync();
        _ = MirrorSafeAsync();
    }

    private async Task MirrorSafeAsync()
    {
        try { await MirrorAsync(); } catch { /* best effort */ }
    }

    private const int MaxFileBytes = 200_000;
    private const int MaxFiles = 40;

    // ------------------------------------------------------------------ format

    public static string ToMarkdown(Skill s)
    {
        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(s.Name).Append('\n');
        sb.Append("description: ").Append(Quote(s.Description)).Append('\n');
        sb.Append("metadata:\n  icon: ").Append(Quote(s.Icon)).Append('\n');
        if (s.CreatedBy is not null) sb.Append("  author: ").Append(Quote(s.CreatedBy == "locus" ? "Locus" : "bot:" + s.CreatedBy)).Append('\n');
        if (s.Uses > 0) sb.Append("  uses: ").Append(s.Uses).Append('\n');
        sb.Append("---\n\n").Append(s.Instructions.Trim()).Append('\n');
        return sb.ToString();
    }

    private static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

    /// <summary>Parses SKILL.md text. Without frontmatter, the first heading becomes the name.</summary>
    public static Skill Parse(string text, string? fallbackName = null)
    {
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        if (text.StartsWith("---\n"))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                ParseYaml(text[4..end], meta);
                body = text[(end + 4)..].TrimStart('-').Trim('\n');
            }
        }
        var heading = Heading().Match(body);
        var name = meta.GetValueOrDefault("name") ?? fallbackName ?? (heading.Success ? heading.Groups[1].Value : "imported-skill");
        var description = meta.GetValueOrDefault("description") ?? FirstSentence(body);
        return new Skill
        {
            Name = Skill.Slug(name).Length > 0 ? Skill.Slug(name) : "imported-skill",
            Description = description.Length > 300 ? description[..300] : description,
            Icon = meta.GetValueOrDefault("icon") is { Length: > 0 and <= 8 } icon ? icon : "📦",
            Instructions = body.Trim(),
        };
    }

    /// <summary>Just enough YAML for skill frontmatter: key: value, quoted values, block scalars, nested maps (flattened).</summary>
    private static void ParseYaml(string yaml, Dictionary<string, string> into)
    {
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = YamlLine().Match(lines[i]);
            if (!m.Success) continue;
            var key = m.Groups[2].Value;
            var value = m.Groups[3].Value.Trim();
            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var indent = m.Groups[1].Value.Length;
                var parts = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent))
                    parts.Add(lines[++i].Trim());
                value = string.Join(value.StartsWith('|') ? "\n" : " ", parts).Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1].Replace("\\\"", "\"").Replace("''", "'");
            if (value.Length > 0) into.TryAdd(key, value);
        }
    }

    private static string FirstSentence(string body)
    {
        var line = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? line[..(dot + 1)] : line;
    }

    // ------------------------------------------------------------------ safety scan

    private static readonly (Regex pattern, string warning)[] Risks =
    [
        (new(@"(curl|wget)[^\n|]*\|\s*(ba|z)?sh", RegexOptions.IgnoreCase), "Downloads a script and runs it (curl … | sh)."),
        (new(@"base64\s+(-d|--decode)|frombase64string|atob\(", RegexOptions.IgnoreCase), "Decodes hidden (base64) content."),
        (new(@"rm\s+-rf\s+[/~]", RegexOptions.IgnoreCase), "Deletes files recursively."),
        (new(@"ignore (all )?(previous|prior|above) instructions|disregard (your|the) (rules|instructions)", RegexOptions.IgnoreCase), "Tries to override the bot's instructions."),
        (new(@"(api[_ -]?key|password|token|secret|private key|seed phrase|\.ssh|id_rsa|keychain|cookies?)\b[^\n]{0,80}\b(send|post|upload|paste|email|share)", RegexOptions.IgnoreCase), "Talks about sending credentials or keys somewhere."),
        (new(@"(send|post|upload|exfiltrate)[^\n]{0,60}\b(api[_ -]?key|password|token|secret|cookies?|\.ssh)", RegexOptions.IgnoreCase), "Talks about sending credentials or keys somewhere."),
        (new(@"(pastebin|transfer\.sh|ngrok|webhook\.site|requestbin|discord(app)?\.com/api/webhooks)", RegexOptions.IgnoreCase), "Mentions a data-drop or tunnel service."),
        (new(@"(crypto ?wallet|bitcoin|ethereum|wire transfer|gift card)", RegexOptions.IgnoreCase), "Mentions moving money or crypto."),
        (new(@"\b(chmod\s+\+x|sudo\s|launchctl|crontab|systemctl enable|reg add)\b", RegexOptions.IgnoreCase), "Changes system settings or installs something that persists."),
        (new(@"[​-‏‪-‮⁠-⁤]"), "Contains invisible characters (can hide instructions)."),
    ];

    public static List<string> Scan(Skill s)
    {
        var all = s.Instructions + "\n" + string.Join("\n", s.Files.Values);
        var found = Risks.Where(r => r.pattern.IsMatch(all)).Select(r => r.warning).Distinct().ToList();
        if (s.Files.Keys.Any(f => f.EndsWith(".sh") || f.EndsWith(".py") || f.EndsWith(".js") || f.EndsWith(".ps1")))
            found.Add("Includes scripts: " + string.Join(", ", s.Files.Keys.Where(f => f.EndsWith(".sh") || f.EndsWith(".py") || f.EndsWith(".js") || f.EndsWith(".ps1"))) + ". Read them before approving.");
        return found;
    }

    // ------------------------------------------------------------------ import

    /// <summary>Imports a SKILL.md text. The skill waits for review; a name clash gets a suffix.</summary>
    public async Task<Skill> ImportTextAsync(string text, string source, Dictionary<string, string>? files = null, string? fallbackName = null)
    {
        var skill = Parse(text, fallbackName);
        if (skill.Instructions.Length < 10) throw new InvalidOperationException("That doesn't look like a skill (no instructions found).");
        skill.Files = files ?? [];
        skill.Source = source;
        skill.Pending = true;
        skill.CreatedBy = null;
        skill.Warnings = Scan(skill);
        var existing = (await store.ListAsync()).Select(s => s.Name).ToHashSet();
        var baseName = skill.Name;
        for (var n = 2; existing.Contains(skill.Name); n++) skill.Name = $"{baseName}-{n}";
        await store.SaveAsync(skill);
        return skill;
    }

    /// <summary>Imports from a .zip holding one or more skill folders (each with a SKILL.md).</summary>
    public async Task<List<Skill>> ImportZipAsync(Stream zip, string source)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var result = new List<Skill>();
        var skillFiles = archive.Entries.Where(e => e.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)).Take(50).ToList();
        if (skillFiles.Count == 0) throw new InvalidOperationException("No SKILL.md found in that zip.");
        foreach (var entry in skillFiles)
        {
            var folder = entry.FullName[..^entry.Name.Length];
            var files = new Dictionary<string, string>();
            foreach (var f in archive.Entries.Where(e => e.FullName.StartsWith(folder) && e != entry && e.Length is > 0 and <= MaxFileBytes && IsText(e.Name)).Take(MaxFiles))
                files[f.FullName[folder.Length..]] = await ReadAsync(f);
            var folderName = folder.TrimEnd('/').Split('/').LastOrDefault();
            result.Add(await ImportTextAsync(await ReadAsync(entry), source, files, folderName));
        }
        return result;
    }

    private static async Task<string> ReadAsync(ZipArchiveEntry e)
    {
        await using var s = e.Open();
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    private static bool IsText(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".md" or ".txt" or ".py" or ".sh" or ".js" or ".ts" or ".json" or ".yaml" or ".yml"
            or ".csv" or ".html" or ".css" or ".xml" or ".toml" or ".ps1" or ".sql" or "";

    /// <summary>
    /// Imports from a link: a raw SKILL.md URL, a GitHub file or folder URL
    /// (github.com/owner/repo/tree/branch/path), or a .zip URL.
    /// </summary>
    public async Task<List<Skill>> ImportUrlAsync(string url, CancellationToken ct = default)
    {
        var client = http.CreateClient(Llm.LlmClientFactory.HttpClientName);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Locus-skill-import");
        url = url.Trim();
        var gh = GitHubPath().Match(url);
        if (gh.Success)
        {
            var (owner, repo, kind, branch, path) = (gh.Groups[1].Value, gh.Groups[2].Value, gh.Groups[3].Value, gh.Groups[4].Value, gh.Groups[5].Value.Trim('/'));
            if (kind == "blob")
                return [await ImportTextAsync(await client.GetStringAsync($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}", ct), url,
                    fallbackName: path.Split('/').SkipLast(1).LastOrDefault())];
            return await ImportGitHubFolderAsync(client, owner, repo, branch, path, url, ct);
        }
        using var res = await client.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || res.Content.Headers.ContentType?.MediaType == "application/zip")
        {
            await using var ms = new MemoryStream(await res.Content.ReadAsByteArrayAsync(ct));
            return await ImportZipAsync(ms, url);
        }
        var text = await res.Content.ReadAsStringAsync(ct);
        if (text.TrimStart().StartsWith('<')) throw new InvalidOperationException("That link is a web page, not a SKILL.md file. Use the raw file or GitHub folder link.");
        return [await ImportTextAsync(text, url, fallbackName: url.Split('/').SkipLast(1).LastOrDefault())];
    }

    private async Task<List<Skill>> ImportGitHubFolderAsync(HttpClient client, string owner, string repo, string branch, string path,
        string source, CancellationToken ct)
    {
        async Task<JsonArray> List(string p) =>
            await client.GetFromJsonAsync<JsonArray>($"https://api.github.com/repos/{owner}/{repo}/contents/{p}?ref={branch}", ct) ?? [];
        var items = await List(path);
        // A folder of skill folders: import each one that has a SKILL.md.
        if (!items.Any(i => i?["name"]?.ToString() == "SKILL.md"))
        {
            var result = new List<Skill>();
            foreach (var dir in items.Where(i => i?["type"]?.ToString() == "dir").Take(30))
                if ((await List(dir!["path"]!.ToString())).Any(i => i?["name"]?.ToString() == "SKILL.md"))
                    result.AddRange(await ImportGitHubFolderAsync(client, owner, repo, branch, dir["path"]!.ToString(), source, ct));
            if (result.Count == 0) throw new InvalidOperationException("No SKILL.md in that folder.");
            return result;
        }
        var files = new Dictionary<string, string>();
        string? skillText = null;
        async Task Walk(JsonArray list, string prefix, int depth)
        {
            foreach (var i in list)
            {
                if (files.Count >= MaxFiles || i is null) break;
                var name = i["name"]!.ToString();
                if (i["type"]?.ToString() == "dir" && depth < 2) await Walk(await List(i["path"]!.ToString()), prefix + name + "/", depth + 1);
                else if (i["type"]?.ToString() == "file" && (i["size"]?.GetValue<long>() ?? 0) <= MaxFileBytes && IsText(name) && i["download_url"]?.ToString() is { } dl)
                {
                    var content = await client.GetStringAsync(dl, ct);
                    if (prefix.Length == 0 && name == "SKILL.md") skillText = content; else files[prefix + name] = content;
                }
            }
        }
        await Walk(items, "", 0);
        return [await ImportTextAsync(skillText!, source, files, path.Split('/').LastOrDefault())];
    }

    // ------------------------------------------------------------------ export

    /// <summary>A zip with one folder per skill (SKILL.md plus its files), readable by Claude, Hermes and OpenClaw.</summary>
    public static byte[] ExportZip(IEnumerable<Skill> skills)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var s in skills)
            {
                Write(zip, $"{s.Name}/SKILL.md", ToMarkdown(s));
                foreach (var (path, content) in s.Files) Write(zip, $"{s.Name}/{path}", content);
            }
        return ms.ToArray();

        static void Write(ZipArchive zip, string path, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open());
            w.Write(content);
        }
    }

    /// <summary>The skills folder other agents can read (kept in sync with Locus's approved skills).</summary>
    public string FolderPath => Path.Combine(paths.DataDirectory, "skills");

    public async Task MirrorAsync()
    {
        var dir = FolderPath;
        Directory.CreateDirectory(dir);
        var active = await store.ActiveAsync();
        foreach (var s in active)
        {
            var folder = Path.Combine(dir, s.Name);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), ToMarkdown(s));
            foreach (var (path, content) in s.Files)
            {
                var full = Path.GetFullPath(Path.Combine(folder, path));
                if (!full.StartsWith(Path.GetFullPath(folder))) continue; // no "../" escapes
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content);
            }
        }
        // Remove folders of skills that were deleted (only ones Locus wrote: they have a SKILL.md).
        foreach (var folder in Directory.EnumerateDirectories(dir))
            if (active.All(s => s.Name != Path.GetFileName(folder)) && File.Exists(Path.Combine(folder, "SKILL.md")))
                Directory.Delete(folder, true);
    }

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)([A-Za-z0-9_-]+):\s*(.*)$")] private static partial Regex YamlLine();
    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/(tree|blob)/([^/]+)/?(.*)$")] private static partial Regex GitHubPath();
}
