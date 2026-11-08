using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services.Google;
using Hearth.Core.Services.Microsoft;

namespace Hearth.Core.Services;

/// <summary>How to reverse one change a tool made: delete the event it added, put back the cells it overwrote…</summary>
public sealed record UndoStep(string Kind, string Description, JsonObject Data);

/// <summary>
/// Collects the undo steps offered while one tool call runs. The clients that change things (Sheets, Excel, OneDrive,
/// calendars, workspace files) call <see cref="Offer"/>; outside a tool call nothing is collected.
/// </summary>
public static class UndoScope
{
    private static readonly AsyncLocal<List<UndoStep>?> Current = new();

    public static IDisposable Begin(out List<UndoStep> steps)
    {
        var previous = Current.Value;
        Current.Value = steps = [];
        return new Restore(previous);
    }

    public static bool Active => Current.Value is not null;

    public static void Offer(UndoStep step)
    {
        var list = Current.Value;
        if (list is null) return;
        lock (list) list.Add(step);
    }

    private sealed class Restore(List<UndoStep>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>A change a bot made that can still be undone.</summary>
public sealed class UndoEntry
{
    /// <summary>The tool call that made the change.</summary>
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public string? BotId { get; set; }
    public string? ConversationId { get; set; }
    public List<UndoStep> Steps { get; set; } = [];
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? UndoneAt { get; set; }
    public string? Result { get; set; }
}

/// <summary>
/// Undo for bot actions (undo.json): files, documents, spreadsheet edits and calendar events a bot made can be reversed
/// from the tool card for <see cref="Window"/>.
/// </summary>
public sealed class UndoLog(IAppPaths paths, IServiceProvider services)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<UndoEntry>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "undo.json");

    public event Action? Changed;

    private async Task<List<UndoEntry>> LoadAsync() => _cache ??= await Json.ReadAsync<List<UndoEntry>>(FilePath) ?? [];

