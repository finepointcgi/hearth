using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Llm;

namespace Locus.Core.Services.Microsoft;

/// <summary>
/// The Microsoft Graph calls the bots use: Outlook mail and calendar, OneDrive files (Word, Excel, PDF, text) and
/// To Do. Results are plain text for the model.
/// </summary>
public sealed class GraphClient(MicrosoftAuth auth, IHttpClientFactory http)
{
    private HttpClient Client => http.CreateClient(LlmClientFactory.HttpClientName);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path.StartsWith("http") ? path : auth.GraphBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.AccessTokenAsync(ct));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (content is not null) req.Content = content;
        configure?.Invoke(req);
        var res = await Client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return res;
        var text = await res.Content.ReadAsStringAsync(ct);
        res.Dispose();
        string message;
        try { message = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch { message = text; }
        throw new InvalidOperationException(res.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden => $"Microsoft refused this ({message}). Your account or organisation may not allow it.",
            System.Net.HttpStatusCode.NotFound => $"Not found ({message}). Check the id.",
            _ => $"Microsoft Graph: {message}",
        });
    }

    private async Task<JsonNode> GetJsonAsync(string path, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct, configure);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
    }

    private static string Q(string s) => Uri.EscapeDataString(s);
    private static DateTimeOffset Utc(JsonNode? dt) =>
        DateTimeOffset.TryParse(dt?["dateTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : default;
    private static JsonArray People(string? list) => new((list ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } }).ToArray());

    // ------------------------------------------------------------------ mail

    public async Task<string> SearchMailAsync(string? query, int max, CancellationToken ct)
    {
        max = Math.Clamp(max, 1, 25);
        const string select = "$select=id,subject,from,receivedDateTime,bodyPreview,isRead";
        var path = string.IsNullOrWhiteSpace(query)
            ? $"/me/mailFolders/inbox/messages?$filter=isRead eq false&$orderby=receivedDateTime desc&$top={max}&{select}"
            : $"/me/messages?$search={Q("\"" + query.Replace("\"", "") + "\"")}&$top={max}&{select}";
        var items = (await GetJsonAsync(path, ct))["value"]?.AsArray() ?? [];
        return items.Count == 0 ? "No matching email." : string.Join("\n", items.Select(m =>
            $"- {(m!["isRead"]?.GetValue<bool>() == false ? "● " : "")}{DateTimeOffset.Parse(m["receivedDateTime"]!.ToString()).ToLocalTime():MMM d HH:mm} | " +
            $"{m["from"]?["emailAddress"]?["name"]} <{m["from"]?["emailAddress"]?["address"]}> | {m["subject"]} — {m["bodyPreview"]?.ToString().ReplaceLineEndings(" ")} (id {m["id"]})"));
    }

    public async Task<string> ReadMailAsync(string id, CancellationToken ct)
    {
        var m = await GetJsonAsync($"/me/messages/{Q(id)}?$select=subject,from,toRecipients,ccRecipients,receivedDateTime,body", ct,
            r => r.Headers.Add("Prefer", "outlook.body-content-type=\"text\""));
        string Addr(JsonNode? list) => string.Join(", ", (list?.AsArray() ?? []).Select(x => x?["emailAddress"]?["address"]?.ToString()));
        return $"From: {m["from"]?["emailAddress"]?["name"]} <{m["from"]?["emailAddress"]?["address"]}>\nTo: {Addr(m["toRecipients"])}\n" +
               (m["ccRecipients"]?.AsArray().Count > 0 ? $"Cc: {Addr(m["ccRecipients"])}\n" : "") +
               $"Date: {DateTimeOffset.Parse(m["receivedDateTime"]!.ToString()).ToLocalTime():f}\nSubject: {m["subject"]}\n\n{m["body"]?["content"]}";
    }

    /// <summary>Sends, or saves a draft of, a new email or a reply.</summary>
    public async Task<string> ComposeAsync(string? to, string? subject, string body, string? cc, string? replyToId, bool send, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(replyToId))
        {
            if (send)
            {
                (await SendAsync(HttpMethod.Post, $"/me/messages/{Q(replyToId)}/reply", new JsonObject { ["comment"] = body }, ct)).Dispose();
                return "Reply sent.";
            }
            using var draft = await SendAsync(HttpMethod.Post, $"/me/messages/{Q(replyToId)}/createReply", new JsonObject { ["comment"] = body }, ct);
            return "Reply saved in Drafts for the user to review.";
        }
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException("Give a recipient (to).");
        var message = new JsonObject
        {
            ["subject"] = subject ?? "",
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = body },
            ["toRecipients"] = People(to),
        };
        if (!string.IsNullOrWhiteSpace(cc)) message["ccRecipients"] = People(cc);
        if (send)
        {
            (await SendAsync(HttpMethod.Post, "/me/sendMail", new JsonObject { ["message"] = message, ["saveToSentItems"] = true }, ct)).Dispose();
            return $"Sent to {to}.";
        }
        (await SendAsync(HttpMethod.Post, "/me/messages", message, ct)).Dispose();
        return "Saved in Outlook Drafts for the user to review.";
    }

    // ------------------------------------------------------------------ calendar

    public async Task<string> EventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var path = $"/me/calendarView?startDateTime={Q(from.UtcDateTime.ToString("o"))}&endDateTime={Q(to.UtcDateTime.ToString("o"))}" +
                   "&$orderby=start/dateTime&$top=50&$select=id,subject,start,end,location,organizer,isAllDay,onlineMeeting";
        var items = (await GetJsonAsync(path, ct))["value"]?.AsArray() ?? [];
        return items.Count == 0 ? $"No events between {from:MMM d} and {to:MMM d}." : string.Join("\n", items.Select(e =>
        {
            var start = Utc(e!["start"]);
            var when = e["isAllDay"]?.GetValue<bool>() == true ? $"{start:ddd MMM d} (all day)" : $"{start:ddd MMM d h:mm tt}–{Utc(e["end"]):h:mm tt}";
            var where = e["location"]?["displayName"]?.ToString() is { Length: > 0 } l ? $" @ {l}" : "";
            return $"- {when} | {e["subject"]}{where} (organizer {e["organizer"]?["emailAddress"]?["name"]}; id {e["id"]})";
        }));
    }

    public async Task<string> CreateEventAsync(string subject, DateTimeOffset start, DateTimeOffset end, string? location, string? attendees,
        string? notes, CancellationToken ct)
    {
        var ev = new JsonObject
        {
            ["subject"] = subject,
            ["start"] = new JsonObject { ["dateTime"] = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
        };
        if (!string.IsNullOrWhiteSpace(location)) ev["location"] = new JsonObject { ["displayName"] = location };
        if (!string.IsNullOrWhiteSpace(notes)) ev["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = notes };
        if (!string.IsNullOrWhiteSpace(attendees))
            ev["attendees"] = new JsonArray(People(attendees).Select(p => (JsonNode)new JsonObject { ["emailAddress"] = p!["emailAddress"]!.DeepClone(), ["type"] = "required" }).ToArray());
        using var res = await SendAsync(HttpMethod.Post, "/me/events", ev, ct);
        var made = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return $"Added \"{subject}\" on {start:ddd MMM d h:mm tt}{(string.IsNullOrWhiteSpace(attendees) ? "" : $"; invitations sent to {attendees}")} (id {made?["id"]}).";
    }

    // ------------------------------------------------------------------ OneDrive files

    public async Task<string> SearchFilesAsync(string query, CancellationToken ct)
    {
        var path = $"/me/drive/root/search(q='{Q(query.Replace("'", "''"))}')?$top=20&$select=id,name,size,lastModifiedDateTime,webUrl,file,folder,parentReference";
        var items = (await GetJsonAsync(path, ct))["value"]?.AsArray() ?? [];
        return items.Count == 0 ? "No matching files." : string.Join("\n", items.Select(f =>
            $"- {(f!["folder"] is not null ? "📁 " : "")}{f["name"]} | {f["parentReference"]?["path"]?.ToString().Replace("/drive/root:", "")} | " +
            $"modified {DateTimeOffset.Parse(f["lastModifiedDateTime"]!.ToString()).ToLocalTime():MMM d yyyy} (id {f["id"]})"));
    }

    /// <summary>The text of a file: Word, PDF, HTML or text. Spreadsheets point to excel_read.</summary>
    public async Task<string> ReadFileAsync(string id, CancellationToken ct)
    {
        var meta = await GetJsonAsync($"/me/drive/items/{Q(id)}?$select=name,size,webUrl,file", ct);
        var name = meta["name"]?.ToString() ?? "file";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm" or ".xls") return $"{name} is a spreadsheet: read it with excel_read (id {id}).";
        if ((meta["size"]?.GetValue<long>() ?? 0) > 20_000_000) return $"{name} is too large to read here ({meta["webUrl"]}).";
        using var res = await SendAsync(HttpMethod.Get, $"/me/drive/items/{Q(id)}/content", null, ct);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        var text = ext == ".docx" ? WordDocs.ReadText(bytes)
            : TextExtractor.IsSupported(name) ? TextExtractor.Extract(name, bytes)
            : throw new InvalidOperationException($"Can't read {ext} files as text; open it at {meta["webUrl"]}.");
        return $"# {name}\nLink: {meta["webUrl"]}\n\n{text}";
    }

    /// <summary>Writes a file to OneDrive (a .docx path gets a real Word document made from the Markdown).</summary>
    public async Task<string> WriteFileAsync(string path, string content, CancellationToken ct)
    {
        path = path.Trim().TrimStart('/');
        if (path.Length == 0 || path.Contains("..")) throw new InvalidOperationException("Give a path like Documents/Cover letter.docx.");
        var word = path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
        var bytes = word ? WordDocs.FromMarkdown(content, Path.GetFileNameWithoutExtension(path)) : Encoding.UTF8.GetBytes(content);
        if (bytes.Length > 4_000_000) throw new InvalidOperationException("That's too large to upload in one go (4 MB limit).");
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue(word ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document" : "text/plain");
        using var res = await SendAsync(HttpMethod.Put, $"/me/drive/root:/{encoded}:/content", null, ct, content: body);
        var made = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return $"Saved {(word ? "Word document " : "")}{path} to OneDrive: {made?["webUrl"]} (id {made?["id"]})";
    }

    // ------------------------------------------------------------------ Excel

    public async Task<string> ReadExcelAsync(string id, string? sheet, string? range, CancellationToken ct)
    {
        var sheets = ((await GetJsonAsync($"/me/drive/items/{Q(id)}/workbook/worksheets?$select=name", ct))["value"]?.AsArray() ?? [])
            .Select(s => s!["name"]!.ToString()).ToList();
        if (sheets.Count == 0) return "This workbook has no worksheets.";
        var name = sheet is { Length: > 0 } ? sheets.FirstOrDefault(s => s.Equals(sheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No sheet '{sheet}'. Sheets: {string.Join(", ", sheets)}.") : sheets[0];
        var where = string.IsNullOrWhiteSpace(range) ? "usedRange(valuesOnly=true)" : $"range(address='{Q(range.Replace("'", ""))}')";
        var r = await GetJsonAsync($"/me/drive/items/{Q(id)}/workbook/worksheets('{Q(name.Replace("'", "''"))}')/{where}?$select=address,values", ct);
        var rows = r["values"]?.AsArray() ?? [];
        var sb = new StringBuilder($"Sheets: {string.Join(", ", sheets)}\nRange {r["address"]} ({rows.Count} rows):\n");
        foreach (var row in rows.Take(500)) sb.AppendLine(string.Join(" | ", (row?.AsArray() ?? []).Select(c => c?.ToString() ?? "")));
        if (rows.Count > 500) sb.AppendLine($"… {rows.Count - 500} more rows; read a smaller range.");
        return sb.ToString();
    }

    public async Task<string> WriteExcelAsync(string id, string sheet, string range, JsonArray values, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Patch,
            $"/me/drive/items/{Q(id)}/workbook/worksheets('{Q(sheet.Replace("'", "''"))}')/range(address='{Q(range.Replace("'", ""))}')",
            new JsonObject { ["values"] = values.DeepClone() }, ct);
        var r = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return $"Wrote {values.Count} row(s) to {sheet}!{r?["address"]?.ToString().Split('!').Last() ?? range}.";
    }

    // ------------------------------------------------------------------ contacts

    public async Task<List<Google.Contact>> FindContactsAsync(string query, CancellationToken ct)
    {
        var q = query.Replace("'", "''");
        var filter = $"startswith(displayName,'{q}') or startswith(givenName,'{q}') or startswith(surname,'{q}') or startswith(companyName,'{q}')";
        var json = await GetJsonAsync($"/me/contacts?$filter={Q(filter)}&$top=10&$select=displayName,emailAddresses,mobilePhone,businessPhones,homePhones,companyName", ct);
        return (json["value"]?.AsArray() ?? []).OfType<JsonNode>().Select(c => new Google.Contact(
            c["displayName"]?.ToString() ?? "",
            (c["emailAddresses"]?.AsArray() ?? []).Select(e => e?["address"]?.ToString()).OfType<string>().ToList(),
            new[] { c["mobilePhone"]?.ToString() }.Concat((c["businessPhones"]?.AsArray() ?? []).Select(p => p?.ToString()))
                .Concat((c["homePhones"]?.AsArray() ?? []).Select(p => p?.ToString())).OfType<string>().Where(p => p.Length > 0).ToList(),
            c["companyName"]?.ToString(), "Outlook")).ToList();
    }

    // ------------------------------------------------------------------ To Do

    private async Task<string> DefaultListAsync(CancellationToken ct)
    {
        var lists = (await GetJsonAsync("/me/todo/lists", ct))["value"]?.AsArray() ?? [];
        return (lists.FirstOrDefault(l => l?["wellknownListName"]?.ToString() == "defaultList") ?? lists.FirstOrDefault())?["id"]?.ToString()
               ?? throw new InvalidOperationException("No To Do list found.");
    }

    public async Task<string> TasksAsync(CancellationToken ct)
    {
        var list = await DefaultListAsync(ct);
        var items = (await GetJsonAsync($"/me/todo/lists/{Q(list)}/tasks?$filter=status ne 'completed'&$top=50", ct))["value"]?.AsArray() ?? [];
        return items.Count == 0 ? "No open tasks." : string.Join("\n", items.Select(t =>
            $"- {t!["title"]}{(t["dueDateTime"] is { } d ? $" (due {Utc(d):ddd MMM d})" : "")}"));
    }

    public async Task<string> AddTaskAsync(string title, DateTimeOffset? due, string? notes, CancellationToken ct)
    {
        var list = await DefaultListAsync(ct);
        var task = new JsonObject { ["title"] = title };
        if (due is { } d) task["dueDateTime"] = new JsonObject { ["dateTime"] = d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" };
        if (!string.IsNullOrWhiteSpace(notes)) task["body"] = new JsonObject { ["content"] = notes, ["contentType"] = "text" };
        (await SendAsync(HttpMethod.Post, $"/me/todo/lists/{Q(list)}/tasks", task, ct)).Dispose();
        return $"Added to Microsoft To Do: {title}{(due is { } x ? $" (due {x:ddd MMM d})" : "")}.";
    }
}
