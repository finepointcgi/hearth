using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Services.Microsoft;

namespace Locus.Core.Services.Google;

/// <summary>Google Drive and Docs (find, read, create) and Google Contacts, as plain text for the model.</summary>
public sealed partial class DriveClient(GoogleAuth auth, IHttpClientFactory http)
{
    private HttpClient Http => http.CreateClient(Llm.LlmClientFactory.HttpClientName);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.AccessTokenAsync(ct));
        var res = await Http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return res;
        var text = await res.Content.ReadAsStringAsync(ct);
        res.Dispose();
        string msg;
        try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch { msg = text; }
        if (res.StatusCode == HttpStatusCode.Forbidden && msg.Contains("scope", StringComparison.OrdinalIgnoreCase))
            msg += " — reconnect Google on the Connections page to grant this access.";
        throw new InvalidOperationException($"Google: {msg}");
    }

    private async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
    }

    private const string GoogleDoc = "application/vnd.google-apps.document", GoogleSheet = "application/vnd.google-apps.spreadsheet",
        GoogleSlides = "application/vnd.google-apps.presentation", Folder = "application/vnd.google-apps.folder";

    // ------------------------------------------------------------------ Drive

    public async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        var (_, drive, _, _) = await auth.EndpointsAsync();
        var q = query.Replace("\\", "").Replace("'", "\\'");
        var filter = $"trashed = false and (name contains '{q}' or fullText contains '{q}')";
        var json = await GetJsonAsync($"{drive}/files?q={Uri.EscapeDataString(filter)}&pageSize=20&orderBy=modifiedTime desc" +
            "&fields=files(id,name,mimeType,modifiedTime,webViewLink)&supportsAllDrives=true&includeItemsFromAllDrives=true", ct);
        var files = json["files"]?.AsArray() ?? [];
        return files.Count == 0 ? "No matching files in Google Drive." : string.Join("\n", files.Select(f =>
            $"- {Kind(f!["mimeType"]?.ToString())}{f["name"]} | modified {DateTimeOffset.Parse(f["modifiedTime"]!.ToString()).ToLocalTime():MMM d yyyy} (id {f["id"]})"));
    }

    private static string Kind(string? mime) => mime switch
    {
        GoogleDoc => "📄 ", GoogleSheet => "📊 ", GoogleSlides => "📽️ ", Folder => "📁 ", _ => "",
    };

    /// <summary>The text of a Drive file: Google Docs and Slides exported as text; Word, PDF and text files downloaded.</summary>
    public async Task<string> ReadAsync(string id, CancellationToken ct)
    {
        var (_, drive, _, _) = await auth.EndpointsAsync();
        var meta = await GetJsonAsync($"{drive}/files/{Uri.EscapeDataString(id)}?fields=name,mimeType,size,webViewLink&supportsAllDrives=true", ct);
        var name = meta["name"]?.ToString() ?? "file";
        var mime = meta["mimeType"]?.ToString() ?? "";
        string text;
        if (mime == GoogleSheet) return $"{name} is a Google Sheet: read it with sheets_read (id {id}).";
        if (mime == Folder) return $"{name} is a folder.";
        if (mime is GoogleDoc or GoogleSlides)
        {
            using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"{drive}/files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString("text/plain")}"), ct);
            text = await res.Content.ReadAsStringAsync(ct);
        }
        else
        {
            if (long.TryParse(meta["size"]?.ToString(), out var size) && size > 20_000_000) return $"{name} is too large to read here ({meta["webViewLink"]}).";
            using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{drive}/files/{Uri.EscapeDataString(id)}?alt=media&supportsAllDrives=true"), ct);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            text = name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? WordDocs.ReadText(bytes)
                : TextExtractor.IsSupported(name) ? TextExtractor.Extract(name, bytes)
                : throw new InvalidOperationException($"Can't read this kind of file as text; open it at {meta["webViewLink"]}.");
        }
        return $"# {name}\nLink: {meta["webViewLink"]}\n\n{text.Trim()}";
    }

    /// <summary>Creates a Google Doc from Markdown (Drive converts the HTML, keeping headings, lists and bold).</summary>
    public async Task<string> CreateDocAsync(string title, string markdown, CancellationToken ct)
    {
        var (upload, _) = await auth.MoreEndpointsAsync();
        var boundary = "locus" + Guid.NewGuid().ToString("N");
        var metadata = new JsonObject { ["name"] = title, ["mimeType"] = GoogleDoc }.ToJsonString();
        var body = $"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{metadata}\r\n" +
                   $"--{boundary}\r\nContent-Type: text/html; charset=UTF-8\r\n\r\n{ToHtml(markdown)}\r\n--{boundary}--";
        var req = new HttpRequestMessage(HttpMethod.Post, $"{upload}/files?uploadType=multipart&fields=id,webViewLink")
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");
        using var res = await SendAsync(req, ct);
        var made = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return $"Created Google Doc \"{title}\": {made?["webViewLink"]} (id {made?["id"]})";
    }

    /// <summary>Markdown → simple HTML: headings, bullet and numbered lists, bold, italics, links, paragraphs.</summary>
    public static string ToHtml(string markdown)
    {
        var sb = new StringBuilder("<html><body>");
        string? list = null;
        void Close() { if (list is not null) { sb.Append($"</{list}>"); list = null; } }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Close(); continue; }
            if (HeadingRx().Match(line) is { Success: true } h)
            {
                Close();
                var level = Math.Min(3, h.Groups[1].Value.Length);
                sb.Append($"<h{level}>{Inline(h.Groups[2].Value)}</h{level}>");
                continue;
            }
            var bullet = BulletRx().Match(line);
            var number = NumberRx().Match(line);
            if (bullet.Success || number.Success)
            {
                var kind = bullet.Success ? "ul" : "ol";
                if (list != kind) { Close(); sb.Append($"<{kind}>"); list = kind; }
                sb.Append($"<li>{Inline(bullet.Success ? bullet.Groups[1].Value : number.Groups[2].Value)}</li>");
                continue;
            }
            Close();
            sb.Append($"<p>{Inline(line)}</p>");
        }
        Close();
        return sb.Append("</body></html>").ToString();
    }

    private static string Inline(string text)
    {
        var t = WebUtility.HtmlEncode(text);
        t = LinkRx().Replace(t, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        t = BoldRx().Replace(t, "<b>$1</b>");
        return ItalicRx().Replace(t, "<i>$1</i>");
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^\s*[-*•]\s+(.*)$")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")] private static partial Regex NumberRx();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<![*\w])\*([^*]+)\*(?!\*)")] private static partial Regex ItalicRx();

    // ------------------------------------------------------------------ Contacts

    /// <summary>Contacts whose name, email or number matches.</summary>
    public async Task<List<Contact>> FindContactsAsync(string query, CancellationToken ct)
    {
        var (_, people) = await auth.MoreEndpointsAsync();
        const string mask = "names,emailAddresses,phoneNumbers,organizations";
        // Google's contact search needs a warm-up request with an empty query before it returns results.
        try { (await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{people}/people:searchContacts?query=&readMask={mask}"), ct)).Dispose(); }
        catch (InvalidOperationException) { }
        var json = await GetJsonAsync($"{people}/people:searchContacts?query={Uri.EscapeDataString(query)}&readMask={mask}&pageSize=10", ct);
        return (json["results"]?.AsArray() ?? []).Select(r => r?["person"]).OfType<JsonNode>().Select(p => new Contact(
            p["names"]?[0]?["displayName"]?.ToString() ?? "",
            (p["emailAddresses"]?.AsArray() ?? []).Select(e => e?["value"]?.ToString()).OfType<string>().ToList(),
            (p["phoneNumbers"]?.AsArray() ?? []).Select(e => e?["value"]?.ToString()).OfType<string>().ToList(),
            p["organizations"]?[0]?["name"]?.ToString(), "Google")).ToList();
    }
}

/// <summary>Someone from the user's address book.</summary>
public sealed record Contact(string Name, List<string> Emails, List<string> Phones, string? Company, string Source)
{
    public override string ToString() =>
        $"- {Name}{(Company is { Length: > 0 } c ? $" ({c})" : "")}: " +
        string.Join("; ", Emails.Select(e => "email " + e).Concat(Phones.Select(p => "phone " + p))) + $" [{Source}]";
}
