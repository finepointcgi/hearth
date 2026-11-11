using Hearth.Core.Models;

namespace Hearth.Core.Tools;

/// <summary>A field the user fills in when adding a catalog server.</summary>
/// <param name="Target">"env:NAME", "header:Name" (value may use {value}), "arg" (appended), or "url" (replaces {value} in the URL).</param>
public sealed record McpField(string Label, string Target, string Placeholder, bool Secret = false, string? Format = null, string? Help = null);

/// <summary>A popular MCP server, ready to add with a click (versions pinned).</summary>
public sealed record McpCatalogItem(string Id, string Name, string Icon, string Description, McpTransport Transport,
    string Command, string Arguments, string Url, IReadOnlyList<McpField> Fields, string? Needs, string Docs)
{
    /// <summary>Builds the server config from what the user typed.</summary>
    public McpServerConfig Build(IReadOnlyDictionary<string, string> values)
    {
        var c = new McpServerConfig { Name = Id, Transport = Transport, Command = Command, Arguments = Arguments, Url = Url };
        foreach (var f in Fields)
        {
            var v = values.GetValueOrDefault(f.Label, "").Trim();
            if (v.Length == 0) continue;
            var formatted = f.Format is null ? v : f.Format.Replace("{value}", v);
            if (f.Target.StartsWith("env:")) c.Environment[f.Target[4..]] = formatted;
            else if (f.Target.StartsWith("header:")) c.Headers[f.Target[7..]] = formatted;
            else if (f.Target == "arg") c.Arguments = (c.Arguments + " " + Quote(formatted)).Trim();
            else if (f.Target == "url") c.Url = c.Url.Replace("{value}", formatted.TrimEnd('/'));
        }
        return c;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public bool IsComplete(IReadOnlyDictionary<string, string> values) =>
        Fields.All(f => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(f.Label)));
}

public static class McpCatalog
{
    public static readonly IReadOnlyList<McpCatalogItem> All =
    [
        new("github", "GitHub", "code", "Issues, pull requests, code search and repositories (GitHub's hosted server).",
            McpTransport.Http, "", "", "https://api.githubcopilot.com/mcp/",
            [new("Personal access token", "header:Authorization", "github_pat_…", Secret: true, Format: "Bearer {value}",
                Help: "github.com → Settings → Developer settings → Fine-grained tokens")],
            null, "https://github.com/github/github-mcp-server"),
        new("notion", "Notion", "book-open", "Search, read and update your Notion pages and databases.",
            McpTransport.Stdio, "npx", "-y @notionhq/notion-mcp-server@2.6.1", "",
            [new("Integration token", "env:NOTION_TOKEN", "ntn_…", Secret: true,
                Help: "notion.so/profile/integrations → New integration, then share pages with it")],
            "node", "https://github.com/makenotion/notion-mcp-server"),
        new("home-assistant", "Home Assistant", "monitor", "Control and check your smart home (lights, sensors, scenes).",
            McpTransport.Http, "", "", "{value}/api/mcp",
            [new("Home Assistant address", "url", "http://homeassistant.local:8123"),
             new("Long-lived access token", "header:Authorization", "eyJ…", Secret: true, Format: "Bearer {value}",
                Help: "Add the \"Model Context Protocol Server\" integration in Home Assistant; create the token under your profile → Security")],
            null, "https://www.home-assistant.io/integrations/mcp_server/"),
        new("workbench", "Workbench", "chart-bar", "Projects, funding, budgets, earned value, timesheets, approvals and sheets from your Workbench (read only).",
            McpTransport.Http, "", "", "{value}/api/v1/mcp",
            [new("Workbench address", "url", "https://workbench.example.com"),
             new("API key", "header:Authorization", "wbk_…", Secret: true, Format: "Bearer {value}",
                Help: "Workbench → Admin › Integrations › API keys: give it the AI assistants (MCP) scope plus the areas it may read")],
            null, "https://finepointcgi.github.io/workbench/integrations/mcp/"),
        new("files", "Files on this computer", "folder", "Read and edit files in one folder you choose.",
            McpTransport.Stdio, "npx", "-y @modelcontextprotocol/server-filesystem@2.2.0", "",
            [new("Folder", "arg", "/Users/you/Documents/Notes")],
            "node", "https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem"),
        new("brave-search", "Brave Search", "search", "Web, news and image search through Brave's API (no SearXNG needed).",
            McpTransport.Stdio, "npx", "-y @brave/brave-search-mcp-server@2.1.4", "",
            [new("API key", "env:BRAVE_API_KEY", "BSA…", Secret: true, Help: "brave.com/search/api (free tier available)")],
            "node", "https://github.com/brave/brave-search-mcp-server"),
        new("docs", "Library docs (Context7)", "file-text", "Up-to-date documentation and code examples for programming libraries.",
            McpTransport.Stdio, "npx", "-y @upstash/context7-mcp@4.5.0", "", [],
            "node", "https://github.com/upstash/context7"),
        new("git", "Git repository", "code", "History, diffs, branches and commits of a local Git repository.",
            McpTransport.Stdio, "uvx", "mcp-server-git --repository", "",
            [new("Repository folder", "arg", "/Users/you/code/my-project")],
            "uv", "https://github.com/modelcontextprotocol/servers/tree/main/src/git"),
        new("time", "Time zones", "clock", "Current time anywhere and conversions between time zones.",
            McpTransport.Stdio, "uvx", "mcp-server-time", "", [],
            "uv", "https://github.com/modelcontextprotocol/servers/tree/main/src/time"),
    ];
}
