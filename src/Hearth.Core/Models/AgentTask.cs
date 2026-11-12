namespace Hearth.Core.Models;

/// <summary>
/// Where a task is. <see cref="Scheduled"/>: waiting on the world rather than the user (a reply, a price, a date) and
/// picks itself up again at <see cref="AgentTask.WakeAt"/>. New values go at the end: states are saved by name, but
/// keep the order stable anyway.
/// </summary>
public enum TaskState { Running, WaitingForYou, Paused, Done, Failed, Cancelled, Scheduled }

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
    /// <summary>Work cycles before it checks in with the user. Each wake-up adds a little more, up to <see cref="HardCycleLimit"/>.</summary>
    public int MaxCycles { get; set; } = 30;
    /// <summary>A task that spans days still stops for a "keep going?" here, however often it wakes up.</summary>
    public const int HardCycleLimit = 200;
    /// <summary>Cycles each wake-up may use, so a goal that runs for weeks can't burn through its budget early.</summary>
    public const int CyclesPerWake = 10;
    /// <summary>While <see cref="TaskState.Scheduled"/>: when to pick the work up again, and why it's waiting.</summary>
    public DateTimeOffset? WakeAt { get; set; }
    public string? WaitReason { get; set; }
    public int Wakes { get; set; }
    /// <summary>Something the bot said the user should hear now (task_step with tell_user); sent once, then cleared.</summary>
    public string? News { get; set; }
    public int IdleCycles { get; set; }
    /// <summary>Who started it, for notifications ("app", "telegram:123"…).</summary>
    public string? Origin { get; set; }
    /// <summary>The chat that handed this task off (start_task): its plan, questions and result are posted there.</summary>
    public string? ReportTo { get; set; }
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
