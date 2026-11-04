using System.Text;
using Locus.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Locus.Core.Services;

public sealed class InboxConfig
{
    public bool Enabled { get; set; }
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = "";
    public string? PasswordSealed { get; set; }
    /// <summary>The address replies come from (default: Username).</summary>
    public string? Address { get; set; }
    /// <summary>Bot that answers (null = Locus).</summary>
    public string? BotId { get; set; }
    /// <summary>Only these senders get answers: full addresses or "@domain.com". Everything else is left unread.</summary>
    public List<string> AllowedSenders { get; set; } = [];
    public int PollMinutes { get; set; } = 2;
    /// <summary>Message-ID → conversation id, so a thread stays one conversation.</summary>
    public Dictionary<string, string> Threads { get; set; } = [];
    public DateTimeOffset? LastCheck { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// "Email the bot": watches a mailbox over IMAP, lets the chosen bot answer mail from allowed senders, and replies
/// over SMTP in the same thread. Replies go through the permission system (area "inbox"), so by default you're
/// asked before anything is sent.
/// </summary>
public sealed class BotInbox(IAppPaths paths, ChatService chat, BotStore bots, ConversationStore conversations, ModelPicker models,
    ApprovalCenter approvals, ActivityLog activity, SettingsStore settingsStore) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SecretBox _box = new(paths);
    private InboxConfig? _cache;
    private Timer? _timer;
    private int _busy;
    private string FilePath => Path.Combine(paths.DataDirectory, "inbox.json");

    public event Action? Changed;

    public async Task<InboxConfig> GetAsync() => _cache ??= await Json.ReadAsync<InboxConfig>(FilePath) ?? new();

    public async Task SaveAsync(InboxConfig config, string? password)
    {
        await _lock.WaitAsync();
        try
        {
            if (password is { Length: > 0 }) config.PasswordSealed = _box.Seal(password);
            _cache = config;
            await Json.WriteAtomicAsync(FilePath, config);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

    private async Task TickAsync()
    {
        var c = await GetAsync();
        if (!c.Enabled || c.LastCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromMinutes(Math.Max(1, c.PollMinutes))) return;
        await CheckNowAsync();
    }

    public static bool IsAllowed(string sender, IReadOnlyList<string> allowed) =>
        allowed.Any(a => a.StartsWith('@')
            ? sender.EndsWith(a, StringComparison.OrdinalIgnoreCase)
            : sender.Equals(a.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Connects, answers new mail from allowed senders, and reports what happened.</summary>
    public async Task<string> CheckNowAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "Already checking.";
        var c = await GetAsync();
        try
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(c.ImapHost, c.ImapPort, SecureSocketOptions.Auto, ct);
            await imap.AuthenticateAsync(c.Username, _box.Open(c.PasswordSealed ?? ""), ct);
            var inbox = imap.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
            var answered = 0;
            foreach (var uid in uids.Take(10))
            {
                var msg = await inbox.GetMessageAsync(uid, ct);
                var from = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                if (!IsAllowed(from, c.AllowedSenders))
                {
                    await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Channel, $"Bot inbox: ignored email from {from}", msg.Subject, c.BotId, null, true));
                    await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                    continue;
                }
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
                if (await AnswerAsync(c, msg, ct)) answered++;
            }
            await imap.DisconnectAsync(true, ct);
            c.LastError = null;
            return uids.Count == 0 ? "No new email." : $"{uids.Count} new, {answered} answered.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (c.LastError != ex.Message)
                await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Error, "Bot inbox can't check mail", ex.Message, c.BotId, null, false));
            c.LastError = ex.Message;
            return $"Couldn't check mail: {ex.Message}";
        }
        finally
        {
            c.LastCheck = DateTimeOffset.Now;
            await SaveAsync(c, null);
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<bool> AnswerAsync(InboxConfig c, MimeMessage msg, CancellationToken ct)
    {
        var bot = c.BotId is null ? null : await bots.GetAsync(c.BotId);
        var model = await models.PickAsync(bot) ?? throw new InvalidOperationException("No model available.");

        // Keep a thread in one conversation.
        var threadKey = msg.References.Concat([msg.InReplyTo]).Where(r => r is not null).Select(r => r!).FirstOrDefault(r => c.Threads.ContainsKey(r));
        var conv = threadKey is not null ? await conversations.GetAsync(c.Threads[threadKey]) : null;
        conv ??= new Conversation { Title = "📮 " + (msg.Subject ?? "(no subject)"), BotIds = bot is null ? [] : [bot.Id] };

        var body = msg.TextBody ?? (msg.HtmlBody is { } html ? TextExtractor.FromHtml(html).text : "");
        var from = msg.From.ToString();
        conv.Messages.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = $"Email from {from}\nSubject: {msg.Subject}\n\n{(body.Length > 12000 ? body[..12000] + "…" : body)}\n\n" +
                      "(Write the reply email body only — no subject line. It will be sent from the bot's mailbox.)",
        });
        await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Channel, $"Bot inbox: email from {from}", msg.Subject, bot?.Id, conv.Id));
        var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true), () => Task.CompletedTask, ct, autoTitle: false);
        if (reply is null || reply.Error is not null || reply.Content.Length == 0) return false;

        // Ask before sending unless allowed on the Permissions page.
        var settings = await settingsStore.GetAsync();
        var level = settings.Permissions.GetValueOrDefault(PermissionCatalog.Key("inbox", true), PermissionLevel.Default);
        if (level == PermissionLevel.Block) return false;
        if (level != PermissionLevel.Allow)
        {
            var call = new ToolCall { Name = "inbox_reply", ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { to = from, subject = "Re: " + msg.Subject, body = reply.Content }) };
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            approvals.Register(conv, bot, call, decision);
            bool ok;
            try { ok = await decision.Task.WaitAsync(TimeSpan.FromHours(24), ct); }
            catch (TimeoutException) { ok = false; }
            if (!ok) { reply.Notice = "Reply not sent (declined or no answer within 24 hours)."; await conversations.SaveAsync(conv); return false; }
        }

        var out_ = new MimeMessage();
        out_.From.Add(new MailboxAddress(bot?.Name ?? "Locus", c.Address is { Length: > 0 } a ? a : c.Username));
        out_.To.AddRange(msg.ReplyTo.Count > 0 ? msg.ReplyTo : msg.From);
        out_.Subject = msg.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true ? msg.Subject : "Re: " + msg.Subject;
        if (msg.MessageId is not null)
        {
            out_.InReplyTo = msg.MessageId;
            foreach (var r in msg.References) out_.References.Add(r);
            out_.References.Add(msg.MessageId);
        }
        out_.Body = new TextPart("plain") { Text = reply.Content };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(c.SmtpHost, c.SmtpPort, SecureSocketOptions.Auto, ct);
        await smtp.AuthenticateAsync(c.Username, _box.Open(c.PasswordSealed ?? ""), ct);
        await smtp.SendAsync(out_, ct);
        await smtp.DisconnectAsync(true, ct);

        foreach (var id in new[] { msg.MessageId, out_.MessageId }.Where(x => x is not null)) c.Threads[id!] = conv.Id;
        reply.Notice = $"Sent as an email reply to {from}.";
        await conversations.SaveAsync(conv);
        await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Tool, $"{bot?.Name ?? "Locus"}: replied to {from}", out_.Subject, bot?.Id, conv.Id));
        return true;
    }

    public void Dispose() => _timer?.Dispose();
}
