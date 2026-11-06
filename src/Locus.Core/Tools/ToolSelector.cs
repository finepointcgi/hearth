using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Locus.Core.Tools;

/// <summary>
/// Sends each reply only the tools it's likely to need. Every tool's name, description and schema is read by the model
/// before it answers; with email, calendar, files, Office, phone and code connected that's thousands of tokens, slow on
/// local models and crowding small context windows. A core set is always there; the rest come in groups when the
/// conversation is about them (or were used recently), and the model can load any group with load_tools.
/// </summary>
public static partial class ToolSelector
{
    public sealed record Group(string Name, string Summary, Func<string, bool> Contains, Regex Topic);

    /// <summary>Always offered: cheap, general, and what most replies use.</summary>
    public static readonly HashSet<string> Core =
    [
        "calculate", "get_current_datetime", "convert_units", "web_search", "fetch_url",
        "save_memory", "recall_memory", "search_past_chats", "read_past_chat",
        "message_bot", "delegate_task", "use_skill", "follow_up", "rename_chat", "end_call", LoadToolsTool.ToolName,
        "who_is", "note_about", "update_plan",
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<Group> Groups =
    [
        new("email", "read, search, draft and send email (Gmail, Outlook)",
            t => t.StartsWith("gmail_") || t is "outlook_search" or "outlook_read" or "outlook_draft" or "outlook_send",
            R(@"\b(e-?mails?|mail|inbox|gmail|outlook|repl(y|ies)|draft|unread|sender|newsletter|messages? from)\b")),
        new("calendar", "see and add calendar events (Google, Outlook)",
            t => t.StartsWith("calendar_") || t is "outlook_calendar" or "outlook_add_event",
            R(@"\b(calendar|meetings?|schedule|appointments?|events?|busy|free time|availability|invite|agenda|this week|tomorrow)\b")),
        new("contacts", "look up people's phone numbers and email addresses",
            t => t == "find_contact",
            R(@"\b(contacts?|phone number|number for|email address|address for|call|text|email)\b")),
        new("documents", "find, read and write documents: Google Docs and Drive, OneDrive and Word",
            t => t.StartsWith("drive_") || t.StartsWith("docs_") || t.StartsWith("onedrive_"),
            R(@"\b(docs?|documents?|drive|onedrive|word|files?|folders?|report|letter|cover letter|r[ée]sum[ée]|cv|write[- ]?up|memo|pdf)\b")),
        new("spreadsheets", "read and write spreadsheets (Google Sheets, Excel)",
            t => t.StartsWith("sheets_") || t.StartsWith("excel_"),
            R(@"\b(sheets?|spreadsheets?|excel|workbook|csv|rows?|columns?|cells?|tracker|table)\b")),
        new("workspace", "files in Locus's workspace and your document library",
            t => t is "list_files" or "read_file" or "write_file" or "search_documents" or "read_linked_file" or "list_linked_files"
                 or "edit_project_file" or "write_project_file" or "undo_project_change" or "list_project_changes",
            R(@"\b(files?|save|saved|folders?|notes?|documents?|library|pdf|attachments?|project)\b")),
        new("browser", "use a real web browser: sign in, click, fill forms, book and order",
            t => t.StartsWith("browser_"),
            R(@"\b(browser|websites?|site|log ?in|sign ?in|click|forms?|book|order|buy|checkout|account|portal|apply)\b")),
        new("computer", "see the screen and use the mouse and keyboard",
            t => t.StartsWith("desktop_"),
            R(@"\b(screen|desktop|mouse|keyboard|window|app|application|click)\b")),
        new("code", "run Python and shell commands, analyse data, work on servers",
            t => t is "run_python" or "run_command" or "run_remote" or "run_in_project" or "run_tools_script",
            R(@"\b(code|python|script|run|execute|compute|analy[sz]e|plot|chart|graph|data|csv|ssh|server|command|terminal|shell)\b")),
        new("images", "generate pictures",
            t => t == "generate_image",
            R(@"\b(image|picture|photo|draw|drawing|logo|illustration|art|render|wallpaper)\b")),
        new("planning", "to-dos, background tasks, routines and new bots",
            t => t is "todo" or "start_task" or "add_routine" or "propose_new_bot" or "list_follow_ups" or "cancel_follow_up",
            R(@"\b(remind(er)?|to-?dos?|tasks?|every (day|week|morning|night|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|daily|weekly|routine|recurring|later|background|hire|new bot)\b")),
        new("phone", "phone a business or person for the user",
            t => t == "call_phone",
            R(@"\b(call|phone|ring|dial)\b")),
        new("skills", "write and improve saved skills",
            t => t is "save_skill" or "improve_skill",
            R(@"\b(skills?|method|remember how|next time)\b")),
    ];

    /// <summary>The group a tool belongs to (null = core; "integrations" = anything else, e.g. MCP servers).</summary>
    public static string? GroupOf(string tool) =>
        Core.Contains(tool) ? null : Groups.FirstOrDefault(g => g.Contains(tool))?.Name ?? "integrations";

    /// <summary>With this few tools there's nothing to save: offer them all.</summary>
    public const int SmallSet = 16;

    /// <summary>
    /// The tools to offer: core, groups whose topic comes up in <paramref name="recentText"/>, groups used in
    /// <paramref name="recentlyUsed"/>, and integrations (MCP) named in the text. Also returns the groups left out.
    /// </summary>
    public static (List<ITool> offered, List<Group> notLoaded) Select(IReadOnlyList<ITool> all, string recentText, IReadOnlySet<string> recentlyUsed,
        IReadOnlySet<string> alwaysInclude)
    {
        if (all.Count <= SmallSet) return (all.ToList(), []);
        var groups = new HashSet<string>();
        foreach (var g in Groups)
            if (g.Topic.IsMatch(recentText) || recentlyUsed.Any(g.Contains)) groups.Add(g.Name);
        var words = Words().Matches(recentText.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 3).ToHashSet();
        var offered = all.Where(t =>
        {
            if (alwaysInclude.Contains(t.Name) || recentlyUsed.Contains(t.Name)) return true;
            var group = GroupOf(t.Name);
            if (group is null) return true;
            if (group != "integrations") return groups.Contains(group);
            // MCP and other integrations: when the conversation names them (e.g. "github", "notion").
            return t.Name.ToLowerInvariant().Split('_', '-', '.').Any(words.Contains);
        }).ToList();
        var missing = Groups.Where(g => !groups.Contains(g.Name) && all.Any(t => g.Contains(t.Name) && !offered.Contains(t))).ToList();
        if (all.Any(t => GroupOf(t.Name) == "integrations" && !offered.Contains(t)))
            missing.Add(new Group("integrations", "tools from connected services (MCP): " +
                string.Join(", ", all.Where(t => GroupOf(t.Name) == "integrations" && !offered.Contains(t)).Select(t => t.Name).Take(12)),
                t => GroupOf(t) == "integrations", R("$^")));
        return (offered, missing);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Words();
}

/// <summary>Loads a group of tools that wasn't offered for this message.</summary>
public sealed class LoadToolsTool(IReadOnlyList<ToolSelector.Group> notLoaded, Func<string, IReadOnlyList<string>> load) : ITool
{
    public const string ToolName = "load_tools";
    public string Name => ToolName;
    public string DisplayName => "Load tools";
    public string Icon => "🧰";
    public string Description => "More tools are available than are listed. Load a group when you need it: " +
        string.Join("; ", notLoaded.Select(g => $"{g.Name} ({g.Summary})")) + ".";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["group"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(notLoaded.Select(g => (JsonNode)g.Name).ToArray()) },
        },
        ["required"] = new JsonArray("group"),
    };
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var group = args.Str("group")?.Trim().ToLowerInvariant() ?? "";
        var names = load(group);
        return Task.FromResult(names.Count == 0
            ? ToolResult.Error($"No group '{group}'. Groups: {string.Join(", ", notLoaded.Select(g => g.Name))}.")
            : new ToolResult($"Loaded {string.Join(", ", names)}. Use them now."));
    }
}
