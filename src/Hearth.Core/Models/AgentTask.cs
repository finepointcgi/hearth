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

/// <summary>
/// One work cycle, for the task's timeline. <see cref="Outcome"/>: planned, worked, step (a step finished), idle,
/// waited, asked (the bot asked), stuck (the runner asked: no progress or a step dragging on), done, retry (temporary
/// problem, trying again), error (a one-off hard error), failed, interrupted, or restarted (retry from a step).
/// </summary>
public sealed record TaskCycle(int Number, DateTimeOffset At, long DurationMs, int? Step, string Outcome, string? Detail, int Tools);

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
    /// <summary>While waiting: what counts as "it happened" (task_wait's done_when), checked first on waking.</summary>
    public string? WaitCondition { get; set; }
    /// <summary>Woken from a wait and not yet checked whether what it waited for happened.</summary>
    public bool JustWoke { get; set; }
    /// <summary>Waiting only to retry after a temporary problem (model server down, timeout), not on the world.</summary>
    public bool Retrying { get; set; }

    /// <summary>Temporary problems in a row (retried with backoff), or the same error in a row (for hard ones).</summary>
    public int Retries { get; set; }
    /// <summary>The last problem: a model or tool error. Shown when the task stops so it can be resumed cleanly.</summary>
    public string? LastError { get; set; }
    /// <summary>Work cycles since a step last finished (done, skipped or failed).</summary>
    public int StepCycles { get; set; }
    /// <summary>task_plan calls since a step last finished: caps re-planning loops.</summary>
    public int PlansInARow { get; set; }
    /// <summary>What to do when one step takes <see cref="MaxStepCycles"/> cycles: "ask" the user, or "skip" it (mark failed, move on).</summary>
    public string OnStuckStep { get; set; } = "ask";
    /// <summary>Short findings the bot keeps (task_note): shown every cycle, so they survive the chat window and wake-ups.</summary>
    public List<string> Scratchpad { get; set; } = [];
    /// <summary>What happened in each work cycle (newest <see cref="MaxTimeline"/>), for the task page.</summary>
    public List<TaskCycle> Timeline { get; set; } = [];
    public const int MaxTimeline = 200;
    /// <summary>A one-off instruction for the next cycle's prompt (e.g. "the user asked you to redo from step 3").</summary>
    public string? Nudge { get; set; }

    public void Record(TaskCycle cycle)
    {
        Timeline.Add(cycle);
        if (Timeline.Count > MaxTimeline) Timeline.RemoveRange(0, Timeline.Count - MaxTimeline);
    }

    public const int MaxRetries = 3;
    public const int MaxStepCycles = 4;
    public const int MaxPlansInARow = 3;
    public const int MaxScratchpad = 10;

    /// <summary>The step being worked on: the first one not finished.</summary>
    public (int Number, TaskStep Step)? CurrentStep =>
        Plan.Select((s, i) => (i + 1, s)).FirstOrDefault(x => x.s.Status is "todo" or "working") is var (n, step) && step is not null ? (n, step) : null;

    public int FinishedSteps => Plan.Count(s => s.Status is "done" or "skipped" or "failed");
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
