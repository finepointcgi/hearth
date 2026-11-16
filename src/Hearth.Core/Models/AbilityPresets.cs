namespace Hearth.Core.Models;

/// <summary>
/// One-click sets of abilities for a bot, so nobody has to tune every switch: a careful home helper, a work helper that
/// asks before it changes things, and everything on. Applying one leaves the bot's own setup (its project folder,
/// browser profile and which screen it drives) as it was.
/// </summary>
public static class AbilityPresets
{
    public sealed record Preset(string Id, string Name, string Description, Action<BotAbilities> Apply);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("safe-home", "Safe Home",
            "Searches the web, reads your documents, keeps your calendar and makes pictures. No email, files, code, other apps or screen.",
            a => Set(a, web: true, browser: false, files: false, code: false, computer: false, images: true, documents: true, skills: true,
                bots: true, mcp: false, sheets: false, gmail: false, calendar: true, microsoft: false, docs: false, contacts: false)),
        new("careful-work", "Careful Work",
            "Adds the browser, files, email, Google and Microsoft apps, contacts and connected tools. Code edits ask first; no control of your screen.",
            a =>
            {
                Set(a, web: true, browser: true, files: true, code: true, computer: false, images: true, documents: true, skills: true,
                    bots: true, mcp: true, sheets: true, gmail: true, calendar: true, microsoft: true, docs: true, contacts: true);
                a.CodeMode = CodePermissionMode.Ask;
            }),
        new("full-power", "Full Power",
            "Everything, including using your computer's screen and keyboard, and making code edits without asking each time.",
            a =>
            {
                Set(a, web: true, browser: true, files: true, code: true, computer: true, images: true, documents: true, skills: true,
                    bots: true, mcp: true, sheets: true, gmail: true, calendar: true, microsoft: true, docs: true, contacts: true);
                a.CodeMode = CodePermissionMode.AcceptEdits;
            }),
    ];

    public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The preset these abilities match exactly, or null when they've been tuned by hand.</summary>
    public static Preset? Matching(BotAbilities abilities)
    {
        foreach (var p in All)
        {
            var probe = Copy(abilities);
            p.Apply(probe);
            if (Same(probe, abilities)) return p;
        }
        return null;
    }

    /// <summary>What a bot with these abilities can do, in a few plain lines (for the bot's page and its chats).</summary>
    public static IReadOnlyList<string> Describe(BotAbilities a, bool memory = true)
    {
        var can = new List<string>();
        if (a.WebSearch) can.Add("search the web");
        if (a.Browser) can.Add("browse websites");
        if (a.Gmail || a.Microsoft365) can.Add("read and write email (sending asks you first)");
        if (a.Calendar || a.Microsoft365) can.Add("see and change your calendar");
        if (a.Contacts) can.Add("look up your contacts");
        if (a.Documents) can.Add("read your documents");
        if (a.GoogleDocs || a.GoogleSheets) can.Add("use Google Docs, Drive and Sheets");
        if (a.Files) can.Add("read and write files in its workspace");
        if (a.Code) can.Add(a.CodeMode == CodePermissionMode.AcceptEdits ? "run code and edit projects" : "run code and edit projects (asking first)");
        if (a.ComputerUse) can.Add(a.Desktop == DesktopTarget.Sandbox ? "use a private sandbox desktop" : "use your computer's screen and keyboard");
        if (a.Images) can.Add("make pictures");
        if (a.MessageBots) can.Add("hand work to other bots");
        if (a.Mcp) can.Add("use connected tools");
        if (a.Skills) can.Add("use saved skills");
        if (memory) can.Add("remember things about you");

        var cannot = new List<string>();
        if (!a.Gmail && !a.Microsoft365) cannot.Add("email");
        if (!a.Browser) cannot.Add("the browser");
        if (!a.Code) cannot.Add("code");
        if (!a.ComputerUse) cannot.Add("your screen");
        if (!a.Contacts) cannot.Add("contacts");

        List<string> lines = [can.Count == 0 ? "Can only talk." : "Can " + Join(can) + "."];
        if (cannot.Count > 0) lines.Add("No access to " + Join(cannot) + ".");
        return lines;
    }

    private static string Join(List<string> items) => items.Count <= 1 ? string.Join("", items)
        : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static void Set(BotAbilities a, bool web, bool browser, bool files, bool code, bool computer, bool images, bool documents,
        bool skills, bool bots, bool mcp, bool sheets, bool gmail, bool calendar, bool microsoft, bool docs, bool contacts)
    {
        a.WebSearch = web; a.Browser = browser; a.Files = files; a.Code = code; a.ComputerUse = computer; a.Images = images;
        a.Documents = documents; a.Skills = skills; a.MessageBots = bots; a.Mcp = mcp; a.GoogleSheets = sheets; a.Gmail = gmail;
        a.Calendar = calendar; a.Microsoft365 = microsoft; a.GoogleDocs = docs; a.Contacts = contacts;
    }

    private static BotAbilities Copy(BotAbilities a) =>
        System.Text.Json.JsonSerializer.Deserialize<BotAbilities>(System.Text.Json.JsonSerializer.Serialize(a))!;

    private static bool Same(BotAbilities x, BotAbilities y) =>
        System.Text.Json.JsonSerializer.Serialize(x) == System.Text.Json.JsonSerializer.Serialize(y);
}
