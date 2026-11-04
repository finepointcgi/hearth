using Locus.Core.Tools;

namespace Locus.Core.Models;

public enum PermissionLevel { Default, Allow, Ask, Block }

/// <summary>Implemented by tools whose area isn't obvious from their name (MCP tools).</summary>
public interface IPermissionInfo
{
    string Area { get; }
    string AreaTitle { get; }
    bool IsWrite { get; }
}

/// <summary>
/// What bots may do, per area, for reading and for changing things — one place for every service ("Sentinel"-style).
/// Default keeps each tool's own behaviour (reads run, changes ask).
/// </summary>
public static class PermissionCatalog
{
    public sealed record Area(string Key, string Icon, string Title, string? Read, string? Write);

    public static readonly IReadOnlyList<Area> Areas =
    [
        new("web", "🔎", "Web", "Search and read web pages", null),
        new("browser", "🧭", "Browser", "Open, look at and screenshot pages", "Click, type, sign in"),
        new("desktop", "🖱️", "Your computer", "See the screen", "Click, type, open apps"),
        new("files", "🗂️", "Workspace files", "List and read files", "Create and change files"),
        new("code", "🐍", "Code", null, "Run Python and shell commands"),
        new("sheets", "📊", "Google Sheets", "Find and read spreadsheets", "Write, add rows, create, clear"),
        new("gmail", "📧", "Gmail", "Search and read email", "Save drafts, archive, label"),
        new("gmail.send", "📤", "Sending email", null, "Send email as you"),
        new("calendar", "📅", "Calendar", "Check your schedule", "Add and delete events"),
        new("documents", "📚", "Documents", "Search your library", null),
        new("memory", "🧠", "Memory", "Recall memories", "Save and forget memories"),
        new("teamwork", "💬", "Teamwork", null, "Message other bots"),
        new("skills", "📒", "Skills", null, "Write and improve skills"),
        new("remote", "🛰️", "Remote computers", null, "Run commands over SSH"),
        new("images", "🎨", "Images", null, "Generate images"),
        new("inbox", "📮", "Bot inbox", null, "Reply to email sent to the bot"),
    ];

    /// <summary>Area and whether the tool changes things; null area = always allowed (calculator, clock, skills, tasks).</summary>
    public static (string? area, bool write) Classify(ITool tool)
    {
        if (tool is IPermissionInfo p) return (p.Area, p.IsWrite);
        var n = tool.Name;
        return n switch
        {
            "web_search" or "fetch_url" => ("web", false),
            "browser_open" or "browser_snapshot" or "browser_scroll" or "browser_back" or "browser_screenshot" => ("browser", false),
            _ when n.StartsWith("browser_") => ("browser", true),
            "desktop_screenshot" => ("desktop", false),
            _ when n.StartsWith("desktop_") => ("desktop", true),
            "list_files" or "read_file" => ("files", false),
            "write_file" => ("files", true),
            "run_python" or "run_command" => ("code", true),
            "sheets_find" or "sheets_info" or "sheets_read" => ("sheets", false),
            _ when n.StartsWith("sheets_") => ("sheets", true),
            "gmail_search" or "gmail_read" => ("gmail", false),
            "gmail_send" => ("gmail.send", true),
            _ when n.StartsWith("gmail_") => ("gmail", true),
            "calendar_events" => ("calendar", false),
            _ when n.StartsWith("calendar_") => ("calendar", true),
            "search_documents" => ("documents", false),
            "recall_memory" or "search_past_chats" or "read_past_chat" => ("memory", false),
            "save_memory" or "delete_memory" => ("memory", true),
            "message_bot" => ("teamwork", true),
            "save_skill" or "improve_skill" => ("skills", true),
            "generate_image" => ("images", true),
            _ => (null, false),
        };
    }

    public static string Key(string area, bool write) => $"{area}.{(write ? "write" : "read")}";

    public static PermissionLevel LevelFor(ITool tool, AppSettings s)
    {
        var (area, write) = Classify(tool);
        return area is null ? PermissionLevel.Allow : s.Permissions.GetValueOrDefault(Key(area, write), PermissionLevel.Default);
    }

    public static bool IsBlocked(ITool tool, AppSettings s) => LevelFor(tool, s) == PermissionLevel.Block;

    /// <summary>Whether running this tool needs the user's OK.</summary>
    public static bool NeedsApproval(ITool tool, AppSettings s, Tools.ToolContext? ctx = null) => LevelFor(tool, s) switch
    {
        PermissionLevel.Allow => false,
        PermissionLevel.Ask => true,
        PermissionLevel.Block => true,
        _ => (ctx is not null && tool is Tools.IContextualApproval c ? c.RequiresApprovalFor(ctx) : tool.RequiresApproval) && !s.Tools.AutoApproveAll,
    };
}
