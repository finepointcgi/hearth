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
            t.Note($"Picked up again: {t.WaitReason}");
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
                next.State = TaskState.Failed;
                next.Note($"Failed: {ex.Message}");
                await store.SaveAsync(next);
                Announce(next, $"❌ Task failed: {next.Title}\n{ex.Message}");
            }
        }
    }

    private async Task CycleAsync(AgentTask task)
    {
        var conv = await conversations.GetAsync(task.ConversationId)
                   ?? throw new InvalidOperationException("The task's conversation was deleted.");
        var bot = task.BotId is null ? null : await bots.GetAsync(task.BotId);
        var model = await models.PickAsync(bot) ?? throw new InvalidOperationException("No model is available.");

        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = Prompt(task), RoutineName = "Task" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _running[task.Id] = cts;
        CurrentTaskId = task.Id;
        var planBefore = task.Plan.Count;
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

        task.Cycles++;
        if (reply?.Error is not null)
        {
            task.Note($"Problem: {reply.Error}");
            task.IdleCycles++;
            if (task.IdleCycles >= 3) { task.State = TaskState.Failed; Announce(task, $"❌ Task failed: {task.Title}\n{reply.Error}"); }
        }
        else if (task.State == TaskState.Running)
        {
            var progressed = reply?.ToolCalls.Any(t => !t.Name.StartsWith("task_") || t.Name is "task_plan" or "task_step") == true;
            task.IdleCycles = progressed ? 0 : task.IdleCycles + 1;
            if (reply?.Content is { Length: > 0 } said) task.Note(said.Length > 300 ? said[..300] + "…" : said);
            if (task.IdleCycles >= 2)
            {
                task.State = TaskState.WaitingForYou;
                task.Question = reply?.Content is { Length: > 0 } c ? $"I'm not making progress. Last thing I said: {c}" : "I'm stuck — how should I continue?";
            }
            else if (task.Cycles >= task.MaxCycles)
            {
                task.State = TaskState.WaitingForYou;
                task.Question = $"I've used my budget of {task.MaxCycles} work cycles ({task.DoneSteps}/{task.Plan.Count} steps done). Keep going?";
            }
        }
        // Waiting on the world isn't being stuck.
        if (task.State == TaskState.Scheduled) task.IdleCycles = 0;
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

    private static string Prompt(AgentTask task)
    {
        if (task.Cycles == 0)
            return $"TASK: {task.Goal}\n\n" +
                   "You're doing this as a long-running task that may take many steps. First call task_plan with 3–8 concrete steps. " +
                   "Then start on step 1 using your tools. After each step call task_step. If you need a decision or information " +
                   "only the user has, call task_ask and stop. If the next step depends on time passing (replies, prices, a date), " +
                   "call task_wait and stop: the task can run for days or weeks. When everything is done, call task_done with a " +
                   "clear final report. Keep chat text short — do the work with tools.";
        var sb = new StringBuilder($"Continue the task.\nGoal: {task.Goal}\n");
        if (task.Log.LastOrDefault()?.Text is { } last && last.StartsWith("Picked up again: "))
            sb.Append($"It's now {DateTimeOffset.Now:dddd MMM d, h:mm tt}. You set this aside, waiting for: {last["Picked up again: ".Length..]}. " +
                      "Check whether it happened before going on.\n");
        sb.Append("\nPlan:\n");
        for (var i = 0; i < task.Plan.Count; i++)
            sb.AppendLine($"{i + 1}. [{task.Plan[i].Status}] {task.Plan[i].Text}{(task.Plan[i].Note is { } n ? $" — {n}" : "")}");
        if (task.Plan.Count == 0) sb.AppendLine("(no plan yet — call task_plan first)");
        sb.Append("\nWork on the next unfinished step now. Call task_step when it's done (tell_user only for news the user would want now), " +
                  "task_ask if you're blocked on the user, task_wait if the next step needs time to pass, task_done when finished.");
        return sb.ToString();
    }

    private static string? Clip(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n] + "…";
}