    public async Task AddAsync(string toolCallId, string tool, string? botId, string? conversationId, IReadOnlyList<UndoStep> steps)
    {
        if (steps.Count == 0) return;
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.RemoveAll(e => e.Id == toolCallId || DateTimeOffset.Now - e.At > Window);
            list.Add(new UndoEntry { Id = toolCallId, Tool = tool, BotId = botId, ConversationId = conversationId, Steps = steps.ToList() });
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task<UndoEntry?> GetAsync(string toolCallId)
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).FirstOrDefault(e => e.Id == toolCallId); }
        finally { _lock.Release(); }
    }

    /// <summary>Changes that can still be undone, newest first.</summary>
    public async Task<List<UndoEntry>> RecentAsync()
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).Where(CanUndo).OrderByDescending(e => e.At).ToList(); }
        finally { _lock.Release(); }
    }

    public static bool CanUndo(UndoEntry e) => e.UndoneAt is null && DateTimeOffset.Now - e.At <= Window;

    /// <summary>Reverses a tool call's changes (last change first). Returns what happened.</summary>
    public async Task<string> UndoAsync(string toolCallId, CancellationToken ct = default)
    {
        var entry = await GetAsync(toolCallId) ?? throw new InvalidOperationException("There's nothing to undo for this.");
        if (entry.UndoneAt is not null) return "Already undone." + (entry.Result is { } before ? " " + before : "");
        if (!CanUndo(entry)) throw new InvalidOperationException($"Too late to undo: changes can be undone for {Window.TotalHours:0} hours.");
        var done = new List<string>();
        var failed = new List<string>();
        for (var i = entry.Steps.Count - 1; i >= 0; i--)
        {
            var step = entry.Steps[i];
            try { done.Add(await RunAsync(step, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add($"{step.Description}: {ex.Message}"); }
        }
        var result = string.Join(" ", done) + (failed.Count > 0 ? (done.Count > 0 ? " " : "") + "Couldn't undo: " + string.Join("; ", failed) : "");
        await _lock.WaitAsync();
        try
        {
            if (failed.Count == 0) entry.UndoneAt = DateTimeOffset.Now;
            entry.Result = result;
            await Json.WriteAtomicAsync(FilePath, await LoadAsync());
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        if (failed.Count > 0 && done.Count == 0) throw new InvalidOperationException(result);
        return result;
    }

    private T Get<T>() where T : notnull => (T)(services.GetService(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} isn't available."));

    private async Task<string> RunAsync(UndoStep step, CancellationToken ct)
    {
        var d = step.Data;
        string S(string key) => d[key]?.ToString() ?? "";
        switch (step.Kind)
        {
            case "file":
            {
                var path = S("path");
                if (File.Exists(path) && d["after"]?.ToString() is { } after && Hash(await File.ReadAllBytesAsync(path, ct)) != after)
                    throw new InvalidOperationException("the file has changed since, so it was left alone");
                if (d["previous"] is null) { if (File.Exists(path)) File.Delete(path); return $"Deleted {Path.GetFileName(path)}."; }
                await File.WriteAllTextAsync(path, S("previous"), ct);
                return $"Put back the earlier {Path.GetFileName(path)}.";
            }
            case "gcal_event":
                await Get<WorkspaceClient>().DeleteEventAsync(S("id"), ct);
                return "Deleted the calendar event.";
            case "outlook_event":
                await Get<GraphClient>().DeleteEventAsync(S("id"), ct);
                return "Deleted the Outlook event.";
            case "sheets_values":
                await Get<SheetsClient>().WriteAsync(S("id"), S("range"), Grid(d["rows"]), ct);
                return $"Put back {S("range")}.";
            case "sheets_clear":
                await Get<SheetsClient>().ClearAsync(S("id"), S("range"), ct);
                return $"Cleared the added rows ({S("range")}).";
            case "gdrive_trash":
                await Get<DriveClient>().TrashAsync(S("id"), ct);
                return "Moved the new file to the Google Drive trash.";
            case "onedrive_file":
                if (d["previous"] is null)
                {
                    await Get<GraphClient>().DeleteItemAsync(S("id"), ct);
                    return $"Moved {S("name")} to the OneDrive recycle bin.";
                }
                await Get<GraphClient>().ReplaceContentAsync(S("id"), Convert.FromBase64String(S("previous")), ct);
                return $"Put back the earlier {S("name")}.";
            case "excel_values":
                await Get<GraphClient>().WriteExcelFormulasAsync(S("id"), S("sheet"), S("range"), d["formulas"]!.AsArray(), ct);
                return $"Put back {S("sheet")}!{S("range")}.";
            default:
                throw new InvalidOperationException($"unknown change type {step.Kind}");
        }
    }

    private static List<List<string>> Grid(JsonNode? node) =>
        (node as JsonArray ?? []).Select(r => (r as JsonArray ?? []).Select(c => c?.ToString() ?? "").ToList()).ToList();

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
}

/// <summary>A1 ranges: the block a write of <c>rows × cols</c> starting at a cell covers.</summary>
public static class A1
{
    /// <summary>"Sales!B2" + 3×2 → "Sales!B2:C4". Null when the start isn't a single cell or a range.</summary>
    public static string? Block(string range, int rows, int cols)
    {
        if (rows == 0 || cols == 0) return null;
        var bang = range.LastIndexOf('!');
        var tab = bang >= 0 ? range[..(bang + 1)] : "";
        var cells = bang >= 0 ? range[(bang + 1)..] : range;
        var start = cells.Split(':')[0].Replace("$", "");
        var m = System.Text.RegularExpressions.Regex.Match(start, "^([A-Za-z]{1,3})([0-9]+)$");
        if (!m.Success) return null;
        var col = ColumnNumber(m.Groups[1].Value);
        var row = int.Parse(m.Groups[2].Value);
        return $"{tab}{ColumnName(col)}{row}:{ColumnName(col + cols - 1)}{row + rows - 1}";
    }

    public static int ColumnNumber(string letters) => letters.ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + (ch - 'A' + 1));

    public static string ColumnName(int n)
    {
        var s = "";
        while (n > 0) { var r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; }
        return s;
    }

    /// <summary>Pads a grid to rows × cols with empty cells (writing an empty cell clears it).</summary>
    public static List<List<string>> Pad(List<List<string>> grid, int rows, int cols) =>
        Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, cols)
            .Select(c => r < grid.Count && c < grid[r].Count ? grid[r][c] : "").ToList()).ToList();
}
