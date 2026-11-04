using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Locus.Core.Services;

/// <summary>Per-routine memory for polling triggers (what was already seen), kept out of bots.json.</summary>
public sealed class TriggerState
{
    public DateTimeOffset? LastPolled { get; set; }
    public List<string> SeenIds { get; set; } = [];
    public string? PageHash { get; set; }
    public string? PageText { get; set; }
    public string? LastError { get; set; }
}

public sealed class TriggerStateStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string FileFor(string routineId) => Path.Combine(paths.DataDirectory, "triggers", Path.GetFileName(routineId) + ".json");

    public async Task<TriggerState> GetAsync(string routineId) => await Json.ReadAsync<TriggerState>(FileFor(routineId)) ?? new();

    public async Task SaveAsync(string routineId, TriggerState state)
    {
        await _lock.WaitAsync();
        try { await Json.WriteAtomicAsync(FileFor(routineId), state); }
        finally { _lock.Release(); }
    }
}

public sealed record FeedItem(string Id, string Title, string Link, string Summary, DateTimeOffset? Published);

/// <summary>Minimal RSS 2.0 / Atom parser.</summary>
public static class FeedParser
{
    public static List<FeedItem> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("Empty feed.");
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var items = new List<FeedItem>();

        if (root.Name.LocalName == "feed")
        {
            foreach (var e in root.Elements(atom + "entry"))
            {
                var link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value ?? "";
                items.Add(new FeedItem(
                    e.Element(atom + "id")?.Value ?? link,
                    e.Element(atom + "title")?.Value.Trim() ?? "",
                    link,
                    Clean(e.Element(atom + "summary")?.Value ?? e.Element(atom + "content")?.Value ?? ""),
                    Date(e.Element(atom + "updated")?.Value ?? e.Element(atom + "published")?.Value)));
            }
        }
        else
        {
            foreach (var e in root.Descendants().Where(x => x.Name.LocalName == "item"))
            {
                string? V(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value;
                var link = V("link") ?? "";
                items.Add(new FeedItem(V("guid") ?? link, V("title")?.Trim() ?? "", link.Trim(), Clean(V("description") ?? ""), Date(V("pubDate") ?? V("date"))));
            }
        }
        return items.Where(i => i.Id.Length > 0).ToList();
    }

    private static string Clean(string html)
    {
        if (!html.Contains('<')) return html.Trim();
        var (_, text) = TextExtractor.FromHtml(html);
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }

    private static DateTimeOffset? Date(string? s) => DateTimeOffset.TryParse(s, out var d) ? d : null;
}

public static class TextDiff
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Lines that are new or gone between two versions (cheap set-based diff, good enough for pages).</summary>
    public static (List<string> added, List<string> removed) Lines(string before, string after)
    {
        static List<string> Split(string s) => s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var a = Split(before);
        var b = Split(after);
        var setA = a.ToHashSet();
        var setB = b.ToHashSet();
        return (b.Where(l => !setA.Contains(l)).Distinct().ToList(), a.Where(l => !setB.Contains(l)).Distinct().ToList());
    }
}
