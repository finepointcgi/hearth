using System.Globalization;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services.Microsoft;

namespace Hearth.Core.Tools;

/// <summary>
/// Microsoft 365 through Microsoft Graph: Outlook mail and calendar, OneDrive (Word, Excel, PDF, text) and To Do.
/// Offered once an account is connected (Connections → Microsoft). Reading runs; changing things asks first
/// (Permissions can change that), and sending email always asks unless allowed there.
/// </summary>
public sealed class MicrosoftToolProvider(MicrosoftAuth auth, GraphClient graph) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!await auth.IsConnectedAsync()) return [];
        var send = new[] { ("to", "string", "Recipient(s), comma-separated (optional when replying)", false), ("subject", "string", "Subject (optional when replying)", false),
            ("body", "string", "Plain-text body", true), ("cc", "string", "Cc (optional)", false), ("reply_to_id", "string", "Message id to reply to (optional)", false) };
        return
        [
            new GTool("outlook_search", "Search Outlook", "📧",
                "Search the user's Outlook email (words, names, addresses, e.g. \"invoice\", \"from:anna\"). No query: unread inbox. Returns sender, subject, date, preview and id.",
                Schema.Object(("query", "string", "What to search for (optional)", false), ("max", "integer", "Up to 25 (default 10)", false)), false,
                a => graph.SearchMailAsync(a.Str("query"), (int)(a.Num("max") ?? 10), default)),
            new GTool("outlook_read", "Read Outlook email", "📨", "Read the full text of an Outlook email by its id (from outlook_search).",
                Schema.Object(("id", "string", "Message id", true)), false,
                a => graph.ReadMailAsync(a.Str("id") ?? "", default)),
            new GTool("outlook_draft", "Draft Outlook email", "📝",
                "Save an email (or a reply, with reply_to_id) in Outlook Drafts for the user to review. Safer than sending.",
                Schema.Object(send), true,
                a => graph.ComposeAsync(a.Str("to"), a.Str("subject"), a.Str("body") ?? "", a.Str("cc"), a.Str("reply_to_id"), send: false, default)),
            new GTool("outlook_send", "Send Outlook email", "📤",
                "Send an email (or a reply, with reply_to_id) from the user's Outlook. Prefer outlook_draft unless the user clearly asked to send.",
                Schema.Object(send), true,
                a => graph.ComposeAsync(a.Str("to"), a.Str("subject"), a.Str("body") ?? "", a.Str("cc"), a.Str("reply_to_id"), send: true, default)),
            new GTool("outlook_calendar", "Outlook calendar", "📅",
                "List the user's Outlook calendar events between two dates (default: today through the next 7 days).",
                Schema.Object(("from", "string", "Start date/time, e.g. 2026-11-09", false), ("to", "string", "End date/time", false)), false,
                a =>
                {
                    var from = Date(a.Str("from")) ?? DateTimeOffset.Now.Date;
                    return graph.EventsAsync(from, Date(a.Str("to")) ?? from.AddDays(7), default);
                }),
            new GTool("outlook_add_event", "Add Outlook event", "🗓️",
                "Add an event to the user's Outlook calendar. Attendees get invitations.",
                Schema.Object(("subject", "string", "Title", true), ("start", "string", "Local start, e.g. 2026-11-09 14:00", true),
                    ("end", "string", "Local end (default: an hour after start)", false), ("location", "string", "Where (optional)", false),
                    ("attendees", "string", "Emails to invite, comma-separated (optional)", false), ("notes", "string", "Description (optional)", false)), true,
                a =>
                {
                    var start = Date(a.Str("start")) ?? throw new InvalidOperationException("Give start as a date and time, e.g. 2026-11-09 14:00.");
                    return graph.CreateEventAsync(a.Str("subject") ?? "Event", start, Date(a.Str("end")) ?? start.AddHours(1),
                        a.Str("location"), a.Str("attendees"), a.Str("notes"), default);
                }),
            new GTool("onedrive_search", "Search OneDrive", "🔎",
                "Find files in the user's OneDrive (and SharePoint files shared to it) by name or content. Returns name, folder, date and id.",
                Schema.Object(("query", "string", "Words to search for", true)), false,
                a => graph.SearchFilesAsync(a.Str("query") ?? "", default)),
            new GTool("onedrive_read", "Read OneDrive file", "📄",
                "Read a OneDrive file's text by id (from onedrive_search): Word, PDF, text, Markdown, HTML. For spreadsheets use excel_read.",
                Schema.Object(("id", "string", "File id", true)), false,
                a => graph.ReadFileAsync(a.Str("id") ?? "", default)),
            new GTool("onedrive_write", "Save to OneDrive", "💾",
                "Save a file to the user's OneDrive. A path ending .docx becomes a real Word document made from your Markdown (# headings, - bullets, **bold**). Overwrites a file at the same path.",
                Schema.Object(("path", "string", "Folder and name, e.g. Documents/Cover letter - Shield AI.docx", true), ("content", "string", "The content (Markdown for .docx)", true)), true,
                a => graph.WriteFileAsync(a.Str("path") ?? "", a.Str("content") ?? "", default)),
            new GTool("excel_read", "Read Excel", "📊",
                "Read an Excel workbook in OneDrive by file id: lists its sheets and returns the used range of a sheet, or a range like A1:F50.",
                Schema.Object(("id", "string", "Workbook file id (from onedrive_search)", true), ("sheet", "string", "Sheet name (default: the first)", false),
                    ("range", "string", "Range like A1:F50 (default: everything used)", false)), false,
                a => graph.ReadExcelAsync(a.Str("id") ?? "", a.Str("sheet"), a.Str("range"), default)),
            new GTool("excel_write", "Write Excel", "✏️",
                "Write values into an Excel workbook in OneDrive. values is a JSON array of rows, e.g. [[\"Company\",\"Role\"],[\"Shield AI\",\"Director\"]]; the range must be the same size, e.g. A1:B2.",
                Schema.Object(("id", "string", "Workbook file id", true), ("sheet", "string", "Sheet name", true), ("range", "string", "Top-left to bottom-right, e.g. A1:B2", true),
                    ("values", "string", "JSON array of rows", true)), true,
                a =>
                {
                    var raw = a["values"];
                    var rows = raw as JsonArray ?? (raw is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) as JsonArray : null)
                        ?? throw new InvalidOperationException("values must be a JSON array of rows, e.g. [[\"a\",\"b\"]].");
                    return graph.WriteExcelAsync(a.Str("id") ?? "", a.Str("sheet") ?? "", a.Str("range") ?? "", rows, default);
                }),
            new GTool("todo", "Microsoft To Do", "✅",
                "The user's Microsoft To Do: list open tasks, or add one (with an optional due date) so it shows up on their phone.",
                Schema.Object(("add", "string", "Task to add (leave out to list open tasks)", false), ("due", "string", "Due date, e.g. 2026-11-14 (optional)", false),
                    ("notes", "string", "Notes (optional)", false)), false,
                a => a.Str("add") is { Length: > 0 } title ? graph.AddTaskAsync(title, Date(a.Str("due")), a.Str("notes"), default) : graph.TasksAsync(default)),
        ];
    }

    private static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}
