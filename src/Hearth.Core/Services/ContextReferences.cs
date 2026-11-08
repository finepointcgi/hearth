using System.Text.RegularExpressions;
using Hearth.Core.Models;
using Hearth.Core.Tools;

namespace Hearth.Core.Services;

/// <summary>Something attached to a message by an @reference.</summary>
public sealed record ReferencedContent(string Name, string Text, string? UntrustedSource);

/// <summary>
/// "@https://…" attaches that page's text and "@/path/to/file" (or "@~/…") attaches a file from a linked folder,
/// the way other agents use @ for context. Bot names after @ keep working: only links and paths are references.
/// Web pages are labelled as information (not instructions) and mark the run as having read the web.
/// </summary>
public sealed partial class ContextReferences(IHttpClientFactory http, LinkedFolders folders)
{
    public const int MaxChars = 16000;

    [GeneratedRegex(@"(?<=^|\s)@(https?://[^\s<>""]+)")] private static partial Regex UrlRef();
    // "@/…", "@~/…" and, on Windows, "@C:\…" or "@C:/…".
    [GeneratedRegex(@"(?<=^|\s)@((?:~|/|[A-Za-z]:[\\/])[^\s]+)")] private static partial Regex PathRef();

    public static bool HasReferences(string text) => UrlRef().IsMatch(text) || PathRef().IsMatch(text);

    /// <summary>Reads everything the text references. Problems are returned as messages instead of failing.</summary>
    public async Task<(List<ReferencedContent> items, List<string> problems)> ResolveAsync(string text, CancellationToken ct = default)
    {
        var items = new List<ReferencedContent>();
        var problems = new List<string>();
        foreach (var url in UrlRef().Matches(text).Select(m => m.Groups[1].Value.TrimEnd('.', ',', ')', ';')).Distinct().Take(5))
        {
            try
            {
                var (title, body) = await new FetchUrlTool(http).ReadAsync(new Uri(url), ct);
                body = Clip(body);
                items.Add(new ReferencedContent(title.Length > 0 ? $"{title} ({url})" : url,
                    ContentTrust.Label("the web", $"Page: {url}\n\n{body}"), "the web"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"Couldn't read {url}: {ex.Message}"); }
        }
        foreach (var raw in PathRef().Matches(text).Select(m => m.Groups[1].Value.TrimEnd('.', ',', ')', ';')).Distinct().Take(10))
        {
            var path = LinkedFolders.Normalize(raw);
            if (await folders.FolderForAsync(path) is null) { problems.Add($"{raw} isn't in a linked folder (link it on the Documents page)."); continue; }
            if (!File.Exists(path)) { problems.Add($"No file at {raw}."); continue; }
            if (!TextExtractor.IsSupported(path) || new FileInfo(path).Length > LinkedFolders.MaxFileBytes) { problems.Add($"{raw} can't be read as text."); continue; }
            try
            {
                var body = TextExtractor.Extract(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct));
                items.Add(new ReferencedContent(path, $"File: {path}\n\n{Clip(body)}", null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"Couldn't read {raw}: {ex.Message}"); }
        }
        return (items, problems);
    }

    /// <summary>Files in linked folders whose name contains <paramref name="query"/> (for @ suggestions).</summary>
    public async Task<List<string>> SuggestFilesAsync(string query, int max = 6)
    {
        if (query.Length < 2) return [];
        var found = new List<string>();
        foreach (var f in (await folders.ListAsync()).Where(f => f.Enabled))
            foreach (var file in LinkedFolders.Scan(f.Path))
            {
                if (!file.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(file.FullName);
                if (found.Count >= max) return found;
            }
        return found;
    }

    private static string Clip(string s) => s.Length <= MaxChars ? s : s[..MaxChars] + "\n… (cut)";
}
