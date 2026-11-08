using Hearth.Core.Models;

namespace Hearth.Core.Services.Agents;

public enum AgentRunStatus { Running, WaitingForYou, Done, Failed, Cancelled }

/// <summary>One thing an agent did or said during a run.</summary>
public sealed class AgentStep
{
    public const string Text = "text", Thought = "thought", Tool = "tool", Plan = "plan", You = "you", Note = "note";

    public string Kind { get; set; } = Text;
    /// <summary>The agent's id for a tool call (so updates find it).</summary>
    public string? Id { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Tool calls: read, edit, execute, search, fetch, think, other.</summary>
    public string? ToolKind { get; set; }
    /// <summary>Tool calls: pending, in_progress, completed, failed.</summary>
    public string? Status { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

/// <summary>A job handed to an outside agent: what was asked, where, everything it did, and how it ended.</summary>
public sealed class AgentRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Task { get; set; } = "";
    public string Folder { get; set; } = "";
    public AgentMode Mode { get; set; }
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Ended { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public List<AgentStep> Steps { get; set; } = [];
    /// <summary>The agent's final answer for the latest prompt.</summary>
    public string? Result { get; set; }
    public string? Error { get; set; }
    /// <summary>The agent's session id (Claude Code resumes it for follow-ups).</summary>
    public string? SessionId { get; set; }
    /// <summary>The chat and bot that handed it over (null: started from the Agents page).</summary>
    public string? ConversationId { get; set; }
    public string? BotId { get; set; }
    /// <summary>No one is watching (a routine): anything that needs an OK is refused.</summary>
    public bool Unattended { get; set; }
    /// <summary>The remote computer it runs on (Remote computers id and name); null: this computer.</summary>
    public string? NodeId { get; set; }
    public string? NodeName { get; set; }
    /// <summary>The approval it's waiting for, while <see cref="AgentRunStatus.WaitingForYou"/>.</summary>
    public string? WaitingOn { get; set; }

    public bool Active => Status is AgentRunStatus.Running or AgentRunStatus.WaitingForYou;

    /// <summary>Where its approval requests are filed (see ApprovalCenter).</summary>
    public string ApprovalKey => "agent-run:" + Id;

    public TimeSpan Duration => (Ended ?? DateTimeOffset.Now) - Started;

    /// <summary>Appends streamed text to the last step of the same kind, or starts a new one.</summary>
    public void AddText(string kind, string text)
    {
        if (text.Length == 0) return;
        lock (Steps)
        {
            if (Steps.Count > 0 && Steps[^1].Kind == kind) Steps[^1].Content += text;
            else Steps.Add(new AgentStep { Kind = kind, Content = text });
            if (Steps.Count > 2000) Steps.RemoveRange(0, Steps.Count - 2000);
        }
    }

    public AgentStep Tool(string? id, string title, string? toolKind, string? status)
    {
        lock (Steps)
        {
            var step = id is null ? null : Steps.LastOrDefault(s => s.Kind == AgentStep.Tool && s.Id == id);
            if (step is null)
            {
                step = new AgentStep { Kind = AgentStep.Tool, Id = id, Content = title, ToolKind = toolKind, Status = status ?? "pending" };
                Steps.Add(step);
            }
            else
            {
                if (title.Length > 0) step.Content = title;
                step.ToolKind = toolKind ?? step.ToolKind;
                step.Status = status ?? step.Status;
            }
            return step;
        }
    }

    public void Note(string text) { lock (Steps) Steps.Add(new AgentStep { Kind = AgentStep.Note, Content = text }); }

    /// <summary>The text the agent wrote since the last prompt (its answer).</summary>
    public string LatestAnswer()
    {
        lock (Steps)
        {
            var from = Steps.FindLastIndex(s => s.Kind == AgentStep.You) + 1;
            return string.Join("\n\n", Steps.Skip(from).Where(s => s.Kind == AgentStep.Text).Select(s => s.Content.Trim())).Trim();
        }
    }
}

/// <summary>Runs are kept as JSON files in agent-runs/ (the newest 200).</summary>
public sealed class AgentRunStore(IAppPaths paths)
{
    private const int Keep = 200;
    private string Dir => Path.Combine(paths.DataDirectory, "agent-runs");

    public async Task SaveAsync(AgentRun run)
    {
        Directory.CreateDirectory(Dir);
        AgentRun copy;
        lock (run.Steps) copy = System.Text.Json.JsonSerializer.Deserialize<AgentRun>(System.Text.Json.JsonSerializer.Serialize(run))!;
        await Json.WriteAtomicAsync(Path.Combine(Dir, run.Id + ".json"), copy);
    }

    public async Task<AgentRun?> GetAsync(string id) =>
        id.All(char.IsAsciiLetterOrDigit) ? await Json.ReadAsync<AgentRun>(Path.Combine(Dir, id + ".json")) : null;

    public async Task<List<AgentRun>> ListAsync(int max = 50)
    {
        if (!Directory.Exists(Dir)) return [];
        var files = new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var old in files.Skip(Keep)) try { old.Delete(); } catch { }
        var runs = new List<AgentRun>();
        foreach (var f in files.Take(max))
            try { if (await Json.ReadAsync<AgentRun>(f.FullName) is { } r) runs.Add(r); } catch { }
        return runs.OrderByDescending(r => r.Started).ToList();
    }
}
