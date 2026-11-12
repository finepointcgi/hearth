using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Hearth.Core.Models;

namespace Hearth.Core.Services.Channels;

/// <summary>
/// Lets you talk to Hearth and your bots from Telegram and Discord.
/// <list type="bullet">
/// <item>Pairing: a chat is ignored until someone sends it the code shown on the Connections page.</item>
/// <item>Routing: "/bot Name" picks who answers; "@Name …" asks one bot once; otherwise the chat's active bot.</item>
/// <item>Approvals: tools that need an OK get Allow/Deny buttons in the chat.</item>
/// <item>Notifications: routine results (and approvals from elsewhere) are pushed to linked chats.</item>
/// </list>
/// </summary>
public sealed class ChannelHub : IDisposable
{
    private readonly ChannelStore _store;
    private readonly ChatService _chat;
    private readonly ConversationStore _conversations;
    private readonly BotStore _bots;
    private readonly ModelPicker _models;
    private readonly ApprovalCenter _approvals;
    private readonly ActivityLog _activity;
    private readonly RoutineRunner _routines;
    private readonly HealthMonitor _health;
    private readonly SettingsStore _settings;
    private readonly SpeechService _speech;
    private readonly AudioTranscriber _transcriber;
    private readonly Ocr _ocr;
    private readonly DocumentStore _documents;
    private readonly TaskRunner _tasks;
    private readonly TaskStore _taskStore;
    private readonly HeartbeatRunner _heartbeat;
    private readonly SpokenReply _spoken;
    private readonly IBotComputer _computer;
    private readonly LearningLoop _loop;
    private readonly LearningRecap _recap;
    private readonly Dictionary<string, IChannelAdapter> _adapters;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, (string channel, string chatId)> _convToChat = new();
    private readonly ConcurrentDictionary<string, DateTime> _nagged = new();
    private bool _started;

    public ChannelHub(ChannelStore store, ChatService chat, ConversationStore conversations, BotStore bots, ModelPicker models,
        ApprovalCenter approvals, ActivityLog activity, RoutineRunner routines, IHttpClientFactory http, HealthMonitor health,
        SettingsStore settings, SpeechService speech, DocumentStore documents, TaskRunner tasks, TaskStore taskStore,
        HeartbeatRunner heartbeat, AudioTranscriber transcriber, Ocr ocr, SpokenReply spoken, IBotComputer computer,
        LearningLoop loop, LearningRecap recap, IAppPaths paths)
    {
        _keys = new ButtonKeys(Path.Combine(paths.DataDirectory, "button-keys.json"));
        _loop = loop;
        _recap = recap;
        _spoken = spoken;
        _computer = computer;
        _heartbeat = heartbeat;
        _tasks = tasks;
        _taskStore = taskStore;
        _health = health;
        _settings = settings;
        _speech = speech;
        _transcriber = transcriber;
        _ocr = ocr;
        _documents = documents;
        _store = store; _chat = chat; _conversations = conversations; _bots = bots; _models = models;
        _approvals = approvals; _activity = activity; _routines = routines;
        _adapters = new() { ["telegram"] = new TelegramAdapter(http), ["discord"] = new DiscordAdapter(http), ["slack"] = new SlackAdapter(http),
            ["imessage"] = new IMessageAdapter(), ["signal"] = new SignalAdapter(http) };
    }

    public event Action? Changed;

    /// <summary>Current pairing code, valid for 15 minutes.</summary>
    public (string code, DateTimeOffset expires)? Pairing { get; private set; }

    public IChannelAdapter Adapter(string channel) => _adapters[channel];

    public string NewPairingCode()
    {
        var code = "HEARTH-" + RandomNumberGenerator.GetInt32(100000, 999999);
        Pairing = (code, DateTimeOffset.Now.AddMinutes(15));
        Changed?.Invoke();
        return code;
    }

