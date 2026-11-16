using System.Text;
using Hearth.Core.Models;
using Hearth.Core.Tools;

namespace Hearth.Core.Services;

/// <summary>
/// Works through long-running tasks in the background, one cycle at a time: each cycle the bot sees the goal,
/// its plan with progress, and recent history, uses tools to advance, and reports with task_step / task_ask /
/// task_done. State is saved after every cycle, so tasks pick up again after a restart. A task can also set itself aside
/// with task_wait (waiting for replies, a date, a change) and wakes up on its own, so one goal can run for days.
/// </summary>
public sealed class TaskRunner(TaskStore store, ConversationStore conversations, ChatService chat, BotStore bots,
    ModelPicker models, ActivityLog activity, ChatRuns runs)
{
    private const int HistoryWindow = 24;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private bool _started;

    /// <summary>(task, message) for notable moments: plan made, step done, question, finished, failed.</summary>
    public event Action<AgentTask, string>? Milestone;

    public string? CurrentTaskId { get; private set; }

    /// <summary>Raises a milestone and, for a task a chat handed off, posts the notable ones (plan, question,
    /// result, failure — not every step) back into that chat.</summary>
    private void Announce(AgentTask task, string message)
    {
        Milestone?.Invoke(task, message);
        if (task.ReportTo is not { } convId || message.StartsWith("📍")) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // A chat that's mid-reply is updated live (its own save would otherwise overwrite this).
                var live = runs.Live(convId);
                if ((live ?? await conversations.GetAsync(convId)) is not { } conv) return;
                conv.Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant, BotId = task.BotId,
                    Content = message + (task.State == TaskState.WaitingForYou ? $"\n\n[Answer on the task page](tasks/{task.Id})" : ""),
                });
                conv.Unread = true;
                conv.UpdatedAt = DateTimeOffset.Now;
                if (live is null) await conversations.SaveAsync(conv);
            }
            catch { /* best effort: the task page has everything */ }
        });
    }

    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(LoopAsync);
    }

    public async Task<AgentTask> StartAsync(string goal, string? botId, string? origin = "app", string? reportTo = null)
    {
        var bot = botId is null ? null : await bots.GetAsync(botId);
        var conv = new Conversation { Title = "🎯 " + (goal.Length > 50 ? goal[..50] + "…" : goal), BotIds = bot is null ? [] : [bot.Id] };
        await conversations.SaveAsync(conv);
        var task = new AgentTask { Goal = goal.Trim(), BotId = bot?.Id, ConversationId = conv.Id, Origin = origin, ReportTo = reportTo };
        task.Note($"Started{(bot is null ? "" : $" by {bot.Name}")}.");
        await store.SaveAsync(task);
        await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Task, $"Task started: {task.Title}", null, bot?.Id, conv.Id));
        EnsureStarted();
        _wake.Release();
        return task;
    }

    public async Task AnswerAsync(string taskId, string answer)
    {
        var task = await store.GetAsync(taskId) ?? throw new InvalidOperationException("No such task.");
        var conv = await conversations.GetAsync(task.ConversationId);
        if (conv is not null)
        {
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = $"Answer to \"{task.Question}\": {answer}" });
            await conversations.SaveAsync(conv);
        }
        task.Note($"You answered: {answer}");
        task.Question = null;
        task.IdleCycles = 0;
        task.StepCycles = 0;
        task.PlansInARow = 0;
        task.Retries = 0;
        if (task.Cycles >= task.MaxCycles) task.MaxCycles = task.Cycles + 15;
        task.State = TaskState.Running;
        await store.SaveAsync(task);
        _wake.Release();
    }

    public async Task SetStateAsync(string taskId, TaskState state)
    {
        var task = await store.GetAsync(taskId);
        if (task is null) return;
        if (state is TaskState.Paused or TaskState.Cancelled && _running.TryGetValue(taskId, out var cts)) cts.Cancel();
        task.State = state;
        task.Note(state switch { TaskState.Paused => "Paused.", TaskState.Cancelled => "Cancelled.", TaskState.Running => "Resumed.", _ => state.ToString() });
        if (state == TaskState.Running && task.Cycles >= task.MaxCycles) task.MaxCycles = task.Cycles + 15;
        await store.SaveAsync(task);
        if (state == TaskState.Running) _wake.Release();
    }

    /// <summary>
    /// Starts a stopped task again from a step: that step and the ones after it go back to to-do, finished steps and
    /// notes stay, and the error, question and counters are cleared. The bot is told once that this was asked for.
    /// </summary>
    public async Task RetryFromStepAsync(string taskId, int step)
    {
        var task = await store.GetAsync(taskId) ?? throw new InvalidOperationException("No such task.");
        if (task.State == TaskState.Running) throw new InvalidOperationException("It's working right now. Pause it first.");
        if (step < 1 || step > task.Plan.Count) throw new InvalidOperationException($"There is no step {step}.");
        foreach (var s in task.Plan.Skip(step - 1)) { s.Status = "todo"; s.Note = null; }
        task.IdleCycles = task.StepCycles = task.PlansInARow = task.Retries = 0;
        task.LastError = task.Question = task.WaitReason = task.WaitCondition = null;
        task.WakeAt = null;
        task.JustWoke = task.Retrying = false;
        task.Result = null;
        task.MaxCycles = Math.Max(task.MaxCycles, task.Cycles + 15);
        var text = task.Plan[step - 1].Text;
        task.Nudge = $"The user asked you to redo the task from step {step} ({text}). Steps before it are finished; start step {step} " +
                     "fresh rather than relying on what you found last time.";
        task.Note($"Retrying from step {step}: {text}");
        task.Record(new TaskCycle(task.Cycles, DateTimeOffset.Now, 0, step, "restarted", $"From step {step}: {text}", 0));
        task.State = TaskState.Running;
        await store.SaveAsync(task);
        EnsureStarted();
        _wake.Release();
    }

    /// <summary>
    /// Wakes the set-aside tasks whose time has come: they go back to work with a fresh, small budget of cycles.
    /// Returns how many woke.
    /// </summary>
    public async Task<int> WakeDueAsync(DateTimeOffset now)
    {
        var woke = 0;
        foreach (var t in (await store.ListAsync()).Where(t => t.State == TaskState.Scheduled && (t.WakeAt is null || t.WakeAt <= now)))
        {
            t.State = TaskState.Running;
            t.Wakes++;
            t.IdleCycles = 0;
            t.MaxCycles = Math.Min(AgentTask.HardCycleLimit, Math.Max(t.MaxCycles, t.Cycles + AgentTask.CyclesPerWake));
            t.Note(t.Retrying ? "Trying again." : $"Picked up again: {t.WaitReason}");
            t.JustWoke = !t.Retrying;
            t.Retrying = false;
            t.WakeAt = null;
            await store.SaveAsync(t);
            woke++;
        }
        if (woke > 0 && _started) _wake.Release();
        return woke;
    }

    /// <summary>Wakes a set-aside task now instead of at its time.</summary>
    public async Task WakeNowAsync(string taskId)
    {
        if (await store.GetAsync(taskId) is not { State: TaskState.Scheduled } task) return;
        task.WakeAt = DateTimeOffset.Now;
        await store.SaveAsync(task);
        EnsureStarted();
        await WakeDueAsync(DateTimeOffset.Now);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            try { await WakeDueAsync(DateTimeOffset.Now); } catch { /* checked again next time round */ }
            var next = (await store.ListAsync()).Where(t => t.State == TaskState.Running).OrderBy(t => t.UpdatedAt).FirstOrDefault();
            if (next is null)
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(1));
                continue;
            }
            try { await CycleAsync(next); }
            catch (Exception ex)
            {
                var gone = ex is TaskGoneException;
                await ProblemAsync(next, ex.Message, temporary: !gone && IsTemporary(ex), hard: gone, cycle: (DateTimeOffset.Now, 0));
            }
        }
    }

    /// <summary>
    /// What this cycle needs from automatic model choice: planning, a problem to get past and a step that's dragging
    /// go to the strong model; a wake check (is it time to carry on?) to the fast one; the rest decides as usual.
    /// </summary>
    public static RouteHint? RouteFor(AgentTask task) =>
        task.Plan.Count == 0 ? new(true, "planning the task")
        : task.LastError is not null ? new(true, "getting past a problem")
        : task.StepCycles >= 2 ? new(true, "a step is taking a while")
        : task.JustWoke ? new(false, "wake check")
        : null;

    private async Task CycleAsync(AgentTask task)
    {
        var conv = await conversations.GetAsync(task.ConversationId)
                   ?? throw new TaskGoneException("The task's work chat was deleted.");
        var bot = task.BotId is null ? null : await bots.GetAsync(task.BotId);
        var model = await models.PickAsync(bot) ?? throw new TemporaryProblemException("No model is available (is the model server running?).");

        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = Prompt(task), RoutineName = "Task", Route = RouteFor(task) });
        var cycleLimit = TimeSpan.FromMinutes(30);
        using var cts = new CancellationTokenSource(cycleLimit);
        var startedAt = DateTimeOffset.Now;
        _running[task.Id] = cts;
        CurrentTaskId = task.Id;
        var planBefore = task.Plan.Count;
        var finishedBefore = task.FinishedSteps;
        var woke = task.JustWoke;
        var stepAtStart = task.CurrentStep?.Number;
        var stateBefore = task.State;
        task.Nudge = null; // shown in this cycle's prompt
        ChatMessage? reply;
        try
        {
            reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true), () => Task.CompletedTask, cts.Token,
                autoTitle: false, extraTools: TaskTools.For(task), historyWindow: HistoryWindow);
        }
        finally
        {
            _running.Remove(task.Id);
            CurrentTaskId = null;
        }

        // Someone may have paused/cancelled while this cycle ran.
        var latest = await store.GetAsync(task.Id);
        if (latest is { State: TaskState.Paused or TaskState.Cancelled }) { latest.Cycles = task.Cycles + 1; await store.SaveAsync(latest); return; }
        if (latest is not null) task.OnStuckStep = latest.OnStuckStep; // changed on the task page meanwhile

        task.Cycles++;
        task.JustWoke = false;
        if (cts.IsCancellationRequested && reply?.Error is null)
        {
            if (DateTimeOffset.Now - startedAt >= cycleLimit - TimeSpan.FromSeconds(5))
            {
                await ProblemAsync(task, "A work cycle took longer than 30 minutes and was stopped.", temporary: true, hard: false, cycle: (startedAt, ToolCount(reply)));
                return;
            }
            // Paused (and maybe resumed) mid-cycle: not a problem, just pick up from here.
            task.Note("Interrupted mid-cycle.");
            task.Record(new TaskCycle(task.Cycles, startedAt, Ms(startedAt), stepAtStart, "interrupted", "Paused mid-cycle", ToolCount(reply)));
            await store.SaveAsync(task);
            return;
        }
        if (reply?.Error is { } error)
        {
            await ProblemAsync(task, error, temporary: IsTemporary(error), hard: false, cycle: (startedAt, ToolCount(reply)));
            return;
        }
        task.Retries = 0;
        task.LastError = LastToolError(reply);
        if (task.FinishedSteps > finishedBefore) task.PlansInARow = 0;
        var idleBefore = task.IdleCycles;
        var askedByBot = task.State == TaskState.WaitingForYou; // task_ask, before the runner judges
        var firstPlan = planBefore == 0 && task.Plan.Count > 0;
        if (task.State == TaskState.Running) Judge(task, reply, finishedBefore, firstPlan, woke);
        // Waiting on the world isn't being stuck.
        if (task.State == TaskState.Scheduled) task.IdleCycles = 0;
        task.Record(Summarise(task, reply, startedAt, stepAtStart, finishedBefore, firstPlan, idleBefore, askedByBot));
        var news = task.News;
        task.News = null;
        await store.SaveAsync(task);

        // Notifications: the plan, news the bot flagged for the user, questions and the result. Routine progress
        // and waiting stay on the task page.
        if (planBefore == 0 && task.Plan.Count > 0)
            Announce(task, $"🗺️ Plan for \"{task.Title}\":\n" + string.Join("\n", task.Plan.Select((s, i) => $"{i + 1}. {s.Text}")));
        else if (news is not null && task.State is TaskState.Running or TaskState.Scheduled)
            Announce(task, $"📍 {task.Title}: {news}");
        if (task.State == TaskState.WaitingForYou)
            Announce(task, $"❓ {task.Title}\n{task.Question}");
        if (task.State == TaskState.Done)
        {
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Task, $"Task done: {task.Title}", Clip(task.Result, 200), task.BotId, task.ConversationId));
            Announce(task, $"🏁 Done: {task.Title}\n\n{task.Result}");
        }
    }

    /// <summary>
    /// Whether the cycle moved the task forward, and what to do if not. Progress is a step finishing (done, skipped or
    /// failed), the bot waiting, asking or finishing (handled before this), or a tool that changed something (sent,
    /// wrote, created…). Talking, reading and re-planning alone aren't progress.
    /// </summary>
    private static void Judge(AgentTask task, ChatMessage? reply, int finishedBefore, bool firstPlan, bool woke)
    {
        var calls = reply?.ToolCalls ?? [];
        var stepFinished = task.FinishedSteps > finishedBefore;
        var changedSomething = calls.Any(c => c.Status == ToolCallStatus.Done && !c.Name.StartsWith("task_") && (c.Undoable || ActsOnTheWorld(c.Name)));
        var usedTools = calls.Any(c => !c.Name.StartsWith("task_") && c.Status == ToolCallStatus.Done);
        if (reply?.Content is { Length: > 0 } said) task.Note(said.Length > 300 ? said[..300] + "…" : said);

        if (firstPlan) task.IdleCycles = 0; // making the first plan is getting started
        else if (stepFinished || changedSomething)
        {
            task.IdleCycles = 0;
            if (stepFinished) task.StepCycles = 0; else task.StepCycles++;
        }
        else
        {
            task.StepCycles++;
            // Only talking or re-planning: idle. Looking things up counts as working on the step (the step budget limits it).
            // After waking, reporting on the step (task_step) is the check it was asked for; anything else is idle.
            if (woke ? calls.All(c => c.Name != "task_step") : !usedTools) task.IdleCycles++;
        }

        var current = task.CurrentStep;
        if (task.IdleCycles >= 2)
        {
            task.State = TaskState.WaitingForYou;
            task.Question = Explain(task, "I'm not making progress.",
                current is { } c ? $"Tell me how to get past step {c.Number}, or say \"skip it\"." : "Tell me how you'd like me to continue.");
        }
        else if (task.StepCycles >= AgentTask.MaxStepCycles && current is { } stuck)
        {
            if (task.OnStuckStep == "skip")
            {
                stuck.Step.Status = "failed";
                stuck.Step.Note = $"Gave up after {task.StepCycles} work cycles" + (stuck.Step.Note is { } n ? $" ({n})" : "");
                task.Note($"Step {stuck.Number} skipped after {task.StepCycles} cycles: {stuck.Step.Text}");
                task.StepCycles = 0;
            }
            else
            {
                task.State = TaskState.WaitingForYou;
                task.Question = Explain(task, $"Step {stuck.Number} has taken {task.StepCycles} work cycles without finishing.",
                    "Should I keep at it, skip it, or try another way?");
            }
        }
        else if (task.Cycles >= task.MaxCycles)
        {
            task.State = TaskState.WaitingForYou;
            task.Question = Explain(task, $"I've used my budget of {task.MaxCycles} work cycles ({task.DoneSteps}/{task.Plan.Count} steps done).", "Keep going?");
        }
    }

    /// <summary>
    /// A problem with a cycle. Temporary ones (model server down, timeouts, rate limits) are retried after 1, 5 and
    /// 15 minutes and then put to the user; the same hard error three times in a row stops the task; a deleted work
    /// chat stops it at once. Every stop says what went wrong, where, and what to do next.
    /// </summary>
    private async Task ProblemAsync(AgentTask task, string message, bool temporary, bool hard, (DateTimeOffset StartedAt, int Tools) cycle)
    {
        void Log(string outcome) => task.Record(new TaskCycle(task.Cycles, cycle.StartedAt, Ms(cycle.StartedAt), task.CurrentStep?.Number,
            outcome, Clip(message, 200), cycle.Tools));
        var same = task.LastError == message;
        task.LastError = message;
        task.JustWoke = false;
        if (hard)
        {
            task.State = TaskState.Failed;
            task.Note($"Stopped: {message}");
            task.Question = null;
            Log("failed");
            await store.SaveAsync(task);
            Announce(task, "❌ " + Explain(task, $"Task stopped: {task.Title}", "Start it again from the goal if you still need it."));
            return;
        }
        task.Retries = temporary || same ? task.Retries + 1 : 1;
        if (temporary && task.Retries <= AgentTask.MaxRetries)
        {
            var wait = TimeSpan.FromMinutes(task.Retries switch { 1 => 1, 2 => 5, _ => 15 });
            task.State = TaskState.Scheduled;
            task.Retrying = true;
            task.WakeAt = DateTimeOffset.Now + wait;
            task.WaitReason = "a temporary problem to clear";
            task.WaitCondition = null;
            task.Note($"Problem (try {task.Retries} of {AgentTask.MaxRetries}, again in {wait.TotalMinutes:0} min): {message}");
            Log("retry");
            await store.SaveAsync(task);
            return; // quiet: the task page shows it
        }
        task.Note($"Problem: {message}");
        if (temporary)
        {
            task.State = TaskState.WaitingForYou;
            task.Question = Explain(task, $"I couldn't work for a while: the same problem came back {AgentTask.MaxRetries} times.",
                "Check the model server (Settings → Models), then answer here to continue.");
        }
        else if (task.Retries >= AgentTask.MaxRetries)
        {
            task.State = TaskState.Failed;
            task.Question = null;
            Log("failed");
            await store.SaveAsync(task);
            Announce(task, "❌ " + Explain(task, $"Task stopped: the same error {AgentTask.MaxRetries} times in a row.",
                "Fix what the error points to, then press Resume on the task page."));
            return;
        }
        // A one-off hard error: the next cycle tries again.
        Log(task.State == TaskState.WaitingForYou ? "stuck" : "error");
        await store.SaveAsync(task);
        if (task.State == TaskState.WaitingForYou) Announce(task, $"❓ {task.Title}\n{task.Question}");
    }

    /// <summary>What a finished cycle did, in one line for the timeline.</summary>
    private static TaskCycle Summarise(AgentTask task, ChatMessage? reply, DateTimeOffset startedAt, int? step, int finishedBefore,
        bool firstPlan, int idleBefore, bool askedByBot)
    {
        var tools = (reply?.ToolCalls ?? []).Where(c => !c.Name.StartsWith("task_")).Select(c => c.Name).Distinct().ToList();
        var used = tools.Count == 0 ? null : string.Join(", ", tools.Take(3)) + (tools.Count > 3 ? $" +{tools.Count - 3}" : "");
        var (outcome, detail) = task.State switch
        {
            TaskState.Done => ("done", "Finished"),
            TaskState.Scheduled => ("waited", $"Until {task.WakeAt:ddd MMM d h:mm tt}: {task.WaitReason}"),
            TaskState.WaitingForYou => (askedByBot ? "asked" : "stuck", Clip(task.Question?.Split('\n')[0], 160)),
            _ when task.FinishedSteps > finishedBefore =>
                ("step", task.Plan.LastOrDefault(p => p.Status is "done" or "skipped" or "failed") is { } last
                    ? $"Step {task.Plan.IndexOf(last) + 1} {last.Status}: {Clip(last.Text, 120)}" : "A step finished"),
            _ when firstPlan => ("planned", $"{task.Plan.Count} steps"),
            _ when task.IdleCycles > idleBefore => ("idle", used is null ? "No tools, no finished step" : $"Only {used}"),
            _ => ("worked", used),
        };
        return new TaskCycle(task.Cycles, startedAt, Ms(startedAt), step, outcome, detail, reply?.ToolCalls.Count(c => !c.Name.StartsWith("task_")) ?? 0);
    }

    private static long Ms(DateTimeOffset since) => (long)(DateTimeOffset.Now - since).TotalMilliseconds;
    private static int ToolCount(ChatMessage? reply) => reply?.ToolCalls.Count(c => !c.Name.StartsWith("task_")) ?? 0;

    /// <summary>A message the user (or another bot) can act on: what happened, the step, the last error, what to do.</summary>
    public static string Explain(AgentTask task, string what, string next)
    {
        var sb = new StringBuilder(what);
        if (task.CurrentStep is { } c) sb.Append($"\nStep {c.Number} of {task.Plan.Count}: {c.Step.Text}");
        if (task.LastError is { Length: > 0 } e) sb.Append($"\nLast error: {Clip(e, 300)}");
        sb.Append($"\nNext: {next}");
        return sb.ToString();
    }

    /// <summary>Problems worth retrying: the model server down or busy, the network, timeouts.</summary>
    public static bool IsTemporary(Exception ex) =>
        ex is TemporaryProblemException or HttpRequestException or TimeoutException or TaskCanceledException || IsTemporary(ex.Message);

    public static bool IsTemporary(string message) =>
        new[] { "timed out", "timeout", "connection refused", "actively refused", "no connection", "unreachable", "no model is available",
                "rate limit", "too many requests", "overloaded", "429", "502", "503", "504", "bad gateway", "service unavailable",
                "server error", "connection reset", "name or service not known", "no such host" }
            .Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tool names that change something rather than look something up.</summary>
    private static bool ActsOnTheWorld(string tool) =>
        new[] { "send", "write", "create", "draft", "edit", "delete", "save", "update", "post", "move", "add", "upload", "book", "schedule", "reply", "forward", "rename", "commit" }
            .Any(v => tool.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static string? LastToolError(ChatMessage? reply) =>
        reply?.ToolCalls.LastOrDefault(c => c.Status == ToolCallStatus.Error && !c.Name.StartsWith("task_")) is { } bad
            ? $"{bad.Name}: {Clip(bad.Result, 200)}" : null;

    private static string Prompt(AgentTask task)
    {
        if (task.Cycles == 0)
            return $"TASK: {task.Goal}\n\n" +
                   "You're doing this as a long-running task that may take many steps. First call task_plan with 3–8 concrete steps. " +
                   "Then start on step 1 using your tools. After each step call task_step. If you need a decision or information " +
                   "only the user has, call task_ask and stop. If the next step depends on time passing (replies, prices, a date), " +
                   "call task_wait and stop: the task can run for days or weeks. Keep key findings with task_note. When everything is done, call task_done with a " +
                   "clear final report. Keep chat text short — do the work with tools.";
        var sb = new StringBuilder($"Continue the task.\nGoal: {task.Goal}\n");
        if (task.Nudge is { Length: > 0 } nudge) sb.Append($"\n{nudge}\n");
        if (task.JustWoke)
            sb.Append($"\nIt's now {DateTimeOffset.Now:dddd MMM d, h:mm tt}. You were waiting for: {task.WaitReason}." +
                      (task.WaitCondition is { Length: > 0 } cond ? $" It counts as happened when: {cond}." : "") +
                      " First check whether that happened. If it did, call task_step on the current step with a note saying what you " +
                      "found, then carry on. If not, call task_wait again or task_ask the user. Do not invent progress.\n");
        else if (task.IdleCycles > 0 && task.CurrentStep is { } c)
            sb.Append($"\nThe last cycle didn't move the task forward. Which part of step {c.Number} are you blocked on? Finish it " +
                      "(task_step done), skip it (task_step skipped, with why), or task_ask the user. Don't re-plan.\n");
        if (task.LastError is { Length: > 0 } err) sb.Append($"\nLast error: {Clip(err, 300)}\n");
        if (task.Scratchpad.Count > 0) sb.Append("\nYour notes:\n" + string.Join("\n", task.Scratchpad.Select(n => "- " + n)) + "\n");
        sb.Append("\nPlan:\n");
        for (var i = 0; i < task.Plan.Count; i++)
            sb.AppendLine($"{i + 1}. [{task.Plan[i].Status}] {task.Plan[i].Text}{(task.Plan[i].Note is { } n ? $" — {n}" : "")}");
        if (task.Plan.Count == 0) sb.AppendLine("(no plan yet — call task_plan first)");
        sb.Append("\nWork on the next unfinished step now. Call task_step when it's done (tell_user only for news the user would want now), " +
                  "task_note to keep a finding, task_ask if you're blocked on the user, task_wait if the next step needs time to pass, " +
                  "task_done when finished.");
        return sb.ToString();
    }

    private static string? Clip(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n] + "…";
}

/// <summary>The task can't go on at all (its work chat was deleted).</summary>
public sealed class TaskGoneException(string message) : Exception(message);

/// <summary>A problem worth retrying later (no model available right now).</summary>
public sealed class TemporaryProblemException(string message) : Exception(message);
