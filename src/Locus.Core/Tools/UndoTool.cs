using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tools;

/// <summary>
/// undo_change: reverses a change made earlier in this chat (a file, document, spreadsheet edit or calendar event),
/// for when the user says "undo that" — from the app, Telegram or a call.
/// </summary>
public sealed class UndoChangeTool(UndoLog undo) : ITool
{
    public const string ToolName = "undo_change";
    public string Name => ToolName;
    public string DisplayName => "Undo change";
    public string Icon => "↶";
    public string Description =>
        $"Undo a change made earlier in this chat (a file written, a document or spreadsheet created or edited, a calendar event added), " +
        $"within {UndoLog.Window.TotalHours:0} hours. Call without id to list what can be undone; then call with the id. Only when the user asks.";
    public JsonObject Parameters => Schema.Object(("id", "string", "The change to undo (from the list); leave out to list them", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var mine = (await undo.RecentAsync()).Where(e => e.ConversationId == ctx.Conversation.Id).ToList();
        var id = args.Str("id")?.Trim();
        if (string.IsNullOrEmpty(id))
            return new ToolResult(mine.Count == 0 ? "Nothing in this chat can be undone."
                : "Can be undone (newest first):\n" + string.Join("\n", mine.Select(e =>
                    $"- id {e.Id}: {Label(e.Tool)} at {e.At:ddd h:mm tt} — {string.Join("; ", e.Steps.Select(s => s.Description))}")));
        var entry = mine.FirstOrDefault(e => e.Id == id);
        if (entry is null) return ToolResult.Error($"No change {id} that can still be undone in this chat. Call undo_change without id to list them.");
        try { return new ToolResult(await undo.UndoAsync(entry.Id, ct)); }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
    }

    private static string Label(string tool) => tool.Replace('_', ' ');
}

public sealed class UndoToolProvider(UndoLog undo) : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ITool>>([new UndoChangeTool(undo)]);
}