    public async Task EnsureStartedAsync()
    {
        if (_started) return;
        _started = true;
        _approvals.Requested += OnApprovalRequested;
        _routines.RoutineFinished += OnRoutineFinished;
        _health.Changed += OnHealthChanged;
        _tasks.Milestone += OnTaskMilestone;
        _heartbeat.Alert += OnHeartbeatAlert;
        _recap.Ready += OnRecapReady;
        _health.EnsureStarted();
        _heldTimer = new Timer(_ => _ = FlushHeldAsync(DateTimeOffset.Now), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        await _store.GetAsync();
        foreach (var ch in _adapters.Keys)
        {
            try { await RestartAsync(ch); }
            catch { /* logged in RestartAsync; one channel failing mustn't stop the others */ }
        }
    }

    /// <summary>(Re)connects a channel with its saved token; stops it when there's none.</summary>
    private readonly Dictionary<string, string> _startErrors = [];
    private readonly Dictionary<string, (CancellationTokenSource cts, int attempt)> _retries = [];

    /// <summary>Why a channel isn't connected (it keeps retrying in the background), or null.</summary>
    public string? StartError(string channel)
    {
        lock (_startErrors) return _startErrors.GetValueOrDefault(channel);
    }

    /// <summary>(Re)connects a channel with its saved token; stops it when there's none. Failures retry with backoff.</summary>
    public Task RestartAsync(string channel) => RestartAsync(channel, attempt: 0);

    private async Task RestartAsync(string channel, int attempt)
    {
        lock (_retries)
            if (_retries.Remove(channel, out var pending) && attempt == 0) pending.cts.Cancel(); // a manual restart replaces the schedule
        var adapter = _adapters[channel];
        await adapter.StopAsync();
        var cfg = await _store.GetAsync();
        var token = _store.Token(channel);
        if (token is null)
        {
            lock (_startErrors) _startErrors.Remove(channel);
            Changed?.Invoke();
            return;
        }
        try
        {
            await adapter.StartAsync(token, channel switch { "telegram" => cfg.TelegramApiBase, "slack" => cfg.SlackApiBase, _ => cfg.DiscordApiBase },
                m => HandleMessageAsync(adapter, m), (chatId, data) => ButtonAsync(adapter, chatId, data), CancellationToken.None);
            bool wasFailing;
            lock (_startErrors) wasFailing = _startErrors.Remove(channel);
            if (wasFailing)
                await _activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Channel, $"{Title(channel)} reconnected"));
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? $"Couldn't reach {Title(channel)} (timed out)." : ex.Message;
            bool firstFailure;
            lock (_startErrors)
            {
                firstFailure = !_startErrors.ContainsKey(channel);
                _startErrors[channel] = message;
            }
            if (firstFailure) // one entry per outage, not one per retry
                await _activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error, $"{Title(channel)} couldn't connect",
                    message + " Retrying automatically.", Ok: false));
            ScheduleRetry(channel, attempt + 1);
            if (attempt == 0) throw;
        }
        finally { Changed?.Invoke(); }
    }

    private void ScheduleRetry(string channel, int attempt)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(attempt - 1, 5))));
        var cts = new CancellationTokenSource();
        lock (_retries)
        {
            if (_retries.Remove(channel, out var old)) old.cts.Cancel();
            _retries[channel] = (cts, attempt);
        }
        _ = Task.Delay(delay, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            lock (_retries)
                if (!_retries.TryGetValue(channel, out var current) || current.cts != cts) return;
                else _retries.Remove(channel);
            try { await RestartAsync(channel, attempt); } catch { /* scheduled again inside */ }
        }, TaskScheduler.Default);
    }

    public static string Title(string channel) => channel switch
    {
        "telegram" => "Telegram", "slack" => "Slack", "imessage" => "iMessage", "signal" => "Signal", _ => "Discord",
    };

    // ------------------------------------------------------------------ incoming messages

    private async Task HandleMessageAsync(IChannelAdapter a, IncomingMessage m)
    {
        var cfg = await _store.GetAsync();
        var link = cfg.Links.FirstOrDefault(l => l.Channel == m.Channel && l.ChatId == m.ChatId && l.UserId == m.UserId);
        var text = m.Text.Trim();

        if (link is null)
        {
            if (Pairing is { } p && p.expires > DateTimeOffset.Now && text.Contains(p.code, StringComparison.OrdinalIgnoreCase))
            {
                link = new ChannelLink { Channel = m.Channel, ChatId = m.ChatId, UserId = m.UserId, Name = $"{m.UserName} ({m.ChatName})" };
                var newLink = link;
                await _store.UpdateAsync(c => c.Links.Add(newLink));
                Pairing = null;
                Changed?.Invoke();
                await _activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Channel, $"{Title(m.Channel)} linked: {link.Name}"));
                await a.SendTextAsync(m.ChatId, "✅ Linked to Hearth.\n\n" + await HelpAsync(link));
                return;
            }
            // Don't answer strangers in groups at all; in private chats, explain once an hour.
            if (m.IsPrivate && (!_nagged.TryGetValue(m.ChatId, out var t) || DateTime.UtcNow - t > TimeSpan.FromHours(1)))
            {
                _nagged[m.ChatId] = DateTime.UtcNow;
                await a.SendTextAsync(m.ChatId, "This chat isn't linked to Hearth yet. Open Hearth → Connections, click \"Get pairing code\" and send the code here.");
            }
            return;
        }

        if (!m.IsPrivate && !m.MentionsMe && !text.StartsWith('/')) return; // in groups, only when addressed

        var bots = await _bots.ListAsync();
        if (text.StartsWith('/')) { await CommandAsync(a, link, text, bots); return; }

        // The answer to "👎 What was wrong?".
        if (_awaitingFix.TryRemove(m.ChatId, out var fix) && DateTime.UtcNow - fix.at < TimeSpan.FromMinutes(30) && (m.Files ?? []).Count == 0)
        {
            await FixAsync(a, link, fix.conv, fix.msg, text);
            return;
        }

        // Attachments: voice → transcript, images → for vision models, documents → text (or the library if large).
        var images = new List<ImageAttachment>();
        var attachmentText = new StringBuilder();
        var heardVoice = false;
        var settings = await _settings.GetAsync();
        foreach (var f in m.Files ?? [])
        {
            try
            {
                if (f.IsVoice)
                {
                    if (await _transcriber.StatusAsync() is { ready: false } status)
                    {
                        await a.SendTextAsync(m.ChatId, $"🎙 I can't listen to voice notes yet: {status.how}.");
                        return;
                    }
                    var said = await _transcriber.TranscribeAsync(f.Data, f.Name, f.MimeType, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(said)) { await a.SendTextAsync(m.ChatId, "🎙 I couldn't make out anything in that voice note."); return; }
                    await a.SendTextAsync(m.ChatId, $"🎙 “{said}”");
                    heardVoice = true;
                    text = (text + " " + said).Trim();
                }
                else if (f.MimeType.StartsWith("image/"))
                {
                    images.Add(new ImageAttachment(f.Name, f.MimeType, Convert.ToBase64String(f.Data)));
                    // The text in it too, for models that can't see images (read on this computer).
                    if (_ocr.Available)
                        try { if (await _ocr.ReadAsync(f.Data, f.Name) is { Length: > 10 } seen) attachmentText.Append($"\n--- text in {f.Name} ---\n{seen}\n"); }
                        catch { }
                }
                else if (TextExtractor.IsSupported(f.Name))
                {
                    var extracted = TextExtractor.Extract(f.Name, f.Data);
                    if (extracted.Length <= settings.Documents.InlineLimit)
                        attachmentText.Append($"\n--- {f.Name} ---\n{extracted}\n");
                    else
                    {
                        await a.SendTextAsync(m.ChatId, $"📚 {f.Name} is long, so I'm adding it to your document library first…");
                        await _documents.AddAsync(f.Name, extracted);
                        attachmentText.Append($"\n[{f.Name} was added to the document library; use search_documents to read it]\n");
                    }
                }
                else await a.SendTextAsync(m.ChatId, $"I can't read {f.Name} ({f.MimeType}) yet — send a PDF, text or image.");
            }
            catch (Exception ex) { await a.SendTextAsync(m.ChatId, $"⚠ Couldn't use {f.Name}: {ex.Message}"); }
        }
        if (text.Length == 0 && images.Count > 0) text = "What's in this image?";
        if (text.Length == 0 && attachmentText.Length > 0) text = "Here's a file — summarize it.";
        if (text.Length == 0) return;

        // "@Name message" asks that bot just this once.
        Bot? target = null;
        foreach (var b in bots.OrderByDescending(b => b.Name.Length))
            if (text.StartsWith("@" + b.Name, StringComparison.OrdinalIgnoreCase))
            {
                target = b;
                text = text[(b.Name.Length + 1)..].Trim();
                break;
            }
        await ReplyAsync(a, link, target, text, m.UserName, images, attachmentText.Length > 0 ? attachmentText.ToString() : null, heardVoice);
    }

    private async Task CommandAsync(IChannelAdapter a, ChannelLink link, string text, List<Bot> bots)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant().Split('@')[0];
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (cmd)
        {
            case "/bots":
                await a.SendTextAsync(link.ChatId, bots.Count == 0 ? "No bots yet — create some in Hearth."
                    : string.Join("\n", bots.Select(b => $"{b.Emoji} {b.Name} — {b.Title}{(b.Id == link.ActiveBotId && link.ActiveGroupId is null ? "  ← active" : "")}")));
                break;
            case "/bot":
                var pick = arg.Length == 0 || IsPlain(arg) ? null
                    : bots.FirstOrDefault(b => b.Name.Equals(arg.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
                      ?? bots.FirstOrDefault(b => b.Name.StartsWith(arg.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                if (arg.Length > 0 && pick is null && !IsPlain(arg))
                {
                    await a.SendTextAsync(link.ChatId, $"No bot called \"{arg}\". Try /bots.");
                    break;
                }
                await _store.UpdateAsync(c => { var l = Find(c, link)!; l.ActiveBotId = pick?.Id; l.ActiveGroupId = null; });
                await a.SendTextAsync(link.ChatId, pick is null ? "Now talking to Hearth." : $"Now talking to {pick.Emoji} {pick.Name}.");
                break;
            case "/groups":
                var groups = await GroupsAsync();
                await a.SendTextAsync(link.ChatId, groups.Count == 0
                    ? "No group chats yet — create one in Hearth → Bots → New group chat."
                    : string.Join("\n", groups.Select(g => $"👥 {g.Title}{(g.Id == link.ActiveGroupId ? "  ← active" : "")}")) + "\n\nSwitch with /group Name");
                break;
            case "/group":
                var all = await GroupsAsync();
                var group = all.FirstOrDefault(g => g.Title.Equals(arg, StringComparison.OrdinalIgnoreCase))
                            ?? all.FirstOrDefault(g => arg.Length > 0 && g.Title.Contains(arg, StringComparison.OrdinalIgnoreCase));
                if (group is null) { await a.SendTextAsync(link.ChatId, arg.Length == 0 ? "Which group? Try /groups." : $"No group called \"{arg}\". Try /groups."); break; }
                await _store.UpdateAsync(c => Find(c, link)!.ActiveGroupId = group.Id);
                var members = (await _bots.GetManyAsync(group.BotIds)).Select(b => $"{b.Emoji} {b.Name}");
                await a.SendTextAsync(link.ChatId, $"Now in group 👥 {group.Title} with {string.Join(", ", members)}. @Name to ask one of them.");
                break;
            case "/task":
                if (arg.Length == 0) { await a.SendTextAsync(link.ChatId, "What should I work on? e.g. /task Find three quiet cafés near me with good wifi and compare them"); break; }
                var t = await _tasks.StartAsync(arg, link.ActiveGroupId is null ? link.ActiveBotId : null, $"{link.Channel}:{link.ChatId}");
                await a.SendTextAsync(link.ChatId, $"🎯 On it: {t.Title}\nI'll post the plan, progress and the result here. /tasks to see all, /answer when I ask something.");
                break;
            case "/tasks":
                var tasks = (await _taskStore.ListAsync()).Where(x => x.State is TaskState.Running or TaskState.WaitingForYou or TaskState.Paused or TaskState.Scheduled).ToList();
                await a.SendTextAsync(link.ChatId, tasks.Count == 0 ? "No active tasks. Start one with /task …"
                    : string.Join("\n", tasks.Select(x => $"{Icon(x.State)} {x.Title} — {x.DoneSteps}/{x.Plan.Count} steps{(x.Question is null ? "" : $"\n   ❓ {x.Question}")}")));
                break;
            case "/answer":
                var waiting = (await _taskStore.ListAsync()).FirstOrDefault(x => x.State == TaskState.WaitingForYou);
                if (waiting is null) { await a.SendTextAsync(link.ChatId, "No task is waiting for an answer."); break; }
                if (arg.Length == 0) { await a.SendTextAsync(link.ChatId, $"❓ {waiting.Question}\nReply with /answer your answer"); break; }
                await _tasks.AnswerAsync(waiting.Id, arg);
                await a.SendTextAsync(link.ChatId, $"👍 Thanks — continuing \"{waiting.Title}\".");
                break;
            case "/new":
                if (link.ActiveGroupId is not null) { await a.SendTextAsync(link.ChatId, "Group chats keep one shared history — leave it with /bot Name to start fresh with one bot."); break; }
                await _store.UpdateAsync(c => Find(c, link)!.Conversations.Remove(link.ActiveBotId ?? Bot.PlainId));
                await a.SendTextAsync(link.ChatId, "Started a fresh conversation.");
                break;
            case "/stop":
                foreach (var key in _running.Keys.Where(k => k.StartsWith(link.ChatId + "|"))) _running[key].Cancel();
                await a.SendTextAsync(link.ChatId, "Stopped.");
                break;
            case "/notify":
                var mode = arg.Trim().ToLowerInvariant();
                if (mode is not ("on" or "off" or "approvals" or ""))
                {
                    await a.SendTextAsync(link.ChatId, "Use /notify on, /notify off, or /notify approvals (only approval requests).");
                    break;
                }
                if (mode == "") { await a.SendTextAsync(link.ChatId, NotifySummary(link)); break; }
                var everything = mode == "on";
                await _store.UpdateAsync(c =>
                {
                    var l = Find(c, link)!;
                    l.NotifyRoutines = l.NotifyTasks = l.NotifyHeartbeat = l.NotifyHealth = everything;
                    l.NotifyApprovals = mode != "off";
                });
                await a.SendTextAsync(link.ChatId, mode switch
                {
                    "on" => "🔔 You'll get routine results, task updates, check-ins, alerts and approval requests here.",
                    "approvals" => "🔕 Only approval requests from now on. (/notify on for everything again.)",
                    _ => "🔕 Notifications off. Replies to your own messages still come here.",
                });
                break;
            case "/voice":
                var voiceOn = arg.Trim().ToLowerInvariant();
                if (voiceOn is not ("on" or "off"))
                {
                    await a.SendTextAsync(link.ChatId, $"Voice replies are {(link.VoiceReplies ? "on" : "off")}. /voice on to answer your voice notes with a voice note, /voice off to stop.");
                    break;
                }
                if (voiceOn == "on" && !await _spoken.AvailableAsync())
                {
                    await a.SendTextAsync(link.ChatId, "Hearth has no voice to speak with here: set a text-to-speech server in Settings → Voice.");
                    break;
                }
                await _store.UpdateAsync(c => Find(c, link)!.VoiceReplies = voiceOn == "on");
                await a.SendTextAsync(link.ChatId, voiceOn == "on" ? "🔊 I'll answer your voice notes with a voice note too (plus the text)." : "🔇 Text replies only.");
                break;
            case "/files":
                var filesOn = arg.Trim().ToLowerInvariant();
                if (filesOn is not ("on" or "off")) { await a.SendTextAsync(link.ChatId, $"Sending documents as files is {(link.SendFiles ? "on" : "off")}. Use /files on or /files off."); break; }
                await _store.UpdateAsync(c => Find(c, link)!.SendFiles = filesOn == "on");
                await a.SendTextAsync(link.ChatId, filesOn == "on" ? "📎 Documents bots make will be sent here as files." : "📎 Files off: you'll get links and text only.");
                break;
            case "/quiet":
                var range = arg.Replace(" ", "").Split('-', 2);
                if (arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    await _store.UpdateAsync(c => { var l = Find(c, link)!; l.QuietFrom = l.QuietTo = ""; });
                    await a.SendTextAsync(link.ChatId, "Quiet hours off.");
                }
                else if (range.Length == 2 && TimeOnly.TryParse(range[0], out var qf) && TimeOnly.TryParse(range[1], out var qt))
                {
                    await _store.UpdateAsync(c => { var l = Find(c, link)!; l.QuietFrom = qf.ToString("HH:mm"); l.QuietTo = qt.ToString("HH:mm"); });
                    await a.SendTextAsync(link.ChatId, $"🌙 Quiet from {qf:HH:mm} to {qt:HH:mm}: only approvals and task questions come through then; task updates wait until it ends.");
                }
                else await a.SendTextAsync(link.ChatId, "Use /quiet 22:00-07:00, or /quiet off.");
                break;
            default:
                await a.SendTextAsync(link.ChatId, await HelpAsync(link));
                break;
        }
    }

    private async Task<List<ConversationSummary>> GroupsAsync() =>
        (await _conversations.ListAsync()).Where(c => c.BotIds.Count > 1).ToList();

    private async Task<string> HelpAsync(ChannelLink link)
    {
        var active = link.ActiveGroupId is not null
            ? "the group " + ((await _conversations.GetAsync(link.ActiveGroupId))?.Title ?? "?")
            : (await _bots.GetAsync(link.ActiveBotId ?? ""))?.Name ?? "Hearth";
        return $"You're talking to {active}. Just send a message — or a photo, a file or a voice note.\n\n" +
               "/bots — list your bots\n/bot Name — switch who answers (/bot hearth for plain Hearth)\n" +
               "/groups, /group Name — talk to a group of bots\n" +
               "@Name your message — ask one bot once\n/new — fresh conversation\n/stop — stop the current reply\n" +
               "/task goal — start a long-running task; /tasks — progress; /answer … — reply to a task's question\n" +
               "/notify on|off|approvals — what Hearth sends here on its own\n" +
               "/quiet 22:00-07:00 (or off) — hold everything but approvals overnight\n" +
               "/voice on|off — answer voice notes with a voice note\n/files on|off — send documents bots make as files\n" +
               "Say \"undo that\" to reverse a change a bot just made.";
    }

    /// <summary>What this chat gets, for /notify with no argument.</summary>
    private static string NotifySummary(ChannelLink l)
    {
        string Mark(bool on) => on ? "✅" : "▫️";
        return $"Notifications here:\n{Mark(l.NotifyApprovals)} approval requests\n{Mark(l.NotifyRoutines)} routine results\n" +
               $"{Mark(l.NotifyTasks)} task updates\n{Mark(l.NotifyHeartbeat)} heartbeat check-ins\n{Mark(l.NotifyHealth)} model server alerts\n" +
               (l.QuietFrom.Length > 0 ? $"🌙 quiet {l.QuietFrom}–{l.QuietTo}\n" : "") +
               "Change with /notify on|off|approvals and /quiet 22:00-07:00, or in Hearth → Connections → Chat apps.";
    }

    private static ChannelLink? Find(ChannelConfig c, ChannelLink l) =>
        c.Links.FirstOrDefault(x => x.Channel == l.Channel && x.ChatId == l.ChatId && x.UserId == l.UserId);

    private static readonly IReadOnlyList<(string label, string data)> QuickButtons =
        [("🔄 Again", "q:again"), ("✂️ Shorter", "q:short"), ("➕ More", "q:more")];

    /// <summary>
    /// Sends <paramref name="text"/> to the chat's current bot (or group, or a one-off @bot) and posts the reply.
    /// text = null regenerates the last reply.
    /// </summary>
    private async Task ReplyAsync(IChannelAdapter a, ChannelLink link, Bot? oneOff, string? text, string userName,
        IReadOnlyList<ImageAttachment>? images = null, string? attachmentText = null, bool voiceNote = false)
    {
        // Who answers: a one-off @bot, else the active group, else the active bot (or plain Hearth).
        Conversation? conv = null;
        Bot? bot = oneOff;
        var isGroup = false;
        if (oneOff is null && link.ActiveGroupId is not null)
        {
            conv = await _conversations.GetAsync(link.ActiveGroupId);
            isGroup = conv is { BotIds.Count: > 1 };
            if (!isGroup) { conv = null; await _store.UpdateAsync(c => Find(c, link)!.ActiveGroupId = null); }
        }
        if (!isGroup)
        {
            bot ??= await _bots.GetAsync(link.ActiveBotId ?? "");
            var key = bot?.Id ?? Bot.PlainId;
            if (link.Conversations.TryGetValue(key, out var convId)) conv = await _conversations.GetAsync(convId);
            if (conv is null)
            {
                conv = new Conversation { Title = $"📱 {Title(link.Channel)} · {bot?.Name ?? "Hearth"}", BotIds = bot is null ? [] : [bot.Id] };
                var newId = conv.Id;
                await _store.UpdateAsync(c => Find(c, link)!.Conversations[key] = newId);
            }
        }

        var model = await _models.PickAsync(isGroup ? null : bot);
        if (model is null) { await a.SendTextAsync(link.ChatId, "⚠ Hearth has no model available right now. Check Settings on your computer."); return; }

        if (text is null)
        {
            // Regenerate: drop everything after the last user message.
            var lastUser = conv!.Messages.FindLastIndex(m => m.Role == ChatRole.User);
            if (lastUser < 0) return;
            conv.Messages.RemoveRange(lastUser + 1, conv.Messages.Count - lastUser - 1);
        }
        else
        {
            conv!.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text, Images = images?.ToList() ?? [], AttachmentText = attachmentText });
            await _activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Channel,
                $"{Title(link.Channel)}: {userName} → {(isGroup ? conv.Title : bot?.Name ?? "Hearth")}",
                text.Length > 160 ? text[..160] + "…" : text, bot?.Id, conv.Id));
        }
        var firstNew = conv.Messages.Count;

        var runKey = $"{link.ChatId}|{conv.Id}";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _running[runKey] = cts;
        _convToChat[conv.Id] = (link.Channel, link.ChatId);
        var lastTyping = DateTime.MinValue;
        var sentEarly = new HashSet<string>();
        try
        {
            await a.TypingAsync(link.ChatId);
            await _chat.GenerateAsync(conv, new SendOptions(model, false, false, true), async () =>
            {
                // The lead's quick "on it" goes out at once; the answer follows when the team is done.
                foreach (var ack in conv.Messages.Skip(firstNew).ToArray().Where(m => m.Ack && m.Content.Length > 0))
                    if (sentEarly.Add(ack.Id)) await a.SendTextAsync(link.ChatId, ack.Content);
                if (DateTime.UtcNow - lastTyping > TimeSpan.FromSeconds(4))
                {
                    lastTyping = DateTime.UtcNow;
                    await a.TypingAsync(link.ChatId);
                }
            }, cts.Token);
        }
        catch (Exception ex) { await a.SendTextAsync(link.ChatId, $"⚠ {ex.Message}"); return; }
        finally
        {
            _running.TryRemove(runKey, out _);
            _convToChat.TryRemove(conv.Id, out _);
        }

        // The work behind a team's answer stays in Hearth (it's folded under the answer there).
        var replies = conv.Messages.Skip(firstNew).Where(m => m.Role == ChatRole.Assistant && !m.Behind && !sentEarly.Contains(m.Id)).ToList();
        var allBots = isGroup ? await _bots.ListAsync() : [];
        for (var i = 0; i < replies.Count; i++)
        {
            var reply = replies[i];
            if (reply.Error is not null) { await a.SendTextAsync(link.ChatId, $"⚠ {reply.Error}"); continue; }
            foreach (var img in reply.GeneratedImages) await a.SendImageAsync(link.ChatId, img, null);
            var body = new StringBuilder();
            if (isGroup && allBots.FirstOrDefault(b => b.Id == reply.BotId) is { } author) body.Append($"{author.Emoji} **{author.Name}**\n");
            if (reply.Notice is not null) body.Append($"ℹ {reply.Notice}\n\n");
            // A reply that's nothing but tool calls that never ran says so instead (an answer that merely shows some
            // markup, like a code example, is sent as it is).
            body.Append(reply.Content.Trim().Length > 0 && Llm.TextToolCalls.WithoutMarkup(reply.Content).Length == 0
                ? "(The model wrote tool calls it couldn't run instead of an answer. Try again, or pick a different model.)"
                : reply.Content);
            if (reply.Sources.Count > 0)
                body.Append("\n\n").AppendJoin("\n", reply.Sources.Take(8).Select((s, n) => $"[{n + 1}] {s.Url}"));
            if (body.Length > 0) await a.SendTextAsync(link.ChatId, body.ToString(), i == replies.Count - 1 ? await ReplyButtonsAsync(conv.Id, reply) : null);
            // The documents it made, as files (the cover letter itself, not just a link).
            if (link.SendFiles)
                foreach (var f in await ReplyFiles.CollectAsync(reply, _computer.WorkspaceDirectory))
                    try { await a.SendFileAsync(link.ChatId, f.Name, f.MimeType, f.Data, null); }
                    catch (Exception ex) { await a.SendTextAsync(link.ChatId, $"⚠ Couldn't send {f.Name}: {ex.Message}"); }
            // A voice note gets a spoken answer when the chat asked for that (/voice on).
            if (voiceNote && link.VoiceReplies && reply.Content.Length > 0)
                try { if (await _spoken.SpeakAsync(reply.Content) is { } audio) await a.SendVoiceAsync(link.ChatId, audio.audio, audio.mime); }
                catch (Exception ex) { await a.SendTextAsync(link.ChatId, $"⚠ Couldn't read the reply aloud: {ex.Message}"); }
        }
        // What the answer taught Hearth (with Undo), and offers, once the learning after it is done.
        if (replies.LastOrDefault(r => r.Error is null) is { } last)
            _ = Task.Run(() => AfterReplyAsync(a, link.ChatId, conv.Id, last));
    }

    // ------------------------------------------------------------------ teaching from chat apps

    /// <summary>Short keys for buttons (callback data is limited to 64 bytes): key → (chat, message).</summary>
    private readonly ButtonKeys _keys;
    /// <summary>Chats asked "what was wrong?": chat id → the reply it's about.</summary>
    private readonly ConcurrentDictionary<string, (string conv, string msg, DateTime at)> _awaitingFix = new();

    /// <summary>"/bot hearth" (or the old name) switches back to the assistant itself.</summary>
    private static bool IsPlain(string arg) => arg.Equals("hearth", StringComparison.OrdinalIgnoreCase) || arg.Equals(Bot.PlainId, StringComparison.OrdinalIgnoreCase);

    private Task<string> KeyFor(string convId, string msgId) => _keys.NewAsync(convId, msgId);

    /// <summary>Under an answer: the quick follow-ups, plus 👎 and Save as skill.</summary>
    private async Task<IReadOnlyList<(string label, string data)>> ReplyButtonsAsync(string convId, ChatMessage reply)
    {
        if (reply.Content.Length <= 40) return QuickButtons;
        var key = await KeyFor(convId, reply.Id);
        return [.. QuickButtons, ("👎 Wrong", "w:" + key), ("⚡ Save as skill", "s:" + key)];
    }

    private async Task AfterReplyAsync(IChannelAdapter a, string chatId, string convId, ChatMessage reply)
    {
        try
        {
            await _chat.AfterReplyAsync(reply.Id).WaitAsync(TimeSpan.FromMinutes(4));
            await SendLearnedAsync(a, chatId, convId, reply);
            if (reply.Nudge is { Outcome: null } n)
            {
                var key = await KeyFor(convId, reply.Id);
                await a.SendTextAsync(chatId, n.Kind == Nudge.Routine
                        ? $"💡 You've asked for this {n.Times} times. Make it a routine ({n.When})?"
                        : $"💡 You've asked for this {n.Times} times. Save how it's done as a skill?",
                    [(n.Kind == Nudge.Routine ? "⏰ Make it a routine" : "⚡ Make it a skill", (n.Kind == Nudge.Routine ? "r:" : "s:") + key), ("Not now", "n:" + key)]);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>"🧠 Learned: …" with an Undo button for each (and which is right, when one contradicts an older memory).</summary>
    private async Task SendLearnedAsync(IChannelAdapter a, string chatId, string convId, ChatMessage reply, string? heading = null)
    {
        var items = reply.LearnedItems.Select((item, i) => (item, i)).Where(x => !x.item.Undone).TakeLast(6).ToList();
        if (items.Count == 0) return;
        var key = await KeyFor(convId, reply.Id);
        var text = new StringBuilder(heading ?? "🧠 Learned:");
        var buttons = new List<(string label, string data)>();
        for (var n = 0; n < items.Count; n++)
        {
            text.Append($"\n{n + 1}. {items[n].item.Icon} {items[n].item.Label}");
            buttons.Add(($"↩ Undo {n + 1}", $"u:{key}:{items[n].i}"));
        }
        if (items.FirstOrDefault(x => x.item.HasOpenConflict) is { item: not null } c)
        {
            var at = items.IndexOf(c) + 1;
            text.Append($"\n\n⚠ {at} contradicts what I knew: “{c.item.ConflictText}”. Which is right?");
            buttons.Add(($"New ({at})", $"c:{key}:{c.i}:n"));
            buttons.Add(("What I knew", $"c:{key}:{c.i}:o"));
            buttons.Add(("Both", $"c:{key}:{c.i}:b"));
        }
        await a.SendTextAsync(chatId, text.ToString(), buttons);
    }

    private async Task FixAsync(IChannelAdapter a, ChannelLink link, string convId, string msgId, string feedback)
    {
        try
        {
            if (await _conversations.GetAsync(convId) is not { } conv || conv.Messages.FirstOrDefault(x => x.Id == msgId) is not { } reply)
            {
                await a.SendTextAsync(link.ChatId, "I can't find that reply any more.");
                return;
            }
            await a.TypingAsync(link.ChatId);
            var before = reply.LearnedItems.Count;
            var result = await _loop.CorrectAsync(conv, reply, feedback, () => Task.CompletedTask);
            await a.SendTextAsync(link.ChatId, "✅ " + result, [("🔄 Try again", "q:again")]);
            if (reply.LearnedItems.Skip(before).Any(i => i.HasOpenConflict)) await SendLearnedAsync(a, link.ChatId, convId, reply, "Fixed:");
        }
        catch (Exception ex) { await a.SendTextAsync(link.ChatId, $"⚠ Couldn't fix it: {ex.Message}"); }
    }

    /// <summary>Teaching buttons: u: undo, c: settle a contradiction, w: 👎, s: save as skill, r: make a routine, n: not now, R: undo from the recap.</summary>
    private async Task<ButtonResult?> TeachingButtonAsync(IChannelAdapter a, ChannelLink link, string data)
    {
        var parts = data.Split(':');
        if (parts[0] == "R" && parts.Length == 2 && int.TryParse(parts[1], out var r))
        {
            if (await _keys.RecapAsync(r) is not { } e) return new ButtonResult("That recap has expired: undo it on the What Hearth learned page.", false);
            if (await _recap.FindAsync(e.Conv, e.Msg, e.Index) is not { } found) return new ButtonResult("It's gone already.", false);
            return new ButtonResult(await Safe(() => _chat.UndoLearnedAsync(found.conv, found.item)), false);
        }
        if (parts.Length < 2 || parts[0] is not ("u" or "c" or "w" or "s" or "r" or "n")) return null;
        if (await _keys.FindAsync(parts[1]) is not { } found2) return new ButtonResult("That's expired: open the chat in Hearth to change it.", false);
        if (await _conversations.GetAsync(found2.Conv) is not { } conv || conv.Messages.FirstOrDefault(x => x.Id == found2.Msg) is not { } reply)
            return new ButtonResult("I can't find that reply any more.", true);
        LearnedItem? Item() => parts.Length >= 3 && int.TryParse(parts[2], out var i) && i >= 0 && i < reply.LearnedItems.Count ? reply.LearnedItems[i] : null;
        switch (parts[0])
        {
            case "u":
                return Item() is { } undo ? new ButtonResult(await Safe(() => _chat.UndoLearnedAsync(conv, undo)), false) : new ButtonResult(null, false);
            case "c":
                if (Item() is not { } item || parts.Length < 4) return new ButtonResult(null, false);
                var keep = parts[3] switch { "n" => "new", "o" => "old", _ => "both" };
                return new ButtonResult(await Safe(() => _loop.ResolveConflictAsync(conv, item, keep)), false);
            case "w":
                _awaitingFix[link.ChatId] = (conv.Id, reply.Id, DateTime.UtcNow);
                await a.SendTextAsync(link.ChatId, "👎 What was wrong? Reply in a line and I'll fix what caused it.");
                return new ButtonResult(null, false);
            case "s":
                _ = Task.Run(async () =>
                {
                    await a.TypingAsync(link.ChatId);
                    try
                    {
                        var made = await _chat.SaveReplyAsSkillAsync(conv, reply, () => Task.CompletedTask);
                        if (reply.Nudge is not null) await _loop.NudgeTakenAsync(conv, reply);
                        await a.SendTextAsync(link.ChatId, made.Kind == LearnedItem.NewSkill
                            ? $"📒 Saved {made.Text}. Send {made.Text} any time to have it done the same way."
                            : $"📒 Improved {made.Text}.");
                    }
                    catch (Exception ex) { await a.SendTextAsync(link.ChatId, $"⚠ Couldn't save a skill: {ex.Message}"); }
                });
                return new ButtonResult("Writing the skill…", parts[0] == "s" && reply.Nudge is not null);
            case "r":
                if (reply.Nudge is not { Kind: Nudge.Routine } routine) return new ButtonResult(null, true);
                await _loop.NudgeTakenAsync(conv, reply);
                _ = Task.Run(() => ReplyAsync(a, link, null, routine.RoutineDraft, "button"));
                return new ButtonResult("Setting it up…", true);
            default: // "n"
                await _loop.DismissNudgeAsync(conv, reply);
                return new ButtonResult("OK, I won't offer that again.", true);
        }
    }

    private static async Task<string> Safe(Func<Task<string>> work)
    {
        try { return await work(); }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The weekly recap goes to chats that get routine results, with Undo buttons.</summary>
    private async void OnRecapReady(string text, IReadOnlyList<LearnedEntry> items)
    {
        try
        {
            await _keys.SetRecapAsync(items.Take(8).Select(i => new ButtonKeys.RecapRef(i.ConversationId, i.MessageId, i.Index)));
            var buttons = Enumerable.Range(0, Math.Min(8, items.Count)).Select(i => ($"↩ Undo {i + 1}", $"R:{i}")).ToList();
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Routine, DateTimeOffset.Now)))
                if (_adapters[l.Channel].Running) await _adapters[l.Channel].SendTextAsync(l.ChatId, text, buttons);
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ approvals and notifications

    private async void OnApprovalRequested(PendingApproval p)
    {
        try
        {
            var bot = await _bots.GetAsync(p.BotId ?? "");
            var args = p.ArgumentsJson.Length > 600 ? p.ArgumentsJson[..600] + "…" : p.ArgumentsJson;
            var text = $"🔐 {bot?.Name ?? "Hearth"} wants to run {p.ToolName}\n{args}\n\n(from \"{p.ConversationTitle}\")" +
                       (p.Reason is null ? "" : $"\n⚠ {p.Reason}");
            // Approvals from a Telegram/Discord conversation go to that chat; others go to chats that opted in.
            if (_convToChat.TryGetValue(p.ConversationId, out var origin))
            {
                await _adapters[origin.channel].SendApprovalAsync(origin.chatId, p.Id, text, p.AlwaysAllowFor);
                return;
            }
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Approval, DateTimeOffset.Now)))
                if (_adapters[l.Channel].Running) await _adapters[l.Channel].SendApprovalAsync(l.ChatId, p.Id, text, p.AlwaysAllowFor);
        }
        catch { /* best effort */ }
    }

    /// <summary>Button taps: a:/d: answer approvals; q: are the quick follow-ups under replies.</summary>
    private async Task<ButtonResult> ButtonAsync(IChannelAdapter a, string chatId, string data)
    {
        var link = (await _store.GetAsync()).Links.FirstOrDefault(l => l.Channel == a.Channel && l.ChatId == chatId);
        if (link is null) return new ButtonResult("This chat isn't linked.", false);

        if (data.StartsWith("A:"))
            return new ButtonResult(await _approvals.AllowAlwaysAsync(data[2..])
                ? "✅ Allowed — and won't ask again for this (undo on the Permissions page)" : "Already answered (or it timed out).", true);
        if (data.StartsWith("a:") || data.StartsWith("d:"))
        {
            var allow = data[0] == 'a';
            return new ButtonResult(_approvals.Decide(data[2..], allow) ? (allow ? "✅ Allowed" : "✖ Denied") : "Already answered (or it timed out).", true);
        }
        if (await TeachingButtonAsync(a, link, data) is { } taught) return taught;
        string? prompt = data switch
        {
            "q:again" => null,
            "q:short" => "Make that shorter.",
            "q:more" => "Go into more detail.",
            _ => "",
        };
        if (prompt == "") return new ButtonResult(null, false);
        _ = Task.Run(() => ReplyAsync(a, link, null, prompt, "button"));
        return new ButtonResult(null, true);
    }

    private static string Icon(TaskState s) => s switch
    {
        TaskState.Running => "⏳", TaskState.WaitingForYou => "❓", TaskState.Paused => "⏸", TaskState.Done => "✅", TaskState.Failed => "❌", TaskState.Scheduled => "⏰", _ => "✖",
    };

    /// <summary>
    /// Task progress goes to the chat that started it, or else to chats with task notifications on. In quiet hours
    /// only what can't wait (a question for the user, a failure) is sent; the rest is held until they end.
    /// </summary>
    private async void OnTaskMilestone(AgentTask task, string message)
    {
        try
        {
            var links = (await _store.GetAsync()).Links;
            var targets = task.Origin is { } o && o.Contains(':')
                ? links.Where(l => $"{l.Channel}:{l.ChatId}" == o).ToList()
                : links.Where(l => l.NotifyTasks).ToList();
            if (Sendable(message) is not { } said) return;
            var text = said + (task.State == TaskState.WaitingForYou ? "\n\nReply with /answer your answer" : "");
            var urgent = task.State is TaskState.WaitingForYou or TaskState.Failed;
            var now = DateTimeOffset.Now;
            foreach (var l in targets)
            {
                if (!urgent && l.IsQuiet(now)) await HoldAsync(l, text, now);
                else if (_adapters[l.Channel].Running) await _adapters[l.Channel].SendTextAsync(l.ChatId, text);
            }
        }
        catch { /* best effort */ }
    }

    private Timer? _heldTimer;

    private Task HoldAsync(ChannelLink link, string text, DateTimeOffset now) => _store.UpdateAsync(c =>
    {
        if (c.Links.FirstOrDefault(l => l.Channel == link.Channel && l.ChatId == link.ChatId) is not { } saved) return;
        saved.Held.Add(new HeldMessage(now, text));
        if (saved.Held.Count > ChannelLink.MaxHeld) saved.Held.RemoveRange(0, saved.Held.Count - ChannelLink.MaxHeld);
    });

    /// <summary>Once a chat's quiet hours are over, sends what was held as one message. Runs every minute.</summary>
    public async Task FlushHeldAsync(DateTimeOffset now)
    {
        try
        {
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Held.Count > 0 && !l.IsQuiet(now)).ToList())
            {
                if (!_adapters[l.Channel].Running) continue;
                var held = l.Held.ToList();
                await _adapters[l.Channel].SendTextAsync(l.ChatId, HeldDigest(held));
                await _store.UpdateAsync(c =>
                {
                    if (c.Links.FirstOrDefault(x => x.Channel == l.Channel && x.ChatId == l.ChatId) is { } saved)
                        saved.Held.RemoveAll(h => held.Contains(h));
                });
            }
        }
        catch { /* tried again next minute */ }
    }

    public void Dispose() => _heldTimer?.Dispose();

    /// <summary>The one message that replaces everything held during quiet hours.</summary>
    public static string HeldDigest(IReadOnlyList<HeldMessage> held) =>
        $"🌙 While it was quiet ({held.Count} {(held.Count == 1 ? "update" : "updates")}):\n\n" +
        string.Join("\n\n", held.Select(h => $"{h.At:h:mm tt}: {h.Text}"));

    /// <summary>
    /// Text a bot produced on its own, made fit to send: tool calls it wrote as text but never ran are taken out, and
    /// null when nothing is left (so an empty or half-finished reply never becomes a notification).
    /// </summary>
    private static string? Sendable(string? text) => Llm.TextToolCalls.WithoutMarkup(text) is { Length: > 0 } t ? t : null;

    private async void OnHealthChanged(bool up, string message)
    {
        try
        {
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Health, DateTimeOffset.Now)))
                if (_adapters[l.Channel].Running) await _adapters[l.Channel].SendTextAsync(l.ChatId, message);
        }
        catch { /* best effort */ }
    }

    private async void OnHeartbeatAlert(string conversationId, string alert)
    {
        try
        {
            if (Sendable(alert) is not { } text) return;
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Heartbeat, DateTimeOffset.Now)))
                if (_adapters[l.Channel].Running) await _adapters[l.Channel].SendTextAsync(l.ChatId, "💓 " + text);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// A check-in arrives like any message from the bot: sent plainly and added to the chat that link talks in, so the
    /// reply carries on the conversation with the check-in in view.
    /// </summary>
    private async Task CheckInAsync(Bot bot, string said)
    {
        if (Sendable(said) is not { } text) return;
        foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Routine, DateTimeOffset.Now)))
        {
            if (!_adapters[l.Channel].Running) continue;
            var link = l;
            var key = link.ActiveBotId ?? Bot.PlainId;
            var conv = link.Conversations.TryGetValue(key, out var id) ? await _conversations.GetAsync(id) : null;
            if (conv is null)
            {
                conv = new Conversation { Title = $"📱 {Title(link.Channel)} · {bot.Name}", BotIds = [link.ActiveBotId ?? bot.Id] };
                var newId = conv.Id;
                await _store.UpdateAsync(c => Find(c, link)!.Conversations[key] = newId);
            }
            conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, BotId = bot.Id, Content = text });
            await _conversations.SaveAsync(conv);
            await _adapters[link.Channel].SendTextAsync(link.ChatId, text, QuickButtons);
        }
    }

    private async void OnRoutineFinished(string conversationId)
    {
        try
        {
            var conv = await _conversations.GetAsync(conversationId);
            var reply = conv?.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var trigger = conv?.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.RoutineName;
            if (conv is null || reply is null || trigger is null) return;
            var bot = await _bots.GetAsync(conv.BotIds.FirstOrDefault() ?? "");
            // Each routine can keep its results in Hearth (Bots → bot → Routines → "Send results to chat apps").
            var routine = bot?.Routines.FirstOrDefault(r => r.Name == trigger);
            if (routine is { NotifyChannels: false }) return;
            var content = Sendable(reply.Content);
            // Nothing worth a message: empty, or only calls that never ran.
            if (reply.Error is null && content is null && reply.GeneratedImages.Count == 0) return;
            if (routine is { Conversational: true } && reply.Error is null && content is not null)
            {
                await CheckInAsync(bot!, content);
                return;
            }
            var text = $"⏰ {bot?.Emoji} {bot?.Name ?? "Hearth"} · {trigger}\n\n{(reply.Error is not null ? "⚠ " + reply.Error : content)}";
            foreach (var l in (await _store.GetAsync()).Links.Where(l => l.Wants(ChannelLink.Kind.Routine, DateTimeOffset.Now)))
            {
                if (!_adapters[l.Channel].Running) continue;
                foreach (var img in reply.GeneratedImages) await _adapters[l.Channel].SendImageAsync(l.ChatId, img, null);
                await _adapters[l.Channel].SendTextAsync(l.ChatId, text);
            }
        }
        catch { /* best effort */ }
    }
}
