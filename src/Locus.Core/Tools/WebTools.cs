using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tools;

public sealed class WebSearchTool(WebSearchService search) : ITool
{
    public string Name => "web_search";
    public string DisplayName => "Web search";
    public string Icon => "🔎";
    public string Description =>
        "Search the web for current information. Returns numbered results with title, URL and snippet. " +
        "Cite results in your answer as [n]. Use fetch_url to read a promising page in full.";
    public JsonObject Parameters => Schema.Object(
        ("query", "string", "Search query", true),
        ("count", "integer", "Number of results (1-10, default 5)", false));
    public bool RequiresApproval => false;
    /// <summary>Snippets beyond this are mostly repeated page text; fetch_url reads the page when it matters.</summary>
    public const int MaxSnippetChars = 300;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var query = args.Str("query");
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.Error("query is required");
        var count = (int)Math.Clamp(args.Num("count") ?? 5, 1, 10);
        var results = await search.SearchAsync(ctx.Settings.SearxngUrl!, query, count, ct);
        if (results.Count == 0) return new ToolResult("No results.");

        // Number results globally across the whole reply so [n] citations stay stable.
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var idx = ctx.Reply.Sources.FindIndex(s => s.Url == r.Url);
            if (idx < 0) { ctx.Reply.Sources.Add(r); idx = ctx.Reply.Sources.Count - 1; }
            sb.AppendLine($"[{idx + 1}] {r.Title}\n{r.Url}\n{Schema.Truncate(r.Snippet, MaxSnippetChars)}\n");
        }
        return new ToolResult(sb.ToString());
    }
}

public sealed class FetchUrlTool(IHttpClientFactory http) : ITool
{
    public string Name => "fetch_url";
    public string DisplayName => "Read web page";
    public string Icon => "🌐";
    public string Description => "Download a web page or PDF and return its readable text (the start of it; raise max_chars only if what you need is further down).";
    public JsonObject Parameters => Schema.Object(
        ("url", "string", "Absolute http(s) URL", true),
        ("max_chars", "integer", $"Maximum characters to return (default {DefaultChars})", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (!Uri.TryCreate(args.Str("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ToolResult.Error("A valid http(s) URL is required.");
        var max = (int)Math.Clamp(args.Num("max_chars") ?? DefaultChars, 500, 60000);

        string title, text;
        try { (title, text) = await ReadAsync(uri, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            // Models guess URLs and then keep guessing; point them back at real links.
            return ToolResult.Error(ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone
                ? $"{ex.Message}: that page doesn't exist. Don't guess addresses: open a URL from web_search results, or search again."
                : ex.Message);
        }

        if (!ctx.Reply.Sources.Any(s => s.Url == uri.ToString()))
            ctx.Reply.Sources.Add(new SearchResult(title.Length > 0 ? title : uri.Host, uri.ToString(), ""));

        return new ToolResult($"# {title}\nSource: {uri}\n\n{Schema.Truncate(DropRepeats(text), max)}");
    }

    /// <summary>
    /// Each page read stays in the prompt for the rest of the reply, and on local models a long prompt is slow to
    /// read: 6,000 characters (~1,500 tokens) covers most articles' substance.
    /// </summary>
    public const int DefaultChars = 6000;

    /// <summary>Drops lines repeated on the page (menus, "Share", cookie lines), keeping the first of each.</summary>
    public static string DropRepeats(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n').Where(l => l.Trim() is var t && (t.Length == 0 || t.Length > 120 || seen.Add(t)));
        return string.Join('\n', lines);
    }

    /// <summary>Downloads a page or PDF and returns its title and readable text (throws on HTTP errors).</summary>
    public async Task<(string title, string text)> ReadAsync(Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        var client = http.CreateClient(Llm.LlmClientFactory.HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("(compatible; Locus/1.0)"));
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", null, res.StatusCode);

        var bytes = await ReadLimitedAsync(res, 8 * 1024 * 1024, cts.Token);
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("pdf") || uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return ("", TextExtractor.FromPdf(bytes));
        if (type.Contains("html") || type.Length == 0)
            return TextExtractor.FromHtml(Encoding.UTF8.GetString(bytes));
        return ("", Encoding.UTF8.GetString(bytes));
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage res, int limit, CancellationToken ct)
    {
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit) break;
        }
        return ms.ToArray();
    }
}
