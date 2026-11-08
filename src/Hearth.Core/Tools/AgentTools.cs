using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Agents;

namespace Hearth.Core.Tools;

/// <summary>
/// Lets bots hand jobs to the other AI agents on this computer (Hermes, Claude Code, Gemini CLI, OpenClaw, Codex)
/// and follow up on them: the coordinator can split work between Hearth's bots and these agents.
/// </summary>
public sealed class AgentToolProvider(AgentHub hub, LinkedFolders folders) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!hub.Available) return [];
        bool On(AgentStatus a) => a.Installed && settings.Agents.For(a.Def.Id).Enabled;
        var ready = (await hub.StatusAsync()).Where(On).ToList();
        // Other computers: what's already known (asking over SSH here would hold up every reply); unknown ones are checked in the background.
        var nodes = new List<(RemoteHost host, IReadOnlyList<AgentStatus> agents)>();
        foreach (var h in settings.RemoteHosts)
        {
            if (hub.KnownNodeStatus(h.Id) is { } known) { if (known.Any(On)) nodes.Add((h, known.Where(On).ToList())); }
            else _ = hub.NodeStatusAsync(h).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        return ready.Count == 0 && nodes.Count == 0 ? [] : [new HandOffToAgentTool(hub, folders, ready, nodes), new FollowUpAgentTool(hub)];
    }
}

internal static class AgentReport
{
    public static string Link(AgentRun run) => $"agents/run/{run.Id}";

    /// <summary>What a run came back with, for the bot: the answer, the files it touched, and where the full log is.</summary>
    public static ToolResult For(AgentRun run)
    {
        var took = run.Duration.TotalMinutes >= 1 ? $"{run.Duration.TotalMinutes:0} min" : $"{run.Duration.TotalSeconds:0} s";
        if (run.Active)
            return new ToolResult($"{run.AgentName} is still working (run {run.Id}, {took} so far). The user can follow it at {Link(run)}; " +
                                  "check back with follow_up_agent once it's done.");
        if (run.Status != AgentRunStatus.Done)
            return ToolResult.Error($"{run.AgentName} {(run.Status == AgentRunStatus.Cancelled ? "was stopped" : "stopped")} after {took}: " +
                                    $"{run.Error ?? "no reason given"}. Full log: {Link(run)}");
        var sb = new StringBuilder($"{run.AgentName} finished in {took}{(run.NodeName is { } node ? $" on {node}" : "")} (run {run.Id}).\n\n");
        var answer = run.Result ?? run.LatestAnswer();
        sb.Append(answer.Length > 8000 ? answer[..8000] + "\n…(cut; the full answer is in the log)" : answer.Length == 0 ? "(It didn't write a summary.)" : answer);
        List<string> changed;
        lock (run.Steps)
            changed = run.Steps.Where(s => s is { Kind: AgentStep.Tool, ToolKind: "edit" or "delete" or "move", Status: "completed" })
                .Select(s => s.Content).Distinct().Take(20).ToList();
        if (changed.Count > 0) sb.Append("\n\nChanges it made: ").Append(string.Join("; ", changed));
        List<string> refused;
        lock (run.Steps) refused = run.Steps.Where(s => s.Kind == AgentStep.Note && (s.Content.StartsWith("Refused") || s.Content.StartsWith("You said no"))).Select(s => s.Content).Take(5).ToList();
        if (refused.Count > 0) sb.Append("\n\nNot allowed: ").Append(string.Join("; ", refused));
        sb.Append($"\n\nFull log: {Link(run)}. To continue in the same session, use follow_up_agent with run_id {run.Id}.");
        return new ToolResult(sb.ToString());
    }
}

