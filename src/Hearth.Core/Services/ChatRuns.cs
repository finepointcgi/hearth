using System.Collections.Concurrent;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// Runs chat replies in the background, owned by the app rather than by an open page: closing the tab, locking
/// the phone or opening another chat doesn't stop them, and any device viewing the chat follows along. Each run
/// is marked on the saved chat (<see cref="Conversation.Running"/>), so after a restart it picks up again.
/// </summary>
public sealed class ChatRuns(ChatService chat, ConversationStore store)
{
    /// <summary>Runs interrupted longer ago than this aren't resumed (you'd likely have moved on).</summary>
    public static readonly TimeSpan ResumeWithin = TimeSpan.FromHours(12);
    /// <summary>A run that keeps getting interrupted (for example one that crashes the app) stops resuming.</summary>
    public const int MaxResumes = 2;

    private sealed record Run(Conversation Conv, CancellationTokenSource Cts, DateTimeOffset Started);
    private readonly ConcurrentDictionary<string, Run> _runs = new();
    private volatile bool _stopping;
    private int _started;

    /// <summary>A running chat changed (new text, a tool step). Raised often: render throttled.</summary>
    public event Action<string>? Updated;
    /// <summary>A chat started or finished working.</summary>
    public event Action? RunsChanged;

    public bool IsRunning(string? id) => id is not null && _runs.ContainsKey(id);
    /// <summary>The live chat being worked on, shared by every viewer so they see it stream.</summary>
    public Conversation? Live(string? id) => id is not null && _runs.TryGetValue(id, out var r) ? r.Conv : null;
    public DateTimeOffset? StartedAt(string? id) => id is not null && _runs.TryGetValue(id, out var r) ? r.Started : null;
    public IReadOnlyCollection<string> RunningIds => _runs.Keys.ToList();

    /// <summary>Replies to the last message in the background. Returns when the reply is done.</summary>
    public Task ReplyAsync(Conversation conv, SendOptions opts, int resumes = 0) =>
        StartAsync(conv, (update, ct) => chat.GenerateAsync(conv, opts, update, ct),
            new PendingRun(opts.Model, opts.Think, opts.DeepSearch, opts.UseTools, DateTimeOffset.Now, resumes));

    /// <summary>Runs any work on a chat in the background (resumable only when <paramref name="resume"/> is given).</summary>
    public Task StartAsync(Conversation conv, Func<Func<Task>, CancellationToken, Task> work, PendingRun? resume = null)
    {
        var run = new Run(conv, new CancellationTokenSource(), DateTimeOffset.Now);
        if (!_runs.TryAdd(conv.Id, run)) throw new InvalidOperationException("This chat is already working on a reply.");
        Raise(RunsChanged);
        return Task.Run(async () =>
        {
            try
            {
                if (resume is not null)
                {
                    conv.Running = resume;
                    await store.SaveAsync(conv, notify: false);
                }
                await work(() => { RaiseUpdated(conv.Id); return Task.CompletedTask; }, run.Cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Notice = $"Something went wrong: {ex.Message}" });
            }
            finally
            {
                // Shutting down: leave the mark on the saved chat so the next start resumes it.
                if (!_stopping)
                {
                    _runs.TryRemove(conv.Id, out _);
                    if (conv.Running is not null)
                    {
                        conv.Running = null;
                        try { await store.SaveAsync(conv); } catch { /* the reply itself was already saved */ }
                    }
                    run.Cts.Dispose();
                    Raise(RunsChanged);
                    RaiseUpdated(conv.Id);
                }
            }
        });
    }

    /// <summary>Stops a chat's reply (the Stop button). Not resumed later.</summary>
    public void Stop(string? id)
    {
        if (id is not null && _runs.TryGetValue(id, out var r)) r.Cts.Cancel();
    }

    /// <summary>Called when the app is shutting down: running chats keep their mark and resume on next start.</summary>
    public void PrepareForShutdown() => _stopping = true;

    /// <summary>Once per app start: resumes replies a restart cut off.</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // let the model server connection and settings load
            try { await ResumeInterruptedAsync(); } catch { /* a bad chat file shouldn't stop the app */ }
            try { await chat.TitleUntitledAsync(); } catch { /* naming is cosmetic */ }
        });
    }

    /// <summary>Finds chats whose reply was cut off by a restart and carries on with them.</summary>
    public async Task<int> ResumeInterruptedAsync()
    {
        var resumed = 0;
        foreach (var (id, _) in store.Files())
        {
            if (IsRunning(id)) continue;
            var conv = await store.GetAsync(id);
            if (conv?.Running is not { } pending) continue;

            Tidy(conv);
            var tooOld = DateTimeOffset.Now - pending.StartedAt > ResumeWithin;
            var noQuestion = !conv.Messages.Any(m => m.Role == ChatRole.User);
            if (tooOld || noQuestion || pending.Resumes >= MaxResumes)
            {
                conv.Running = null;
                conv.Messages.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Notice = "Hearth restarted while this was working. Send a message to carry on.",
                });
                await store.SaveAsync(conv);
                continue;
            }
            conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Notice = "Hearth restarted, so this picks up where it left off." });
            conv.Unread = true;
            _ = ReplyAsync(conv, new SendOptions(pending.Model, pending.Think, pending.DeepSearch, pending.UseTools), pending.Resumes + 1);
            resumed++;
        }
        return resumed;
    }

    /// <summary>A reply cut off mid-way: unfinished steps are marked, an empty reply is dropped.</summary>
    private static void Tidy(Conversation conv)
    {
        if (conv.Messages.LastOrDefault() is not { Role: ChatRole.Assistant, Notice: null } last) return;
        foreach (var tc in last.ToolCalls.Where(t => t.Status is ToolCallStatus.Running or ToolCallStatus.AwaitingApproval or ToolCallStatus.Pending))
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = "Interrupted by a restart.";
        }
        if (last.Content.Length == 0 && last.ToolCalls.Count == 0) conv.Messages.Remove(last);
        else if (last.Content.Length == 0) last.Content = "_(interrupted by a restart)_";
    }

    private void RaiseUpdated(string id)
    {
        foreach (var h in Updated?.GetInvocationList() ?? [])
            try { ((Action<string>)h)(id); } catch { /* a closed page */ }
    }

    private static void Raise(Action? e)
    {
        foreach (var h in e?.GetInvocationList() ?? [])
            try { ((Action)h)(); } catch { /* a closed page */ }
    }
}
