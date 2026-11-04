using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Tools;

/// <summary>Tools a bot gets while working on a long-running task, to keep its plan and report status.</summary>
public static class TaskTools
{
    public static IReadOnlyList<ITool> For(AgentTask task) =>
    [
        new TaskTool("task_plan", "Plan", "🗺️",
            "Set (or replace) the plan for this task as 3–10 concrete steps, in order.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["steps"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                ["required"] = new JsonArray("steps"),
            },
            a =>
            {
                var node = a["steps"];
                if (node is JsonValue v && v.TryGetValue<string>(out var s)) node = JsonNode.Parse(s);
                var steps = (node as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                if (steps.Count == 0) return "Give at least one step.";
                var done = task.Plan.Where(p => p.Status == "done").ToList();
                task.Plan = steps.Select(x => done.FirstOrDefault(d => d.Text == x) ?? new TaskStep { Text = x }).ToList();
                task.Note($"Plan: {string.Join(" · ", steps)}");
                return $"Plan saved with {steps.Count} steps.";
            }),
        new TaskTool("task_step", "Progress", "📍",
            "Report progress on a step: status working, done, skipped or failed, with a short note of what you found or did.",
            Schema.Object(("step", "integer", "Step number (1-based)", true), ("status", "string", "working, done, skipped or failed", true),
                ("note", "string", "What you did / found (one or two sentences)", false)),
            a =>
            {
                var n = (int)(a.Num("step") ?? 0);
                if (n < 1 || n > task.Plan.Count) return $"There is no step {n}; the plan has {task.Plan.Count} steps.";
                var step = task.Plan[n - 1];
                step.Status = (a.Str("status") ?? "done").ToLowerInvariant() is var st && st is "working" or "done" or "skipped" or "failed" ? st : "done";
                step.Note = a.Str("note");
                task.IdleCycles = 0;
                task.Note($"Step {n} {step.Status}: {step.Text}{(step.Note is null ? "" : " — " + step.Note)}");
                return $"Step {n} marked {step.Status}. {task.DoneSteps}/{task.Plan.Count} done.";
            }),
        new TaskTool("task_ask", "Ask the user", "❓",
            "Ask the user for a decision or information only they have, then stop working until they answer. Don't ask about things you can find out yourself.",
            Schema.Object(("question", "string", "A clear, specific question (offer options if you can)", true)),
            a =>
            {
                task.Question = a.Str("question") ?? "Can you help me decide how to continue?";
                task.State = TaskState.WaitingForYou;
                task.Note($"Asked: {task.Question}");
                return "Question sent to the user. Stop here; you'll continue when they answer.";
            }),
        new TaskTool("task_done", "Finish", "🏁",
            "Finish the task with a final report: what was done, the results, and anything the user should do next.",
            Schema.Object(("report", "string", "The final report, in Markdown", true)),
            a =>
            {
                task.Result = a.Str("report") ?? "";
                task.State = TaskState.Done;
                foreach (var s in task.Plan.Where(s => s.Status is "todo" or "working")) s.Status = "done";
                task.Note("Finished.");
                return "Task complete.";
            }),
    ];
}

internal sealed class TaskTool(string name, string displayName, string icon, string description, JsonObject parameters,
    Func<JsonObject, string> run) : ITool
{
    public string Name => name;
    public string DisplayName => displayName;
    public string Icon => icon;
    public string Description => description;
    public JsonObject Parameters => parameters;
    public bool RequiresApproval => false;
    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult(run(args)));
}