public sealed class HandOffToAgentTool(AgentHub hub, LinkedFolders folders, IReadOnlyList<AgentStatus> ready,
    IReadOnlyList<(RemoteHost host, IReadOnlyList<AgentStatus> agents)>? nodes = null) : ITool, IPermissionInfo
{
    public string Name => "hand_off_to_agent";
    public string DisplayName => "Hand off to an agent";
    public string Icon => "🤝";
    private IReadOnlyList<(RemoteHost host, IReadOnlyList<AgentStatus> agents)> Nodes => nodes ?? [];
    private IEnumerable<AgentDef> All => ready.Select(a => a.Def).Concat(Nodes.SelectMany(n => n.agents.Select(a => a.Def))).DistinctBy(d => d.Id);

    public string Description =>
        "Give a job to another AI agent and get its answer back. Agents: " +
        string.Join("; ", All.Select(d => $"{d.Id} ({d.Name}: {d.About})")) + ". " +
        (ready.Count > 0 ? $"On this computer: {string.Join(", ", ready.Select(a => a.Def.Id))}. " : "") +
        string.Concat(Nodes.Select(n => $"On the computer \"{n.host.Name}\"{(string.IsNullOrWhiteSpace(n.host.Notes) ? "" : $" ({n.host.Notes})")}: {string.Join(", ", n.agents.Select(a => a.Def.Id))}. ")) +
        "Use it when the user asks for one of them by name, or for a long coding or research job in a folder that one of " +
        "them does well. On this computer it works in this chat's code folder by default, or a linked folder; on another " +
        "computer, give the folder's full path there. It can take minutes. The agent can't see this chat, so write the " +
        "task out in full: the goal, what to change or find, and what to report back.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["agent"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(All.Select(d => (JsonNode)d.Id).ToArray()), ["description"] = "Which agent" },
            ["task"] = new JsonObject { ["type"] = "string", ["description"] = "The job, written out in full" },
            ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Full path of the folder to work in (default: this chat's code folder)" },
            ["computer"] = new JsonObject { ["type"] = "string", ["description"] = "Which computer to run it on (a name from the list; default: this computer)" },
        },
        ["required"] = new JsonArray("agent", "task"),
    };
    public bool RequiresApproval => true;
    public string Area => "agents";
    public string AreaTitle => "Other AI agents";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var agent = args.Str("agent") ?? "";
        var computer = args.Str("computer") is { Length: > 0 } c && !c.Equals("this computer", StringComparison.OrdinalIgnoreCase) ? c : null;
        AgentRun run;
        if (computer is not null)
        {
            var node = Nodes.FirstOrDefault(n => n.host.Name.Equals(computer, StringComparison.OrdinalIgnoreCase));
            if (node.host is null) return ToolResult.Error($"No computer \"{computer}\" with agents. Use one of: this computer{string.Concat(Nodes.Select(n => ", " + n.host.Name))}.");
            if (node.agents.All(a => !a.Def.Id.Equals(agent, StringComparison.OrdinalIgnoreCase) && !a.Def.Name.Equals(agent, StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error($"\"{agent}\" isn't on {node.host.Name}. There: {string.Join(", ", node.agents.Select(a => a.Def.Id))}.");
            if (args.Str("folder") is not { Length: > 0 } there) return ToolResult.Error($"Give the folder's full path on {node.host.Name}.");
            try { run = await hub.StartAsync(agent, args.Str("task") ?? "", there, ctx.Conversation.Id, ctx.Bot?.Id, ctx.Unattended, node.host.Id); }
            catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        }
        else
        {
            if (ready.All(a => !a.Def.Id.Equals(agent, StringComparison.OrdinalIgnoreCase) && !a.Def.Name.Equals(agent, StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error($"\"{agent}\" isn't on this computer. Here: {(ready.Count == 0 ? "none" : string.Join(", ", ready.Select(a => a.Def.Id)))}" +
                                        (Nodes.Count > 0 ? "; or name a computer." : "."));
            var folder = args.Str("folder") is { Length: > 0 } f ? f : ctx.Code?.Directory;
            if (string.IsNullOrWhiteSpace(folder))
                return ToolResult.Error("Say which folder to work in (this chat has no code folder). It has to be a linked folder, or the user can start it from the Agents page.");
            var full = Path.GetFullPath(folder.Replace("~", AgentCatalog.HomeFolder));
            if (!await AllowedAsync(full, ctx))
                return ToolResult.Error($"Bots can only hand off work in this chat's code folder or a linked folder (Documents → Linked folders), and {full} is neither. " +
                                        "The user can run it there from the Agents page.");
            try { run = await hub.StartAsync(agent, args.Str("task") ?? "", full, ctx.Conversation.Id, ctx.Bot?.Id, ctx.Unattended); }
            catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        }
        try { run = await hub.WaitAsync(run.Id, TimeSpan.FromHours(2), ct); }
        catch (OperationCanceledException) { await hub.CancelAsync(run.Id); throw; }
        return AgentReport.For(run);
    }

    private async Task<bool> AllowedAsync(string full, ToolContext ctx)
    {
        static bool Inside(string root, string path)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return path == r || path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        if (ctx.Code?.Directory is { Length: > 0 } code && Inside(code, full)) return true;
        if (ctx.Bot?.Abilities.ProjectFolder is { Length: > 0 } project && Inside(project, full)) return true;
        return (await folders.ListAsync()).Any(l => l.Enabled && Inside(l.Path, full));
    }
}

public sealed class FollowUpAgentTool(AgentHub hub) : ITool, IPermissionInfo
{
    public string Name => "follow_up_agent";
    public string DisplayName => "Follow up with an agent";
    public string Icon => "🤝";
    public string Description =>
        "Continue a job you handed to another agent with hand_off_to_agent, in the same session (it remembers what it " +
        "did): ask for changes, the next step, or more detail. Or leave message empty to check on a run that was still working.";
    public JsonObject Parameters => Schema.Object(
        ("run_id", "string", "The run id from hand_off_to_agent", true),
        ("message", "string", "What to tell the agent (empty: just check on it)", false));
    public bool RequiresApproval => true;
    public string Area => "agents";
    public string AreaTitle => "Other AI agents";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var id = args.Str("run_id") ?? "";
        if (await hub.GetAsync(id) is not { } run) return ToolResult.Error($"No agent run {id}.");
        if (run.ConversationId != ctx.Conversation.Id) return ToolResult.Error("That run was started somewhere else; only its own chat can follow up on it.");
        try
        {
            if (args.Str("message") is { Length: > 0 } message)
            {
                if (run.Active) return ToolResult.Error($"{run.AgentName} is still working on it. Check back with an empty message once it's done.");
                await hub.SendAsync(id, message);
            }
            run = await hub.WaitAsync(id, TimeSpan.FromHours(2), ct);
        }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
        catch (OperationCanceledException) { await hub.CancelAsync(id); throw; }
        return AgentReport.For(run);
    }
}
