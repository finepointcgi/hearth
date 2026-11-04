namespace Locus.Core.Models;

public enum TaskState { Running, WaitingForYou, Paused, Done, Failed, Cancelled }

public sealed class TaskStep
{
    public string Text { get; set; } = "";
    /// <summary>todo | working | done | skipped | failed</summary>
    public string Status { get; set; } = "todo";
    public string? Note { get; set; }
}

public sealed record TaskEvent(DateTimeOffset Time, string Text);

/// <summary>A long-running job a bot works through on its own: plan → steps → final report.</summary>
public sealed class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Goal { get; set; } = "";
    public string? BotId { get; set; }
    public TaskState State { get; set; } = TaskState.Running;
    public List<TaskStep> Plan { get; set; } = [];
    public List<TaskEvent> Log { get; set; } = [];
    /// <summary>The chat where the work happens (open it to see every tool call).</summary>
    public string ConversationId { get; set; } = "";
    public string? Question { get; set; }
    public string? Result { get; set; }
    public int Cycles { get; set; }
    public int MaxCycles { get; set; } = 30;
    public int IdleCycles { get; set; }
    /// <summary>Who started it, for notifications ("app", "telegram:123"…).</summary>
    public string? Origin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string Title => Goal.Length <= 70 ? Goal : Goal[..70].TrimEnd() + "…";
    public int DoneSteps => Plan.Count(s => s.Status is "done" or "skipped");

    public void Note(string text)
    {
        Log.Add(new TaskEvent(DateTimeOffset.Now, text));
        if (Log.Count > 300) Log.RemoveRange(0, Log.Count - 300);
        UpdatedAt = DateTimeOffset.Now;
    }
}
