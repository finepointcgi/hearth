using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Locus.Core.Services.Google;

public sealed record EmailSummary(string Id, string ThreadId, string From, string To, string Subject, DateTimeOffset? Date, string Snippet, bool Unread, string? MessageIdHeader);
public sealed record EmailMessage(EmailSummary Summary, string Body);
public sealed record CalendarEvent(string Id, string Title, DateTimeOffset? Start, DateTimeOffset? End, bool AllDay, string? Location, string? Description, List<string> Attendees, string? Link);

/// <summary>Gmail v1 and Calendar v3 over REST, using the shared Google sign-in.</summary>
public sealed class WorkspaceClient(GoogleAuth auth, IHttpClientFactory http)
{
    private HttpClient Http => http.CreateClient(Llm.LlmClientFactory.HttpClientName);

    private async Task<JsonObject> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.AccessTokenAsync(ct));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg;
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch (JsonException) { msg = text; }
            if ((int)res.StatusCode == 403 && msg.Contains("scope", StringComparison.OrdinalIgnoreCase))
                msg += " — reconnect Google on the Connections page to grant this access.";
            throw new InvalidOperationException($"Google: {msg}");
        }
        return text.Length == 0 ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }

    // ------------------------------------------------------------------ Gmail

    public async Task<List<EmailSummary>> SearchMailAsync(string query, int max, CancellationToken ct)
    {
        var (gmail, _) = await auth.WorkspaceEndpointsAsync();
        var list = await SendAsync(HttpMethod.Get, $"{gmail}/messages?q={Uri.EscapeDataString(query)}&maxResults={Math.Clamp(max, 1, 50)}", null, ct);
        var ids = list["messages"]?.AsArray().Select(m => m!["id"]!.ToString()).ToList() ?? [];
        var result = new List<EmailSummary>();
        foreach (var id in ids)
        {
            var m = await SendAsync(HttpMethod.Get,
                $"{gmail}/messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date&metadataHeaders=Message-ID", null, ct);
            result.Add(Summary(m));
        }
        return result;
    }

    public async Task<EmailMessage> ReadMailAsync(string id, CancellationToken ct)
    {
        var (gmail, _) = await auth.WorkspaceEndpointsAsync();
        var m = await SendAsync(HttpMethod.Get, $"{gmail}/messages/{Uri.EscapeDataString(id)}?format=full", null, ct);
        return new EmailMessage(Summary(m), BodyText(m["payload"]));
    }

    private static EmailSummary Summary(JsonObject m)
    {
        string H(string name) => m["payload"]?["headers"]?.AsArray()
            .FirstOrDefault(h => h?["name"]?.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) == true)?["value"]?.ToString() ?? "";
        var labels = m["labelIds"]?.AsArray().Select(l => l?.ToString()).ToList() ?? [];
        return new EmailSummary(m["id"]!.ToString(), m["threadId"]?.ToString() ?? "", H("From"), H("To"), H("Subject"),
            DateTimeOffset.TryParse(H("Date").Split(" (")[0], out var d) ? d : null,
            System.Net.WebUtility.HtmlDecode(m["snippet"]?.ToString() ?? ""), labels.Contains("UNREAD"), H("Message-ID") is { Length: > 0 } mid ? mid : null);
    }

    /// <summary>Prefers text/plain; falls back to readable text from text/html.</summary>
    private static string BodyText(JsonNode? part)
    {
        if (part is null) return "";
        var mime = part["mimeType"]?.ToString() ?? "";
        var data = part["body"]?["data"]?.ToString();
        if (mime == "text/plain" && data is not null) return Decode(data);
        if (part["parts"] is JsonArray parts)
        {
            foreach (var p in parts) if (p?["mimeType"]?.ToString() == "text/plain" && BodyText(p) is { Length: > 0 } t) return t;
            foreach (var p in parts) if (BodyText(p) is { Length: > 0 } t) return t;
        }
        if (mime == "text/html" && data is not null) return TextExtractor.FromHtml(Decode(data)).text;
        return "";
    }

    private static string Decode(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static string Raw(string to, string subject, string body, string? cc, string? inReplyTo)
    {
        var sb = new StringBuilder();
        sb.Append("To: ").Append(to).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(cc)) sb.Append("Cc: ").Append(cc).Append("\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))).Append("?=\r\n");
        if (inReplyTo is not null) sb.Append("In-Reply-To: ").Append(inReplyTo).Append("\r\nReferences: ").Append(inReplyTo).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Creates a draft (or sends it). replyToId threads it as a reply to that message.</summary>
    public async Task<string> ComposeAsync(string to, string subject, string body, string? cc, string? replyToId, bool send, CancellationToken ct)
    {
        var (gmail, _) = await auth.WorkspaceEndpointsAsync();
        string? threadId = null, inReplyTo = null;
        if (!string.IsNullOrWhiteSpace(replyToId))
        {
            var original = (await ReadMailAsync(replyToId, ct)).Summary;
            threadId = original.ThreadId;
            inReplyTo = original.MessageIdHeader;
            if (string.IsNullOrWhiteSpace(to)) to = original.From;
            if (string.IsNullOrWhiteSpace(subject)) subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? original.Subject : "Re: " + original.Subject;
        }
        var message = new JsonObject { ["raw"] = Raw(to, subject, body, cc, inReplyTo) };
        if (threadId is not null) message["threadId"] = threadId;
        if (send)
        {
            var sent = await SendAsync(HttpMethod.Post, $"{gmail}/messages/send", message, ct);
            return $"Sent to {to} (id {sent["id"]}).";
        }
        var draft = await SendAsync(HttpMethod.Post, $"{gmail}/drafts", new JsonObject { ["message"] = message }, ct);
        return $"Draft saved for {to} — \"{subject}\" (draft id {draft["id"]}). It's in Gmail → Drafts for you to review and send.";
    }

    public async Task<string> LabelAsync(string id, IEnumerable<string> add, IEnumerable<string> remove, CancellationToken ct)
    {
        var (gmail, _) = await auth.WorkspaceEndpointsAsync();
        await SendAsync(HttpMethod.Post, $"{gmail}/messages/{Uri.EscapeDataString(id)}/modify", new JsonObject
        {
            ["addLabelIds"] = new JsonArray(add.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            ["removeLabelIds"] = new JsonArray(remove.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        }, ct);
        return "Updated.";
    }

    // ------------------------------------------------------------------ Calendar

    public async Task<List<CalendarEvent>> EventsAsync(DateTimeOffset from, DateTimeOffset to, string? query, int max, CancellationToken ct)
    {
        var (_, cal) = await auth.WorkspaceEndpointsAsync();
        var url = $"{cal}/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults={Math.Clamp(max, 1, 100)}" +
                  $"&timeMin={Uri.EscapeDataString(from.ToString("o"))}&timeMax={Uri.EscapeDataString(to.ToString("o"))}" +
                  (string.IsNullOrWhiteSpace(query) ? "" : $"&q={Uri.EscapeDataString(query)}");
        var res = await SendAsync(HttpMethod.Get, url, null, ct);
        return res["items"]?.AsArray().Select(e => ToEvent(e!)).ToList() ?? [];
    }

    public async Task<CalendarEvent> CreateEventAsync(string title, DateTimeOffset start, DateTimeOffset end, bool allDay,
        string? location, string? description, IReadOnlyList<string> attendees, CancellationToken ct)
    {
        var (_, cal) = await auth.WorkspaceEndpointsAsync();
        JsonObject When(DateTimeOffset t) => allDay
            ? new JsonObject { ["date"] = t.ToString("yyyy-MM-dd") }
            : new JsonObject { ["dateTime"] = t.ToString("o") };
        var body = new JsonObject { ["summary"] = title, ["start"] = When(start), ["end"] = When(end) };
        if (!string.IsNullOrWhiteSpace(location)) body["location"] = location;
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (attendees.Count > 0)
            body["attendees"] = new JsonArray(attendees.Select(a => (JsonNode)new JsonObject { ["email"] = a }).ToArray());
        var res = await SendAsync(HttpMethod.Post, $"{cal}/calendars/primary/events{(attendees.Count > 0 ? "?sendUpdates=all" : "")}", body, ct);
        var made = ToEvent(res);
        UndoScope.Offer(new UndoStep("gcal_event", $"Delete the event \"{title}\"", new JsonObject { ["id"] = made.Id }));
        return made;
    }

    public async Task DeleteEventAsync(string id, CancellationToken ct)
    {
        var (_, cal) = await auth.WorkspaceEndpointsAsync();
        await SendAsync(HttpMethod.Delete, $"{cal}/calendars/primary/events/{Uri.EscapeDataString(id)}", null, ct);
    }

    private static CalendarEvent ToEvent(JsonNode e)
    {
        static (DateTimeOffset? t, bool allDay) When(JsonNode? w) =>
            w?["dateTime"]?.ToString() is { } dt && DateTimeOffset.TryParse(dt, out var d) ? (d, false)
            : w?["date"]?.ToString() is { } day && DateTimeOffset.TryParse(day, out var d2) ? (d2, true) : (null, false);
        var (start, allDay) = When(e["start"]);
        return new CalendarEvent(e["id"]?.ToString() ?? "", e["summary"]?.ToString() ?? "(no title)", start, When(e["end"]).t, allDay,
            e["location"]?.ToString(), e["description"]?.ToString(),
            e["attendees"]?.AsArray().Select(a => a?["email"]?.ToString() ?? "").Where(a => a.Length > 0).ToList() ?? [],
            e["htmlLink"]?.ToString());
    }

    public static string Describe(CalendarEvent e) =>
        $"- {(e.AllDay ? e.Start?.ToString("ddd MMM d") + " (all day)" : $"{e.Start:ddd MMM d, h:mm tt}–{e.End:h:mm tt}")}: {e.Title}" +
        (e.Location is not null ? $" @ {e.Location}" : "") + (e.Attendees.Count > 0 ? $" with {string.Join(", ", e.Attendees.Take(6))}" : "") + $" (id {e.Id})";
}
