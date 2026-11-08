using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Hearth.Core.Services;

public sealed record CatalogSkill(string Source, string Repo, string Dir, string Name, string Category)
{
    public string GitHubUrl => $"https://github.com/{Repo}/tree/HEAD/{Dir}";
}

/// <summary>
/// Browse public skill collections on GitHub (Anthropic's skills, Hermes' skills) and import one with a click.
/// Listing is one GitHub call per collection (cached for an hour); descriptions load as you look; imported
/// skills wait for review and are scanned like every import.
/// </summary>
public sealed class SkillCatalog(IHttpClientFactory http, SkillPorter porter)
{
    public static readonly (string name, string repo, string prefix)[] Collections =
    [
        ("Anthropic", "anthropics/skills", "skills/"),
        ("Hermes", "NousResearch/hermes-agent", "skills/"),
    ];

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset at, List<CatalogSkill> skills)> _lists = new();
    private readonly ConcurrentDictionary<string, string> _descriptions = new();

    private HttpClient Client()
    {
        var c = http.CreateClient(Llm.LlmClientFactory.HttpClientName);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Hearth-skill-catalog");
        return c;
    }

    /// <summary>Every skill in a collection (folders holding a SKILL.md).</summary>
    public async Task<List<CatalogSkill>> ListAsync(string collection, CancellationToken ct = default)
    {
        if (_lists.TryGetValue(collection, out var cached) && DateTimeOffset.Now - cached.at < CacheFor) return cached.skills;
        var (name, repo, prefix) = Collections.First(c => c.name == collection);
        var tree = await Client().GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{repo}/git/trees/HEAD?recursive=1", ct)
                   ?? throw new InvalidOperationException("GitHub didn't answer.");
        var skills = (tree["tree"] as JsonArray ?? [])
            .Select(n => n?["path"]?.ToString() ?? "")
            .Where(p => p.StartsWith(prefix) && p.EndsWith("/SKILL.md"))
            .Select(p => p[..^"/SKILL.md".Length])
            .Select(dir =>
            {
                var parts = dir[prefix.Length..].Split('/');
                return new CatalogSkill(name, repo, dir, parts[^1], parts.Length > 1 ? parts[^2] : "");
            })
            .OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
        _lists[collection] = (DateTimeOffset.Now, skills);
        return skills;
    }

    /// <summary>The skill's one-line description (from its SKILL.md), fetched once.</summary>
    public async Task<string> DescriptionAsync(CatalogSkill s, CancellationToken ct = default)
    {
        if (_descriptions.TryGetValue(s.GitHubUrl, out var d)) return d;
        try
        {
            var text = await Client().GetStringAsync($"https://raw.githubusercontent.com/{s.Repo}/HEAD/{s.Dir}/SKILL.md", ct);
            d = SkillPorter.Parse(text, s.Name).Description;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { d = ""; }
        return _descriptions[s.GitHubUrl] = d;
    }

    public Task<List<Models.Skill>> ImportAsync(CatalogSkill s, CancellationToken ct = default) => porter.ImportUrlAsync(s.GitHubUrl, ct);
}
