using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services.Google;

namespace Hearth.Core.Tools;

/// <summary>Gmail and Calendar tools. Reading never asks; sending email always asks unless AutoApproveSend.</summary>
public sealed class GoogleWorkspaceToolProvider(GoogleAuth auth, WorkspaceClient google) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var g = settings.Google;
        var tools = new List<ITool>();
        var askEdit = !g.AutoApproveEdits;

        if (g.GmailEnabled && await auth.HasScopeAsync(GoogleAuth.GmailScope))
        {
            tools.Add(new GTool("gmail_search", "Search email", "📧",
                "Search the user's Gmail with Gmail search syntax, e.g. \"is:unread in:inbox\", \"from:anna newer_than:7d\", \"subject:invoice\". " +
                "Returns sender, subject, date, snippet and the message id.",
                Schema.Object(("query", "string", "Gmail search query (default: is:unread in:inbox)", false), ("max", "integer", "Up to 25 (default 10)", false)), false,
                async a =>
                {
                    var list = await google.SearchMailAsync(a.Str("query") is { Length: > 0 } q ? q : "is:unread in:inbox", (int)(a.Num("max") ?? 10), default);
                    return list.Count == 0 ? "No matching email."
                        : string.Join("\n", list.Select(m => $"- {(m.Unread ? "● " : "")}{m.Date:MMM d HH:mm} | {m.From} | {m.Subject} — {m.Snippet} (id {m.Id})"));
                }));
            tools.Add(new GTool("gmail_read", "Read email", "📨",
                "Read the full text of an email by its id (from gmail_search).",
                Schema.Object(("id", "string", "Message id", true)), false,
                async a =>
                {
                    var m = await google.ReadMailAsync(a.Str("id") ?? "", default);
                    return $"From: {m.Summary.From}\nTo: {m.Summary.To}\nDate: {m.Summary.Date:f}\nSubject: {m.Summary.Subject}\n\n{Schema.Truncate(m.Body, 20000)}";
                }));
            tools.Add(new GTool("gmail_draft", "Draft email", "📝",
                "Save an email as a Gmail draft for the user to review (safer than sending). Use reply_to_id to reply in a thread.",
                Schema.Object(("to", "string", "Recipient(s), comma-separated (optional when replying)", false), ("subject", "string", "Subject (optional when replying)", false),
                    ("body", "string", "Plain-text body", true), ("cc", "string", "Cc (optional)", false), ("reply_to_id", "string", "Message id to reply to (optional)", false)), askEdit,
                a => google.ComposeAsync(a.Str("to") ?? "", a.Str("subject") ?? "", a.Str("body") ?? "", a.Str("cc"), a.Str("reply_to_id"), send: false, default)));
            tools.Add(new GTool("gmail_send", "Send email", "📤",
                "Send an email from the user's Gmail. Prefer gmail_draft unless the user clearly asked to send.",
                Schema.Object(("to", "string", "Recipient(s), comma-separated (optional when replying)", false), ("subject", "string", "Subject (optional when replying)", false),
                    ("body", "string", "Plain-text body", true), ("cc", "string", "Cc (optional)", false), ("reply_to_id", "string", "Message id to reply to (optional)", false)), !g.AutoApproveSend,
                a => google.ComposeAsync(a.Str("to") ?? "", a.Str("subject") ?? "", a.Str("body") ?? "", a.Str("cc"), a.Str("reply_to_id"), send: true, default)));
            tools.Add(new GTool("gmail_organize", "Organize email", "🗂️",
                "Archive, mark read/unread, star or label an email.",
                Schema.Object(("id", "string", "Message id", true),
                    ("action", "string", "archive, read, unread, star, unstar, important, or label:<LabelId>", true)), askEdit,
                async a =>
                {
                    var id = a.Str("id") ?? "";
                    var action = (a.Str("action") ?? "").Trim();
                    var (add, remove) = action.ToLowerInvariant() switch
                    {
                        "archive" => (Array.Empty<string>(), new[] { "INBOX" }),
                        "read" => ([], ["UNREAD"]),
                        "unread" => (["UNREAD"], []),
                        "star" => (["STARRED"], []),
                        "unstar" => ([], ["STARRED"]),
                        "important" => (["IMPORTANT"], []),
                        _ when action.StartsWith("label:", StringComparison.OrdinalIgnoreCase) => ([action[6..]], []),
                        _ => throw new ArgumentException("Unknown action."),
                    };
                    await google.LabelAsync(id, add, remove, default);
                    return $"Done: {action}.";
                }));
        }

        if (g.CalendarEnabled && await auth.HasScopeAsync(GoogleAuth.CalendarScope))
        {
            tools.Add(new GTool("calendar_events", "Check calendar", "📅",
                "List events in the user's primary Google Calendar between two dates (default: the next 7 days).",
                Schema.Object(("from", "string", "Start, ISO date/time (default now)", false), ("to", "string", "End, ISO date/time (default +7 days)", false),
                    ("query", "string", "Text to search for (optional)", false)), false,
                async a =>
                {
                    var from = Date(a.Str("from")) ?? DateTimeOffset.Now;
                    var to = Date(a.Str("to")) ?? from.AddDays(7);
                    var events = await google.EventsAsync(from, to, a.Str("query"), 50, default);
                    return events.Count == 0 ? $"No events between {from:ddd MMM d} and {to:ddd MMM d}."
                        : $"Events {from:ddd MMM d} – {to:ddd MMM d} (times are {TimeZoneInfo.Local.Id}):\n" + string.Join("\n", events.Select(WorkspaceClient.Describe));
                }));
            tools.Add(new GTool("calendar_create", "Add event", "🗓️",
                "Create an event in the user's primary calendar. Invitations are emailed if attendees are given. Check for conflicts with calendar_events first.",
                Schema.Object(("title", "string", "Event title", true), ("start", "string", "Start, ISO date/time (or a date for all-day)", true),
                    ("end", "string", "End, ISO date/time (default: start + 1 hour)", false), ("location", "string", "Location (optional)", false),
                    ("description", "string", "Notes (optional)", false), ("attendees", "string", "Comma-separated emails to invite (optional)", false)), askEdit,
                async a =>
                {
                    var startText = a.Str("start") ?? "";
                    var allDay = startText.Length <= 10;
                    var start = Date(startText) ?? throw new ArgumentException("Couldn't read the start time; use ISO format like 2026-11-05T14:00.");
                    var end = Date(a.Str("end")) ?? (allDay ? start.AddDays(1) : start.AddHours(1));
                    var attendees = (a.Str("attendees") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    var e = await google.CreateEventAsync(a.Str("title") ?? "Event", start, end, allDay, a.Str("location"), a.Str("description"), attendees, default);
                    return $"Created:\n{WorkspaceClient.Describe(e)}\n{e.Link}";
                }));
            tools.Add(new GTool("calendar_delete", "Delete event", "🗑️",
                "Delete an event by id (from calendar_events).",
                Schema.Object(("id", "string", "Event id", true)), askEdit,
                async a => { await google.DeleteEventAsync(a.Str("id") ?? "", default); return "Deleted."; }));
        }
        return tools;
    }

    private static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}

internal sealed class GTool(string name, string displayName, string icon, string description, JsonObject parameters,
    bool requiresApproval, Func<JsonObject, Task<string>> run) : ITool
{
    public string Name => name;
    public string DisplayName => displayName;
    public string Icon => icon;
    public string Description => description;
    public JsonObject Parameters => parameters;
    public bool RequiresApproval => requiresApproval;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        try { return new ToolResult(Schema.Truncate(await run(args), 24000)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolResult.Error(ex.Message); }
    }
}
