using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Locus.Core.Services.Google;

public sealed record SpreadsheetRef(string Id, string Title, string? Url = null, DateTimeOffset? Modified = null);
public sealed record SheetTab(string Title, int Index, int Rows, int Columns);
public sealed record SheetValues(string Range, List<List<string>> Rows);

/// <summary>Thin Google Sheets v4 + Drive v3 REST client.</summary>
public sealed partial class SheetsClient(GoogleAuth auth, IHttpClientFactory http)
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
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; }
            catch (JsonException) { msg = text; }
            throw new InvalidOperationException((int)res.StatusCode switch
            {
                403 or 404 when (await auth.GetAsync()).Mode == GoogleAuthMode.ServiceAccount =>
                    $"{msg} — share the spreadsheet with {(await auth.GetAsync()).Email} (Share → add that email as Editor).",
                _ => $"Google Sheets: {msg}",
            });
        }
        return text.Length == 0 ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }

    // ------------------------------------------------------------------ finding spreadsheets

    public async Task<List<SpreadsheetRef>> FindAsync(string? nameContains, int max, CancellationToken ct)
    {
        var (_, drive, _, _) = await auth.EndpointsAsync();
        var q = "mimeType='application/vnd.google-apps.spreadsheet' and trashed=false";
        if (!string.IsNullOrWhiteSpace(nameContains)) q += $" and name contains '{nameContains.Replace("\\", "\\\\").Replace("'", "\\'")}'";
        var url = $"{drive}/files?q={Uri.EscapeDataString(q)}&orderBy=modifiedTime%20desc&pageSize={Math.Clamp(max, 1, 100)}" +
                  "&fields=files(id,name,modifiedTime,webViewLink)&supportsAllDrives=true&includeItemsFromAllDrives=true";
        var res = await SendAsync(HttpMethod.Get, url, null, ct);
        return res["files"]?.AsArray().Select(f => new SpreadsheetRef(
            f!["id"]!.ToString(), f["name"]?.ToString() ?? "", f["webViewLink"]?.ToString(),
            DateTimeOffset.TryParse(f["modifiedTime"]?.ToString(), out var d) ? d : null)).ToList() ?? [];
    }

    /// <summary>Accepts a spreadsheet URL, an ID, or (part of) its name.</summary>
    public async Task<string> ResolveAsync(string spreadsheet, CancellationToken ct)
    {
        var s = spreadsheet.Trim();
        if (UrlId().Match(s) is { Success: true } m) return m.Groups[1].Value;
        if (RawId().IsMatch(s)) return s;
        var hits = await FindAsync(s, 10, ct);
        return (hits.FirstOrDefault(h => h.Title.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? hits.FirstOrDefault())?.Id
            ?? throw new InvalidOperationException($"No spreadsheet named \"{s}\" found. Use sheets_find to list them, or give its URL.");
    }

    [GeneratedRegex(@"/spreadsheets/d/([a-zA-Z0-9_-]{20,})")] private static partial Regex UrlId();
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{30,}$")] private static partial Regex RawId();
    [GeneratedRegex(@"^\$?[A-Za-z]{1,3}\$?\d*(:\$?[A-Za-z]{1,3}\$?\d*)?$")] private static partial Regex CellRef();

    // ------------------------------------------------------------------ reading

    public async Task<(SpreadsheetRef sheet, List<SheetTab> tabs)> InfoAsync(string id, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var res = await SendAsync(HttpMethod.Get,
            $"{sheets}/spreadsheets/{id}?fields=spreadsheetId,spreadsheetUrl,properties.title,sheets.properties", null, ct);
        var tabs = res["sheets"]?.AsArray().Select(t => t!["properties"]!).Select(p => new SheetTab(
            p["title"]!.ToString(), p["index"]?.GetValue<int>() ?? 0,
            p["gridProperties"]?["rowCount"]?.GetValue<int>() ?? 0,
            p["gridProperties"]?["columnCount"]?.GetValue<int>() ?? 0)).ToList() ?? [];
        return (new SpreadsheetRef(id, res["properties"]?["title"]?.ToString() ?? "", res["spreadsheetUrl"]?.ToString()), tabs);
    }

    public async Task<SheetValues> ReadAsync(string id, string range, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var res = await SendAsync(HttpMethod.Get,
            $"{sheets}/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}?valueRenderOption=FORMATTED_VALUE", null, ct);
        var rows = res["values"]?.AsArray().Select(r => r!.AsArray().Select(c => c?.ToString() ?? "").ToList()).ToList() ?? [];
        return new SheetValues(res["range"]?.ToString() ?? range, rows);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Cells as entered (formulas, not their results), for putting a range back exactly.</summary>
    public async Task<List<List<string>>> ReadFormulasAsync(string id, string range, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var res = await SendAsync(HttpMethod.Get, $"{sheets}/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}?valueRenderOption=FORMULA", null, ct);
        return res["values"]?.AsArray().Select(r => r!.AsArray().Select(c => c?.ToString() ?? "").ToList()).ToList() ?? [];
    }

    /// <summary>Offers an undo step that puts <paramref name="block"/> back as it is now.</summary>
    private async Task OfferPutBackAsync(string id, string? block, string description, CancellationToken ct)
    {
        if (!Services.UndoScope.Active || block is null) return;
        try
        {
            var bang = block.LastIndexOf('!');
            var cells = (bang >= 0 ? block[(bang + 1)..] : block).Split(':');
            var m1 = System.Text.RegularExpressions.Regex.Match(cells[0], "^([A-Z]+)([0-9]+)$");
            var m2 = System.Text.RegularExpressions.Regex.Match(cells[^1], "^([A-Z]+)([0-9]+)$");
            var rows = int.Parse(m2.Groups[2].Value) - int.Parse(m1.Groups[2].Value) + 1;
            var cols = Services.A1.ColumnNumber(m2.Groups[1].Value) - Services.A1.ColumnNumber(m1.Groups[1].Value) + 1;
            if (rows * cols > 20000) return;
            var before = Services.A1.Pad(await ReadFormulasAsync(id, block, ct), rows, cols);
            Services.UndoScope.Offer(new Services.UndoStep("sheets_values", description, new JsonObject
            {
                ["id"] = id, ["range"] = block, ["rows"] = ToJson(before),
            }));
        }
        catch { /* no undo for this one, but the change still goes ahead */ }
    }

    public async Task<string> WriteAsync(string id, string range, List<List<string>> rows, CancellationToken ct)
    {
        await OfferPutBackAsync(id, Services.A1.Block(range, rows.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count)), $"Put back {range}", ct);
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var res = await SendAsync(HttpMethod.Put,
            $"{sheets}/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}?valueInputOption=USER_ENTERED",
            new JsonObject { ["range"] = range, ["values"] = ToJson(rows) }, ct);
        return $"Updated {res["updatedCells"] ?? 0} cells in {res["updatedRange"] ?? range}.";
    }

    public async Task<string> AppendAsync(string id, string range, List<List<string>> rows, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var res = await SendAsync(HttpMethod.Post,
            $"{sheets}/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS",
            new JsonObject { ["values"] = ToJson(rows) }, ct);
        if (res["updates"]?["updatedRange"]?.ToString() is { Length: > 0 } added)
            Services.UndoScope.Offer(new Services.UndoStep("sheets_clear", $"Clear the added rows {added}", new JsonObject { ["id"] = id, ["range"] = added }));
        return $"Appended {rows.Count} row(s) at {res["updates"]?["updatedRange"] ?? range}.";
    }

    public async Task<string> ClearAsync(string id, string range, CancellationToken ct)
    {
        if (Services.UndoScope.Active)
            try
            {
                var before = await ReadAsync(id, range, ct); // resolves the range actually holding data
                var cols = before.Rows.Count == 0 ? 0 : before.Rows.Max(r => r.Count);
                var block = Services.A1.Block(before.Range, before.Rows.Count, cols);
                if (block is not null) await OfferPutBackAsync(id, block, $"Put back {range}", ct);
            }
            catch { }
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        await SendAsync(HttpMethod.Post, $"{sheets}/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}:clear", new JsonObject(), ct);
        return $"Cleared {range}.";
    }

    public async Task<SpreadsheetRef> CreateAsync(string title, IReadOnlyList<string> tabs, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        var body = new JsonObject { ["properties"] = new JsonObject { ["title"] = title } };
        if (tabs.Count > 0)
            body["sheets"] = new JsonArray(tabs.Select(t => (JsonNode)new JsonObject { ["properties"] = new JsonObject { ["title"] = t } }).ToArray());
        var res = await SendAsync(HttpMethod.Post, $"{sheets}/spreadsheets", body, ct);
        Services.UndoScope.Offer(new Services.UndoStep("gdrive_trash", $"Move \"{title}\" to the trash", new JsonObject { ["id"] = res["spreadsheetId"]!.ToString() }));
        return new SpreadsheetRef(res["spreadsheetId"]!.ToString(), title, res["spreadsheetUrl"]?.ToString());
    }

    public async Task<string> AddTabAsync(string id, string title, CancellationToken ct)
    {
        var (sheets, _, _, _) = await auth.EndpointsAsync();
        await SendAsync(HttpMethod.Post, $"{sheets}/spreadsheets/{id}:batchUpdate", new JsonObject
        {
            ["requests"] = new JsonArray(new JsonObject { ["addSheet"] = new JsonObject { ["properties"] = new JsonObject { ["title"] = title } } }),
        }, ct);
        return $"Added tab \"{title}\".";
    }

    private static JsonArray ToJson(List<List<string>> rows) =>
        new(rows.Select(r => (JsonNode)new JsonArray(r.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())).ToArray());

    // ------------------------------------------------------------------ helpers

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string ColumnName(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    /// <summary>Top-left cell of an A1 range like "Sales!B3:F20" → (column index, row number).</summary>
    public static (int col, int row) StartOf(string range)
    {
        var cell = range.Contains('!') ? range[(range.LastIndexOf('!') + 1)..] : range;
        if (!range.Contains('!') && !CellRef().IsMatch(cell)) return (0, 1); // a bare tab name like "Sheet1"
        cell = cell.Split(':')[0].Replace("$", "");
        var letters = new string(cell.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        var digits = new string(cell.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
        var col = letters.Length == 0 ? 0 : letters.Aggregate(0, (acc, ch) => acc * 26 + (ch - 'A' + 1)) - 1;
        return (col, int.TryParse(digits, out var r) ? r : 1);
    }

    /// <summary>Renders values as a Markdown table with real row numbers and column letters.</summary>
    public static string ToTable(SheetValues v, int maxRows = 300)
    {
        if (v.Rows.Count == 0) return $"{v.Range} is empty.";
        var (col0, row0) = StartOf(v.Range);
        var width = v.Rows.Max(r => r.Count);
        var sb = new StringBuilder($"Range {v.Range} — {v.Rows.Count} row(s) × {width} column(s)\n\n");
        sb.Append("| row | ").Append(string.Join(" | ", Enumerable.Range(col0, width).Select(ColumnName))).AppendLine(" |");
        sb.Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", width))).AppendLine();
        foreach (var (row, i) in v.Rows.Take(maxRows).Select((r, i) => (r, i)))
            sb.Append($"| {row0 + i} | ")
              .Append(string.Join(" | ", Enumerable.Range(0, width).Select(c => c < row.Count ? row[c].Replace("|", "\\|").Replace("\n", " ") : "")))
              .AppendLine(" |");
        if (v.Rows.Count > maxRows) sb.AppendLine($"…{v.Rows.Count - maxRows} more rows not shown; read a narrower range.");
        return sb.ToString();
    }
}
