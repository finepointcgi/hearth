using Microsoft.AspNetCore.Components;

namespace Hearth.UI.Layout;

public sealed record ShellSection(string Id, string Title, string Icon, string Href, string[] Routes);

/// <summary>The app's places, and which routes belong to each (so the rail and side panel follow the page).</summary>
public static class ShellSections
{
    public static readonly IReadOnlyList<ShellSection> All =
    [
        new("chats", "Chats", "message-circle", "", ["", "c", "bot"]),
        new("code", "Code", "terminal", "code", ["code"]),
        new("bots", "Bots", "bot", "bots", ["bots", "agents"]),
        new("inbox", "Inbox", "inbox", "dashboard", ["dashboard", "tasks", "usage"]),
        new("library", "Library", "library", "skills", ["skills", "documents", "memory", "files", "teach", "learned"]),
        new("settings", "Settings", "settings", "settings", ["settings", "connections", "permissions", "tools", "computer", "backup"]),
    ];

    public static string Current(NavigationManager nav)
    {
        var path = nav.ToBaseRelativePath(nav.Uri).Split('?', '#')[0];
        var first = path.Split('/', 2)[0];
        return All.FirstOrDefault(s => s.Routes.Contains(first))?.Id ?? "chats";
    }
}
