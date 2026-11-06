using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Tools;

/// <summary>
/// A visible plan for long tasks in ordinary chats (code sessions have todo_write): the bot writes a short checklist
/// and ticks it off, shown above the chat so the user can follow along and redirect.
/// </summary>
public sealed class UpdatePlanTool : ITool
{
    public const string ToolName = "update_plan";
    public string Name => ToolName;
    public string DisplayName => "Plan";
    public string Icon => "📋";
    public string Description =>
        "Show the user your plan for a task with 3+ steps (research, a job application, trip planning…): write the whole " +
        "checklist, mark one step in_progress while you work on it and completed when done. Each call replaces the list.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["step"] = new JsonObject { ["type"] = "string", ["description"] = "Short step, e.g. Compare 3 flights" },
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("pending", "in_progress", "completed") },
                    },
                    ["required"] = new JsonArray("step", "status"),
                },
            },
        },
        ["required"] = new JsonArray("steps"),
    };
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var items = args["steps"] switch
        {
            JsonArray a => a,
            JsonValue v when v.TryGetValue<string>(out var s) => JsonNode.Parse(s) as JsonArray,
            _ => null,
        };
        if (items is null) return Task.FromResult(ToolResult.Error("steps must be an array of {step, status}."));
        var plan = items.OfType<JsonObject>()
            .Select(i => (text: (i.Str("step") ?? i.Str("content"))?.Trim(), status: i.Str("status")))
            .Where(i => !string.IsNullOrEmpty(i.text))
            .Select(i => new CodeTodo(i.text!, i.status switch
            {
                "completed" or "done" => CodeTodoStatus.Completed,
                "in_progress" or "doing" => CodeTodoStatus.InProgress,
                _ => CodeTodoStatus.Pending,
            })).Take(20).ToList();
        ctx.Conversation.Plan = plan;
        var done = plan.Count(p => p.Status == CodeTodoStatus.Completed);
        return Task.FromResult(new ToolResult($"Plan updated ({done}/{plan.Count} done). The user sees it above the chat."));
    }
}

public sealed class PlanToolProvider : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ITool>>([new UpdatePlanTool()]);
}
