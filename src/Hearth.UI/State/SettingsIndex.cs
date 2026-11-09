namespace Hearth.UI.State;

/// <summary>A setting that lives on another settings page, found by Settings search.</summary>
public sealed record SettingsLink(string Title, string Page, string Href, string Icon, string Words);

/// <summary>Settings on the other settings pages (Connections, Permissions, Tools, Backup, Computer), so searching
/// on the main Settings page can link to them. Keep the words close to what each page says.</summary>
public static class SettingsIndex
{
    public static readonly IReadOnlyList<SettingsLink> All =
    [
        new("Phone app and notifications", "Connections", "connections", "link", "phone mobile app pair qr code push notifications alerts"),
        new("Reach Hearth away from home", "Connections", "connections", "link", "remote access phone tunnel cloudflare public link netbird wireguard internet away"),
        new("Telegram, Discord, Slack, Signal and iMessage", "Connections", "connections", "link", "chat apps telegram discord slack signal imessage messages bots quiet hours"),
        new("Google (Sheets, Gmail, Calendar)", "Connections", "connections", "link", "google sheets gmail calendar spreadsheets sign in account"),
        new("Email accounts", "Connections", "connections", "link", "email mail imap smtp inbox outlook microsoft"),
        new("API and webhooks", "Connections", "connections", "link", "api key openai compatible endpoint webhooks outgoing integrations"),
        new("Permissions", "Permissions", "permissions", "shield", "permissions allow ask read write files email calendar safety what bots can do"),
        new("Always allowed tools", "Permissions", "permissions", "shield", "always allow approvals approval cards trusted tools"),
        new("What bots did", "Permissions", "permissions", "shield", "activity log audit history actions what bots did"),
        new("Tool calling", "Tools and MCP", "tools", "plug", "tools on by default tool calling rounds auto approve python guard untrusted content"),
        new("Built-in tools", "Tools and MCP", "tools", "plug", "built-in tools turn off disable web search files python sandbox helpers delegate"),
        new("MCP servers and integrations", "Tools and MCP", "tools", "plug", "mcp model context protocol servers integrations github home assistant databases"),
        new("Back up now", "Backup and restore", "backup", "archive-restore", "backup export download file save copy"),
        new("Daily copies", "Backup and restore", "backup", "archive-restore", "daily backups automatic schedule copies keep"),
        new("Restore from a backup", "Backup and restore", "backup", "archive-restore", "restore import backup file move computer"),
        new("Bot computer (browser)", "Computer", "computer", "monitor", "computer browser playwright chrome edge headless background idle shell commands workspace"),
        new("Computer use (screen, mouse, keyboard)", "Computer", "computer", "monitor", "computer use screen mouse keyboard click type desktop apps"),
        new("Saved logins", "Computer", "computer", "monitor", "saved logins sign ins passwords cookies websites browser"),
        new("Workspace files", "Computer", "computer", "monitor", "workspace files folder downloads"),
        new("Remote computers (SSH)", "Computer", "computer", "monitor", "remote computers ssh hosts servers commands"),
    ];

    /// <summary>Links whose title, page or words contain every word of the query.</summary>
    public static IEnumerable<SettingsLink> Find(string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        return All.Where(l =>
        {
            var text = $"{l.Title} {l.Page} {l.Words}".ToLowerInvariant();
            return words.All(text.Contains);
        });
    }
}
