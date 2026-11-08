using System.Diagnostics;
using Hearth.Core.Models;
using Hearth.Core.Tools;

namespace Hearth.Core.Services;

/// <summary>One self-check run: how many quick test questions passed and how long they took.</summary>
public sealed record SelfCheckRun(DateTimeOffset At, string Model, int Passed, int Total, double AverageSeconds, List<string> Failures);

/// <summary>
/// Nightly self-check (Settings → Daily → Self-check): asks the default model quick questions that exercise tool calls,
/// follow-ups, plain answers and Hearth's own features (people notes, plans, undo, skills, contacts), plus slower
/// research and team hand-off checks when "thorough" is on. Keeps the last 30 results and raises a problem (Activity,
/// and a phone notification when those are set up) when more fail than last time or replies got much slower.
/// Everything a check makes (chats, notes, files) is removed afterwards.
/// </summary>
public sealed class SelfCheck(ChatService chat, ConversationStore conversations, SettingsStore settingsStore, ModelPicker models,
    ActivityLog activity, IAppPaths paths, ToolRegistry tools, PeopleStore people, IBotComputer computer, BotStore bots,
    ApprovalCenter approvals) : IDisposable
{
    private string FilePath => Path.Combine(paths.DataDirectory, "selfcheck.json");
    private Timer? _timer;
    private int _busy;

    public event Action? Changed;

    /// <summary>What a check sees afterwards: the last reply, every tool call in the chat, and the chat.</summary>
    public sealed record Outcome(ChatMessage Reply, IReadOnlyList<ToolCall> Tools, Conversation Conversation, AppSettings Settings, string Workspace);

    /// <summary>A question, what it should do, and how to tell it worked.</summary>
    /// <param name="Needs">A tool that must be available, or the check is skipped (e.g. contacts not connected).</param>
    /// <param name="Thorough">Slow (web research, a team hand-off): only with "thorough" on.</param>
    /// <param name="Team">Asked in a chat with the coordinator bot (skipped when there isn't one).</param>
    public sealed record Case(string Name, string[] Prompts, Func<Outcome, bool> Check, string? Needs = null, bool Thorough = false, bool Team = false);

    private static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a, StringComparison.OrdinalIgnoreCase));
    private static bool Ran(Outcome o, string tool) => o.Tools.Any(t => t.Name == tool && t.Status == ToolCallStatus.Done);

    public const string TestPerson = "Selfcheck Testperson";
    public const string UndoFile = "self-check/undo-test.md";

    public static readonly IReadOnlyList<Case> Cases =
    [
        new("math", ["What's 1847 × 392, minus 17% of 5000? Use your calculator."], o => Ran(o, "calculate") && Has(o.Reply.Content, "723,174", "723174")),
        new("date", ["What's today's date?"], o => Ran(o, "get_current_datetime") && Has(o.Reply.Content, DateTime.Now.ToString("MMMM"), DateTime.Now.ToString("MMM"))),
        new("plain", ["Write a two-line poem about coffee. No tools needed."], o => o.Reply.ToolCalls.Count == 0 && o.Reply.Content.Length > 20),
        new("convert", ["Convert 26.2 miles to kilometres."], o => o.Reply.ToolCalls.Count > 0 && Has(o.Reply.Content, "42.1", "42.2")),
        new("follow-up", ["What's 15% of 240?", "Now double that and add 7."], o => Has(o.Reply.Content, "79")),
        new("people", [$"Note this with note_about: {TestPerson} is a recruiter at Example Corp.", $"Who is {TestPerson}? Look them up with who_is."],
            o => Ran(o, "note_about") && Ran(o, "who_is") && Has(o.Reply.Content, "Example Corp"), Needs: "note_about"),
        new("plan", ["Show me a 4-step plan for repainting a bedroom with update_plan (first step in progress), then say you're ready."],
            o => Ran(o, "update_plan") && o.Conversation.Plan.Count >= 3, Needs: "update_plan"),
        new("undo", [$"Save a file {UndoFile} containing the word hello.", "Undo that change."],
            o => Ran(o, "write_file") && Ran(o, "undo_change") && !File.Exists(Path.Combine(o.Workspace, UndoFile)), Needs: "write_file"),
        new("skill", ["Use the meal-plan skill to suggest two quick dinners. Keep it short."],
            o => o.Tools.Any(t => t.Name == "use_skill" && t.Status == ToolCallStatus.Done && t.ArgumentsJson.Contains("meal")), Needs: "use_skill"),
        new("contacts", ["Look up a contact named Mitch with find_contact and tell me whether you found one."], o => Ran(o, "find_contact"), Needs: "find_contact"),
        new("research", ["What's the latest stable .NET release and when did it come out? Cite your source."],
            o => Ran(o, "web_search") && Has(o.Reply.Content, "http", "[1]")
                 && (o.Settings.CheckAnswers == AnswerCheck.Off || o.Reply.Trace.Any(t => t.Name.StartsWith("check: "))), Needs: "web_search", Thorough: true),
        new("handoff", ["research Google and its stock for me, briefly"],
            o => o.Conversation.Messages.Any(m => m.Role == ChatRole.Assistant && m.BotId != o.Conversation.BotIds.FirstOrDefault() && m.Content.Length > 100)
                 || o.Tools.Any(t => t.Name == "message_bot" && t.Status == ToolCallStatus.Done), Thorough: true, Team: true),
    ];

    public async Task<List<SelfCheckRun>> HistoryAsync() => await Json.ReadAsync<List<SelfCheckRun>>(FilePath) ?? [];

    /// <summary>Checks every half hour whether tonight's run is due.</summary>
    public void EnsureStarted() => _timer ??= new Timer(_ => _ = AutoAsync(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(30));

    private async Task AutoAsync()
    {
        try
        {
            var s = (await settingsStore.GetAsync()).SelfCheck;
            if (!s.Enabled || !TimeOnly.TryParse(s.Time, out var t)) return;
            var now = DateTimeOffset.Now;
            var due = new DateTimeOffset(now.Date + t.ToTimeSpan(), now.Offset);
            var last = (await HistoryAsync()).LastOrDefault()?.At;
            if (now >= due && now - due < TimeSpan.FromHours(3) && (last is null || last < due)) await RunAsync();
        }
        catch { /* best effort; failures show up in the results */ }
    }

    /// <summary>Runs every case that applies now. Returns null if a run is already going.</summary>
    public async Task<SelfCheckRun?> RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        try
        {
            var model = await models.PickAsync(null) ?? throw new InvalidOperationException("No model is available.");
            var settings = await settingsStore.GetAsync();
            var available = (await tools.GetToolsAsync(settings, ct)).Select(t => t.Name).ToHashSet();
            var coordinator = (await bots.ListAsync()).FirstOrDefault(b => b.IsCoordinator);
            var cases = Cases.Where(c => (c.Needs is null || available.Contains(c.Needs)) && (!c.Thorough || settings.SelfCheck.Thorough)
                                         && (!c.Team || coordinator is not null)).ToList();
            var failures = new List<string>();
            var seconds = new List<double>();
            foreach (var c in cases)
            {
                // A behind-the-scenes chat: not in the sidebar or chat search, deleted afterwards.
                var conv = new Conversation { Title = "Self-check", HandoffFrom = "self-check" };
                if (c.Team) conv.BotIds = [coordinator!.Id];
                var sw = Stopwatch.StartNew();
                string? problem = null;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromMinutes(c.Thorough ? 10 : 5));
                    ChatMessage? reply = null;
                    foreach (var prompt in c.Prompts)
                    {
                        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt, RoutineName = c.Team ? null : "Self-check" });
                        if (c.Team)
                        {
                            // The whole team path, as from the chat box; nobody is there to approve actions, so they're declined.
                            var run = chat.GenerateAsync(conv, new SendOptions(model, false, false, true), () => Task.CompletedTask, cts.Token);
                            while (!run.IsCompleted)
                            {
                                foreach (var p in approvals.Pending.Where(p => p.ConversationId.StartsWith(conv.Id))) approvals.Decide(p.Id, false);
                                await Task.WhenAny(run, Task.Delay(500, cts.Token));
                            }
                            await run;
                            reply = conv.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
                        }
                        else
                            reply = await chat.RunAgentAsync(conv, null, new SendOptions(model, false, false, true), () => Task.CompletedTask, cts.Token,
                                autoTitle: false, unattended: true, learn: false);
                    }
                    var all = conv.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.ToolCalls).ToList();
                    if (reply is null || reply.Error is not null) problem = reply?.Error ?? "no reply";
                    else if (!c.Check(new Outcome(reply, all, conv, settings, computer.WorkspaceDirectory))) problem = $"unexpected answer: {Clip(reply.Content, 120)}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { problem = ex.Message; }
                finally { await CleanUpAsync(conv); }
                seconds.Add(sw.Elapsed.TotalSeconds);
                if (problem is not null) failures.Add($"{c.Name}: {problem}");
            }
            var run2 = new SelfCheckRun(DateTimeOffset.Now, model, cases.Count - failures.Count, cases.Count, Math.Round(seconds.DefaultIfEmpty(0).Average(), 1), failures);
            var history = await HistoryAsync();
            var alert = Worse(history, run2);
            history.Add(run2);
            await Json.WriteAtomicAsync(FilePath, history.TakeLast(30).ToList());
            var ok = alert is null;
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ok ? ActivityKind.Message : ActivityKind.Error,
                ok ? $"Self-check: {run2.Passed}/{run2.Total} passed, {run2.AverageSeconds}s average" : $"Self-check: {alert}",
                failures.Count == 0 ? null : string.Join("\n", failures), null, null, ok));
            Changed?.Invoke();
            return run2;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Removes what a check made: the chat (and its hand-offs), the test person, the test file.</summary>
    private async Task CleanUpAsync(Conversation conv)
    {
        try { await conversations.DeleteAsync(conv.Id); } catch { }
        try { foreach (var e in (await people.ListAsync()).Where(e => e.Name.Equals(TestPerson, StringComparison.OrdinalIgnoreCase))) await people.DeleteAsync(e.Id); } catch { }
        try
        {
            var file = Path.Combine(computer.WorkspaceDirectory, UndoFile);
            if (File.Exists(file)) File.Delete(file);
            var dir = Path.GetDirectoryName(file)!;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Why this run is worse than before (fewer passed, or about 50% slower than usual), or null.</summary>
    public static string? Worse(IReadOnlyList<SelfCheckRun> history, SelfCheckRun run)
    {
        var previous = history.LastOrDefault();
        if (previous is null) return run.Passed < run.Total ? $"{run.Total - run.Passed} of {run.Total} checks failed" : null;
        // Compared by failures, since the number of checks changes with what's connected.
        if (run.Total - run.Passed > previous.Total - previous.Passed) return $"{run.Passed}/{run.Total} passed, down from {previous.Passed}/{previous.Total}";
        var usual = history.TakeLast(7).Select(h => h.AverageSeconds).Order().ElementAt(Math.Min(6, history.TakeLast(7).Count()) / 2);
        if (usual > 0 && run.AverageSeconds > usual * 1.5 && run.AverageSeconds - usual > 5)
            return $"replies got slower: {run.AverageSeconds}s average, usually {usual}s";
        return null;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _timer?.Dispose();
}
