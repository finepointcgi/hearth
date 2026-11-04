using System.Text.Json;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services.Google;

namespace Locus.Core.Tools;

/// <summary>Google Sheets tools. Reading is free; anything that changes a sheet asks first unless auto-approved.</summary>
public sealed class SheetsToolProvider(GoogleAuth auth, SheetsClient sheets) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.Google.SheetsEnabled || !await auth.HasScopeAsync(GoogleAuth.SheetsScope)) return [];
        var ask = !settings.Google.AutoApproveEdits;
        var conn = await auth.GetAsync();
        var who = conn.Mode == GoogleAuthMode.ServiceAccount
            ? $"Connected as a service account ({conn.Email}); it can only see spreadsheets shared with it."
            : $"Connected as {conn.Email ?? "the user's Google account"}.";
        JsonObject spreadsheet() => new() { ["type"] = "string", ["description"] = "Spreadsheet URL, ID, or name" };
        JsonObject rows() => new()
        {
            ["type"] = "array",
            ["description"] = "Rows of cell values, e.g. [[\"Name\",\"Total\"],[\"Ann\",\"42\"]]. Formulas like =SUM(B2:B9) work.",
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        };

        return
        [
            new SheetsTool("sheets_find", "Find spreadsheets", "🔎",
                $"List the user's Google Sheets (newest first), optionally filtered by name. {who}",
                Schema.Object(("name", "string", "Part of the name to search for (optional)", false)), false,
                async (a, c) =>
                {
                    var list = await sheets.FindAsync(a.Str("name"), 25, c);
                    return list.Count == 0 ? "No spreadsheets found."
                        : string.Join("\n", list.Select(s => $"- {s.Title} — id {s.Id}{(s.Modified is { } m ? $", edited {m:yyyy-MM-dd}" : "")} {s.Url}"));
                }),
            new SheetsTool("sheets_info", "Spreadsheet info", "📋",
                "Get a spreadsheet's title, link and tabs (with sizes). Use before reading if you don't know the tab names.",
                Obj(("spreadsheet", spreadsheet(), true)), false,
                async (a, c) =>
                {
                    var (s, tabs) = await sheets.InfoAsync(await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c), c);
                    return $"\"{s.Title}\" {s.Url}\nTabs:\n" + string.Join("\n", tabs.Select(t => $"- {t.Title} ({t.Rows} rows × {t.Columns} cols)"));
                }),
            new SheetsTool("sheets_read", "Read sheet", "📖",
                "Read cells from a spreadsheet as a table with row numbers and column letters. Range in A1 notation, " +
                "e.g. \"Sales!A1:F100\" or just \"Sales\" for a whole tab (default: the first tab).",
                Obj(("spreadsheet", spreadsheet(), true), ("range", Str("A1 range or tab name (optional)"), false)), false,
                async (a, c) =>
                {
                    var id = await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c);
                    var range = a.Str("range");
                    if (string.IsNullOrWhiteSpace(range)) range = Quote((await sheets.InfoAsync(id, c)).tabs.FirstOrDefault()?.Title ?? "Sheet1");
                    return SheetsClient.ToTable(await sheets.ReadAsync(id, range, c));
                }),
            new SheetsTool("sheets_write", "Write cells", "✏️",
                "Overwrite cells starting at a range, e.g. range \"Sales!B2\" with rows [[\"42\"]]. Values are entered as if typed (numbers, dates, formulas).",
                Obj(("spreadsheet", spreadsheet(), true), ("range", Str("Top-left cell or A1 range, e.g. Sales!A2"), true), ("rows", rows(), true)), ask,
                async (a, c) => await sheets.WriteAsync(await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c), a.Str("range") ?? "A1", Rows(a), c)),
            new SheetsTool("sheets_append", "Add rows", "➕",
                "Append rows after the last row of data in a tab (good for logs and trackers).",
                Obj(("spreadsheet", spreadsheet(), true), ("tab", Str("Tab name (default: first tab)"), false), ("rows", rows(), true)), ask,
                async (a, c) =>
                {
                    var id = await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c);
                    var tab = a.Str("tab");
                    if (string.IsNullOrWhiteSpace(tab)) tab = (await sheets.InfoAsync(id, c)).tabs.FirstOrDefault()?.Title ?? "Sheet1";
                    return await sheets.AppendAsync(id, Quote(tab), Rows(a), c);
                }),
            new SheetsTool("sheets_create", "Create spreadsheet", "🆕",
                "Create a new Google Sheet, optionally with named tabs and a header row in the first tab.",
                Obj(("title", Str("Spreadsheet title"), true),
                    ("tabs", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Tab names (optional)" }, false),
                    ("headers", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Header row for the first tab (optional)" }, false)), ask,
                async (a, c) =>
                {
                    var tabs = Strings(a["tabs"]);
                    var s = await sheets.CreateAsync(a.Str("title") ?? "Untitled", tabs, c);
                    var headers = Strings(a["headers"]);
                    if (headers.Count > 0) await sheets.WriteAsync(s.Id, Quote(tabs.FirstOrDefault() ?? "Sheet1") + "!A1", [headers], c);
                    return $"Created \"{s.Title}\" — id {s.Id} {s.Url}";
                }),
            new SheetsTool("sheets_add_tab", "Add tab", "🗂️",
                "Add a new tab (sheet) to a spreadsheet.",
                Obj(("spreadsheet", spreadsheet(), true), ("title", Str("New tab name"), true)), ask,
                async (a, c) => await sheets.AddTabAsync(await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c), a.Str("title") ?? "New tab", c)),
            new SheetsTool("sheets_clear", "Clear cells", "🧹",
                "Clear the values in a range (formatting stays).",
                Obj(("spreadsheet", spreadsheet(), true), ("range", Str("A1 range to clear"), true)), ask,
                async (a, c) => await sheets.ClearAsync(await sheets.ResolveAsync(a.Str("spreadsheet") ?? "", c), a.Str("range") ?? "", c)),
        ];
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Obj(params (string name, JsonObject schema, bool required)[] props)
    {
        var p = new JsonObject();
        foreach (var x in props) p[x.name] = x.schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = new JsonArray(props.Where(x => x.required).Select(x => (JsonNode)JsonValue.Create(x.name)!).ToArray()),
        };
    }

    /// <summary>Tab names with spaces or symbols need quotes in A1 notation.</summary>
    public static string Quote(string tab) =>
        tab.StartsWith('\'') || tab.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? tab : $"'{tab.Replace("'", "''")}'";

    /// <summary>Accepts rows as a JSON array, or a JSON string of one (some models stringify).</summary>
    public static List<List<string>> Rows(JsonObject args)
    {
        var node = args["rows"];
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) node = JsonNode.Parse(s);
        if (node is not JsonArray arr) throw new ArgumentException("rows must be an array of rows.");
        return arr.Select(r => r is JsonArray cells
            ? cells.Select(Cell).ToList()
            : [Cell(r)]).ToList();
    }

    private static string Cell(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    private static List<string> Strings(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
            try { n = JsonNode.Parse(s); } catch (JsonException) { return [s]; }
        return n is JsonArray a ? a.Select(Cell).Where(x => x.Length > 0).ToList() : [];
    }
}

internal sealed class SheetsTool(string name, string displayName, string icon, string description, JsonObject parameters,
    bool requiresApproval, Func<JsonObject, CancellationToken, Task<string>> run) : ITool
{
    public string Name => name;
    public string DisplayName => displayName;
    public string Icon => icon;
    public string Description => description;
    public JsonObject Parameters => parameters;
    public bool RequiresApproval => requiresApproval;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        try { return new ToolResult(Schema.Truncate(await run(args, ct), 24000)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ToolResult.Error(ex.Message); }
    }
}
