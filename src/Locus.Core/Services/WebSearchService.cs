using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// "DeepSearch" via a self-hosted SearXNG instance (keeps search private, no API keys).
/// SearXNG must have the json format enabled: search.formats: [html, json] in settings.yml.
/// </summary>
public sealed class WebSearchService(IHttpClientFactory http)
{
    public async Task<List<SearchResult>> SearchAsync(string baseUrl, string query, int count, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}";
        var client = http.CreateClient(Llm.LlmClientFactory.HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var doc = await client.GetFromJsonAsync<JsonObject>(url, cts.Token);
        return doc?["results"]?.AsArray()
            .Select(r => new SearchResult(
                r?["title"]?.ToString() ?? "",
                r?["url"]?.ToString() ?? "",
                r?["content"]?.ToString() ?? ""))
            .Where(r => r.Url.Length > 0)
            .Take(count)
            .ToList() ?? [];
    }
}
