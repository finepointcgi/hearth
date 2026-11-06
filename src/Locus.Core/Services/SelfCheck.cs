using System.Diagnostics;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>One self-check run: how many quick test questions passed and how long they took.</summary>
public sealed record SelfCheckRun(DateTimeOffset At, string Model, int Passed, int Total, double AverageSeconds, List<string> Failures);

/// <summary>
/// Nightly self-check (Settings → Self-check): asks the default model a few quick questions that exercise tool calls,
/// multi-turn follow-ups and plain answers, keeps the last 30 results, and raises a problem (Activity, and a phone
/// notification when those are set up) when fewer pass than last time or replies got much slower.
/// </summary>
public sealed class SelfCheck(ChatService chat, ConversationStore conversations, SettingsStore settingsStore, ModelPicker models,
    ActivityLog activity, IAppPaths paths) : IDisposable
{
    private string FilePath => Path.Combine(paths.DataDirectory, "selfcheck.json");
    private Timer? _timer;
    private int _busy;

    public event Action? Changed;

    /// <summary>A question, what it should do, and how to tell it worked.</summary>
    public sealed record Case(string Name, string[] Prompts, Func<ChatMessage, IReadOnlyList<ToolCall>, bool> Check);

    private static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a, StringComparison.OrdinalIgnoreCase));

    public static readonly IReadOnlyList<Case> Cases =
    [
        new("math", ["What's 1847 × 392, minus 17% of 5000? Use your calculator."], (r, t) => t.Any(x => x.Name == "calculate") && Has(r.Content, "723,174", "723174")),
        new("date", ["What's today's date?"], (r, t) => t.Any(x => x.Name == "get_current_datetime") && Has(r.Content, DateTime.Now.ToString("MMMM"), DateTime.Now.ToString("MMM"))),
        new("plain", ["Write a two-line poem about coffee. No tools needed."], (r, t) => t.Count == 0 && r.Content.Length > 20),
        new("convert", ["Convert 26.2 miles to kilometres."], (r, t) => t.Count > 0 && Has(r.Content, "42.1", "42.2")),
        new("follow-up", ["What's 15% of 240?", "Now double that and add 7."], (r, _) => Has(r.Content, "79")),
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

    /// <summary>Runs every case now. Returns null if a run is already going.</summary>
    public async Task<SelfCheckRun?> RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        try
        {
            var model = await models.PickAsync(null) ?? throw new InvalidOperationException("No model is available.");
            var failures = new List<string>();
            var seconds = new List<double>();
            foreach (var c in Cases)
            {
                // A behind-the-scenes chat: not in the sidebar or chat search, deleted afterwards.
                var conv = new Conversation { Title = "Self-check", HandoffFrom = "self-check" };
                var sw = Stopwatch.StartNew();
                string? problem = null;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    ChatMessage? reply = null;
                    foreach (var prompt in c.Prompts)
                    {
                        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt, RoutineName = "Self-check" });
                        reply = await chat.RunAgentAsync(conv, null, new SendOptions(model, false, false, true), () => Task.CompletedTask, cts.Token,
                            autoTitle: false, unattended: true, learn: false);
                    }
                    if (reply is null || reply.Error is not null) problem = reply?.Error ?? "no reply";
                    else if (!c.Check(reply, reply.ToolCalls)) problem = $"unexpected answer: {Clip(reply.Content, 120)}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { problem = ex.Message; }
                finally { await conversations.DeleteAsync(conv.Id); }
                seconds.Add(sw.Elapsed.TotalSeconds);
                if (problem is not null) failures.Add($"{c.Name}: {problem}");
            }
            var run = new SelfCheckRun(DateTimeOffset.Now, model, Cases.Count - failures.Count, Cases.Count, Math.Round(seconds.Average(), 1), failures);
            var history = await HistoryAsync();
            var alert = Worse(history, run);
            history.Add(run);
            await Json.WriteAtomicAsync(FilePath, history.TakeLast(30).ToList());
            var ok = alert is null;
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ok ? ActivityKind.Message : ActivityKind.Error,
                ok ? $"Self-check: {run.Passed}/{run.Total} passed, {run.AverageSeconds}s average" : $"Self-check: {alert}",
                failures.Count == 0 ? null : string.Join("\n", failures), null, null, ok));
            Changed?.Invoke();
            return run;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Why this run is worse than before (fewer passed, or about 50% slower than usual), or null.</summary>
    public static string? Worse(IReadOnlyList<SelfCheckRun> history, SelfCheckRun run)
    {
        var previous = history.LastOrDefault();
        if (previous is null) return run.Passed < run.Total ? $"{run.Total - run.Passed} of {run.Total} checks failed" : null;
        if (run.Passed < previous.Passed) return $"{run.Passed}/{run.Total} passed, down from {previous.Passed}/{previous.Total}";
        var usual = history.TakeLast(7).Select(h => h.AverageSeconds).Order().ElementAt(Math.Min(6, history.TakeLast(7).Count()) / 2);
        if (usual > 0 && run.AverageSeconds > usual * 1.5 && run.AverageSeconds - usual > 5)
            return $"replies got slower: {run.AverageSeconds}s average, usually {usual}s";
        return null;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _timer?.Dispose();
}
