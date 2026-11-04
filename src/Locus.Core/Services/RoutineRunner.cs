using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Runs bots' routines: on a clock schedule, or when an event arrives — a webhook, a file in a watched folder,
/// a new feed item, or a changed web page. Works while the app is open (desktop/mobile) or the server is up (web).
/// Results land in the bot's own chat, marked unread.
/// </summary>
public sealed class RoutineRunner(
    BotStore bots,
    ConversationStore conversations,
    ChatService chat,
    SettingsStore settingsStore,
    ILlmClientFactory factory,
    TriggerStateStore triggerState,
    IHttpClientFactory http,
    PlatformInfo platform,
    Google.SheetsClient sheets,
    ActivityLog activity,
    ModelPicker models,
    Google.WorkspaceClient workspace) : IDisposable
{
    private const int MaxPayloadChars = 20000;
    private Timer? _timer;
    private int _busy;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly Dictionary<string, (string fingerprint, FileSystemWatcher watcher)> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new();

    public event Action<string>? RoutineFinished;

    public void EnsureStarted()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        bots.Changed += () => _ = SyncWatchersAsync();
        _ = SyncWatchersAsync();
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.Now;
            foreach (var bot in await bots.ListAsync())
                foreach (var r in bot.Routines)
                {
                    if (r.IsDue(now)) await RunAsync(bot, r, null, CancellationToken.None);
                    else if (r.Trigger is TriggerKind.Feed or TriggerKind.PageChange or TriggerKind.Sheet or TriggerKind.Email or TriggerKind.Calendar)
                    {
                        var state = await triggerState.GetAsync(r.Id);
                        if (r.IsPollDue(now, state.LastPolled)) await PollAsync(bot, r, state);
                    }
                }
        }
        catch { /* never crash the timer; errors are written into the chat or trigger state */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Runs a routine now. <paramref name="eventPayload"/> describes the triggering event, if any.</summary>
    public async Task<string> RunAsync(Bot bot, Routine routine, string? eventPayload, CancellationToken ct)
    {
        // One run per routine at a time; events that arrive meanwhile wait their turn instead of being dropped.
        var gate = _gates.GetOrAdd(routine.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await bots.MarkRoutineRunAsync(bot.Id, routine.Id, DateTimeOffset.Now);
            var model = await PickModelAsync(bot);
            var conv = await conversations.GetOrCreateDirectAsync(bot);
            if (model is null)
            {
                conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, BotId = bot.Id,
                    Notice = $"Routine \"{routine.Name}\" skipped: no model available. Set a model on this bot." });
                await conversations.SaveAsync(conv);
                return "No model available.";
            }
            conv.Messages.Add(new ChatMessage
            {
                Role = ChatRole.User,
                Content = routine.Prompt,
                RoutineName = routine.Name,
                EventPayload = eventPayload is null ? null : Truncate(eventPayload, MaxPayloadChars),
            });
            conv.Unread = true;
            var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true),
                () => Task.CompletedTask, ct, autoTitle: false, unattended: true);
            conv.Unread = true;
            await conversations.SaveAsync(conv);
            RoutineFinished?.Invoke(conv.Id);
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Routine,
                $"{bot.Name}: {routine.Name}", reply?.Error ?? Clip(reply?.Content ?? "", 280), bot.Id, conv.Id, reply?.Error is null));
            return reply?.Error ?? "Done.";
        }
        finally
        {
            gate.Release();
        }
    }

    // ------------------------------------------------------------------ webhooks

    public enum WebhookResult { Accepted, NotFound, Forbidden, Disabled }

    /// <summary>Called by the host's HTTP endpoint. Returns immediately; the routine runs in the background.</summary>
    public async Task<WebhookResult> HandleWebhookAsync(string routineId, string? token, string body, string? contentType, string? source)
    {
        var found = await bots.FindRoutineAsync(routineId);
        if (found is not { } f || f.routine.Trigger != TriggerKind.Webhook) return WebhookResult.NotFound;
        if (!SecretsEqual(token, f.routine.WebhookSecret))
        {
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error,
                $"Webhook for \"{f.routine.Name}\" rejected (wrong token)", source, f.bot.Id, null, false));
            return WebhookResult.Forbidden;
        }
        if (!f.routine.Enabled) return WebhookResult.Disabled;

        var payload = new StringBuilder()
            .AppendLine($"Webhook received at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}" + (source is null ? "" : $" from {source}"))
            .AppendLine($"Content-Type: {contentType ?? "unknown"}")
            .AppendLine()
            .Append(body.Length == 0 ? "(empty body)" : body)
            .ToString();
        _ = Task.Run(() => RunAsync(f.bot, f.routine, payload, CancellationToken.None));
        return WebhookResult.Accepted;
    }

    private static bool SecretsEqual(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ------------------------------------------------------------------ polling (feeds, pages)

    /// <summary>Checks a feed or page now. The first check only records a baseline, so you don't get flooded.</summary>
    public async Task<string> PollAsync(Bot bot, Routine r, TriggerState? state = null, bool force = false)
    {
        state ??= await triggerState.GetAsync(r.Id);
        state.LastPolled = DateTimeOffset.Now;
        try
        {
            if (r.Trigger is TriggerKind.Sheet or TriggerKind.Email or TriggerKind.Calendar)
            {
                var result = r.Trigger switch
                {
                    TriggerKind.Sheet => await HandleSheetAsync(bot, r, state, force),
                    TriggerKind.Email => await HandleEmailAsync(bot, r, state, force),
                    _ => await HandleCalendarAsync(bot, r, state, force),
                };
                state.LastError = null;
                return result;
            }
            var client = http.CreateClient(LlmClientFactory.HttpClientName);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, r.Url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Locus/1.0)");
            using var res = await client.SendAsync(req, cts.Token);
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync(cts.Token);
            state.LastError = null;

            return r.Trigger == TriggerKind.Feed
                ? await HandleFeedAsync(bot, r, state, body, force)
                : await HandlePageAsync(bot, r, state, body, force);
        }
        catch (Exception ex)
        {
            if (state.LastError != ex.Message)
                await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error,
                    $"{bot.Name}: \"{r.Name}\" trigger failed", ex.Message, bot.Id, null, false));
            state.LastError = ex.Message;
            return $"Check failed: {ex.Message}";
        }
        finally
        {
            await triggerState.SaveAsync(r.Id, state);
        }
    }

    private async Task<string> HandleFeedAsync(Bot bot, Routine r, TriggerState state, string xml, bool force)
    {
        var items = FeedParser.Parse(xml);
        var baseline = state.SeenIds.Count == 0 && !force;
        var fresh = items.Where(i => !state.SeenIds.Contains(i.Id)).ToList();
        state.SeenIds = items.Select(i => i.Id).Concat(state.SeenIds).Distinct().Take(500).ToList();
        if (baseline) return $"Watching {items.Count} existing items; you'll hear about new ones.";
        if (force && fresh.Count == 0) fresh = items.Take(1).ToList();
        if (fresh.Count == 0) return "No new items.";

        var payload = new StringBuilder($"{fresh.Count} new item(s) in feed {r.Url}:\n\n");
        foreach (var i in fresh.Take(10))
            payload.AppendLine($"## {i.Title}\n{i.Link}\n{(i.Published is { } p ? $"Published {p:yyyy-MM-dd HH:mm}\n" : "")}{i.Summary}\n");
        await RunAsync(bot, r, payload.ToString(), CancellationToken.None);
        return $"{fresh.Count} new item(s).";
    }

    /// <summary>New messages matching the Gmail query since last check (the first check just records what's there).</summary>
    private async Task<string> HandleEmailAsync(Bot bot, Routine r, TriggerState state, bool force)
    {
        var query = string.IsNullOrWhiteSpace(r.Url) ? "in:inbox is:unread" : r.Url;
        var mails = await workspace.SearchMailAsync(query, 20, CancellationToken.None);
        var baseline = state.SeenIds.Count == 0 && !force;
        var fresh = mails.Where(m => !state.SeenIds.Contains(m.Id)).ToList();
        state.SeenIds = mails.Select(m => m.Id).Concat(state.SeenIds).Distinct().Take(1000).ToList();
        if (baseline) return $"Watching \"{query}\" ({mails.Count} existing messages); you'll hear about new ones.";
        if (force && fresh.Count == 0) fresh = mails.Take(1).ToList();
        if (fresh.Count == 0) return "No new email.";

        var payload = new StringBuilder($"{fresh.Count} new email(s) matching \"{query}\":\n\n");
        foreach (var m in fresh.Take(5))
        {
            var full = await workspace.ReadMailAsync(m.Id, CancellationToken.None);
            payload.AppendLine($"--- id {m.Id} ---\nFrom: {m.From}\nSubject: {m.Subject}\nDate: {m.Date:f}\n\n{Truncate(full.Body, 4000)}\n");
        }
        foreach (var m in fresh.Skip(5)) payload.AppendLine($"(also) {m.From} — {m.Subject} (id {m.Id})");
        await RunAsync(bot, r, payload.ToString(), CancellationToken.None);
        return $"{fresh.Count} new email(s).";
    }

    /// <summary>Events starting within LeadMinutes that haven't been announced yet.</summary>
    private async Task<string> HandleCalendarAsync(Bot bot, Routine r, TriggerState state, bool force)
    {
        var now = DateTimeOffset.Now;
        var lead = Math.Clamp(r.LeadMinutes, 1, 24 * 60);
        var events = (await workspace.EventsAsync(now, now.AddMinutes(force ? 7 * 24 * 60 : lead), null, 20, CancellationToken.None))
            .Where(e => !e.AllDay).ToList();
        var fresh = events.Where(e => !state.SeenIds.Contains($"{e.Id}@{e.Start:o}")).ToList();
        if (force && fresh.Count == 0) fresh = events.Take(1).ToList();
        if (fresh.Count == 0) return "Nothing starting soon.";
        state.SeenIds = fresh.Select(e => $"{e.Id}@{e.Start:o}").Concat(state.SeenIds).Distinct().Take(1000).ToList();

        foreach (var e in force ? fresh.Take(1) : fresh)
        {
            var minutes = Math.Max(0, (int)((e.Start ?? now) - now).TotalMinutes);
            var payload = $"Upcoming event in {minutes} minutes:\n{Google.WorkspaceClient.Describe(e)}\n" +
                          (e.Description is { Length: > 0 } d ? $"\nNotes:\n{Truncate(d, 3000)}\n" : "") +
                          (e.Link is not null ? $"\n{e.Link}" : "");
            await RunAsync(bot, r, payload, CancellationToken.None);
        }
        return $"{fresh.Count} upcoming event(s).";
    }

    /// <summary>Compares the watched range with last time; new or edited rows go to the bot (with the header row).</summary>
    private async Task<string> HandleSheetAsync(Bot bot, Routine r, TriggerState state, bool force)
    {
        var id = await sheets.ResolveAsync(r.Url, CancellationToken.None);
        var range = string.IsNullOrWhiteSpace(r.SheetRange)
            ? Tools.SheetsToolProvider.Quote((await sheets.InfoAsync(id, CancellationToken.None)).tabs.FirstOrDefault()?.Title ?? "Sheet1")
            : r.SheetRange;
        var values = await sheets.ReadAsync(id, range, CancellationToken.None);
        var (_, row0) = Google.SheetsClient.StartOf(values.Range);
        var lines = values.Rows.Select(row => string.Join("\t", row).TrimEnd('\t')).ToList();
        var text = string.Join("\n", lines);
        var before = state.PageText;
        var hash = TextDiff.Hash(text);
        var changed = state.PageHash is not null && state.PageHash != hash;
        state.PageHash = hash;
        state.PageText = Truncate(text, 200000);
        if (before is null && !force) return $"Watching {values.Rows.Count} rows; you'll hear about new or edited ones.";
        if (!changed && !force) return "No change.";

        var oldSet = (before ?? "").Split('\n').ToHashSet();
        var changedRows = lines.Select((l, i) => (l, i)).Where(x => x.l.Length > 0 && !oldSet.Contains(x.l)).ToList();
        var removed = (before ?? "").Split('\n').Count(l => l.Length > 0 && !lines.Contains(l));
        if (force && changedRows.Count == 0) changedRows = lines.Select((l, i) => (l, i)).Skip(1).TakeLast(3).ToList();

        var payload = new StringBuilder($"Google Sheet {values.Range} changed: {changedRows.Count} new or edited row(s)" +
                                        (removed > 0 ? $", {removed} removed" : "") + ".\n\n");
        if (values.Rows.Count > 0) payload.AppendLine($"Header (row {row0}): {string.Join(" | ", values.Rows[0])}");
        foreach (var (line, i) in changedRows.Take(50))
            payload.AppendLine($"Row {row0 + i}: {line.Replace("\t", " | ")}");
        if (changedRows.Count > 50) payload.AppendLine($"…and {changedRows.Count - 50} more.");
        await RunAsync(bot, r, payload.ToString(), CancellationToken.None);
        return $"{changedRows.Count} new or edited row(s).";
    }

    private async Task<string> HandlePageAsync(Bot bot, Routine r, TriggerState state, string html, bool force)
    {
        var (title, text) = TextExtractor.FromHtml(html);
        var hash = TextDiff.Hash(text);
        var before = state.PageText;
        var changed = state.PageHash is not null && state.PageHash != hash;
        state.PageHash = hash;
        state.PageText = Truncate(text, 60000);
        if (before is null && !force) return "Saved the current version; you'll hear when it changes.";
        if (!changed && !force) return "No change.";

        var (added, removed) = TextDiff.Lines(before ?? "", text);
        var payload = new StringBuilder($"The page \"{title}\" ({r.Url}) changed.\n\n");
        if (added.Count > 0) payload.AppendLine("New or changed lines:").AppendLine(string.Join("\n", added.Take(80).Select(l => "+ " + l)));
        if (removed.Count > 0) payload.AppendLine().AppendLine("Removed lines:").AppendLine(string.Join("\n", removed.Take(40).Select(l => "- " + l)));
        if (added.Count == 0 && removed.Count == 0) payload.AppendLine(Truncate(text, 6000));
        await RunAsync(bot, r, payload.ToString(), CancellationToken.None);
        return "Change found.";
    }

    // ------------------------------------------------------------------ folder watching

    private async Task SyncWatchersAsync()
    {
        if (!platform.CanRunProcesses) return; // folder access is desktop-only
        var wanted = new Dictionary<string, (Bot bot, Routine routine)>();
        foreach (var b in await bots.ListAsync())
            foreach (var r in b.Routines.Where(r => r.Enabled && r.Trigger == TriggerKind.Folder && Directory.Exists(r.FolderPath)))
                wanted[r.Id] = (b, r);

        lock (_watchers)
        {
            foreach (var id in _watchers.Keys.Where(k => !wanted.ContainsKey(k) ||
                         _watchers[k].fingerprint != Fingerprint(wanted[k].routine)).ToList())
            {
                _watchers[id].watcher.Dispose();
                _watchers.Remove(id);
            }
            foreach (var (id, (bot, routine)) in wanted.Where(w => !_watchers.ContainsKey(w.Key)))
            {
                try
                {
                    var w = new FileSystemWatcher(routine.FolderPath,
                        string.IsNullOrWhiteSpace(routine.FilePattern) ? "*" : routine.FilePattern.Trim())
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };
                    var botId = bot.Id;
                    var routineId = routine.Id;
                    w.Created += (_, e) => OnFileEvent(botId, routineId, e.FullPath, "added");
                    w.Changed += (_, e) => OnFileEvent(botId, routineId, e.FullPath, "changed");
                    w.Renamed += (_, e) => OnFileEvent(botId, routineId, e.FullPath, "added");
                    w.EnableRaisingEvents = true;
                    _watchers[id] = (Fingerprint(routine), w);
                }
                catch { /* bad path or permissions — the editor shows whether the folder exists */ }
            }
        }
    }

    private static string Fingerprint(Routine r) => $"{r.FolderPath}|{r.FilePattern}";

    private void OnFileEvent(string botId, string routineId, string path, string what)
    {
        // Editors and copies fire several events per file; act once per file per 10 seconds, after writes settle.
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(path, out var last) && now - last < TimeSpan.FromSeconds(10)) return;
        _recentFiles[path] = now;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (await bots.FindRoutineAsync(routineId) is not { } f || !File.Exists(path)) return;
            var info = new FileInfo(path);
            var payload = new StringBuilder($"File {what}: {path}\nSize: {info.Length:N0} bytes, modified {info.LastWriteTime:yyyy-MM-dd HH:mm}\n\n");
            try
            {
                if (TextExtractor.IsSupported(path) && info.Length < 15 * 1024 * 1024)
                    payload.Append(Truncate(TextExtractor.Extract(path, await File.ReadAllBytesAsync(path)), MaxPayloadChars));
                else payload.Append("(binary or unsupported file type — content not included)");
            }
            catch (Exception ex) { payload.Append($"(couldn't read the file: {ex.Message})"); }
            await RunAsync(f.bot, f.routine, payload.ToString(), CancellationToken.None);
        });
    }

    // ------------------------------------------------------------------

    private Task<string?> PickModelAsync(Bot bot) => models.PickAsync(bot);

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "\n…[truncated]";

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.watcher.Dispose();
            _watchers.Clear();
        }
    }
}
