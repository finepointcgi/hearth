using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Hearth.Core.Services;

/// <summary>Phone notifications through ntfy (ntfy.sh or your own server; the ntfy app subscribes to a secret topic).</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "https://ntfy.sh";
    /// <summary>The secret topic name: anyone who knows it can read the notifications, so it's long and random.</summary>
    public string Topic { get; set; } = NewTopic();
    /// <summary>Access token for a protected self-hosted ntfy server (encrypted on disk).</summary>
    public string? Token { get; set; }
    /// <summary>Include message details (otherwise just a short title, so nothing private leaves Hearth).</summary>
    public bool IncludeDetails { get; set; }
    /// <summary>Where tapping a notification goes (your Hearth address as seen from the phone).</summary>
    public string? OpenUrl { get; set; }
    public bool Approvals { get; set; } = true;
    public bool Heartbeat { get; set; } = true;
    public bool Tasks { get; set; } = true;
    public bool Routines { get; set; }
    /// <summary>A bot came back to a chat with something new (follow_up).</summary>
    public bool FollowUps { get; set; } = true;
    public bool Problems { get; set; } = true;
    /// <summary>The weekly "what Hearth learned" (when that's on in Teach Hearth).</summary>
    public bool Recap { get; set; } = true;

    public static string NewTopic() => "hearth-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
}

public sealed class PushNotifier(SettingsStore settingsStore, IHttpClientFactory http, ApprovalCenter approvals, HeartbeatRunner heartbeat,
    TaskRunner tasks, RoutineRunner routines, ActivityLog activity, BotStore bots, FollowUpRunner followUps, LearningRecap recap)
{
    private bool _started;
    private DateTimeOffset _lastProblem;

    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        approvals.Requested += p => _ = OnApprovalAsync(p);
        heartbeat.Alert += (conv, text) => _ = SendIfAsync(s => s.Heartbeat, "Hearth checked in", text, "heartbeat", conv, priority: 4);
        tasks.Milestone += (task, text) => _ = SendIfAsync(s => s.Tasks, $"Task: {task.Title}", text, "dart", task.ConversationId);
        followUps.Posted += (conv, text) => _ = SendIfAsync(s => s.FollowUps, "A bot followed up", text, "alarm_clock", conv);
        routines.RoutineFinished += conv => _ = SendIfAsync(s => s.Routines, "Routine finished", "A routine has new results.", "alarm_clock", conv);
        recap.Ready += (text, items) => _ = SendIfAsync(s => s.Recap, "What Hearth learned this week", text, "brain", null);
        activity.Added += a =>
        {
            if (a.Ok || a.Kind is not (ActivityKind.Error) || DateTimeOffset.Now - _lastProblem < TimeSpan.FromMinutes(10)) return;
            _lastProblem = DateTimeOffset.Now; // at most one problem notification every 10 minutes
            _ = SendIfAsync(s => s.Problems, "Hearth has a problem", a.Title, "warning", a.ConversationId);
        };
    }

    private async Task OnApprovalAsync(PendingApproval p)
    {
        var bot = p.BotId is null ? null : await bots.GetAsync(p.BotId);
        await SendIfAsync(s => s.Approvals, $"{bot?.Name ?? "Hearth"} needs your OK",
            $"Wants to run {p.ToolName.Replace('_', ' ')}{(p.Reason is null ? "" : " — " + p.Reason)}", "lock", p.ConversationId, priority: 4);
    }

    private async Task SendIfAsync(Func<NotificationSettings, bool> wanted, string title, string detail, string tag, string? conversationId,
        int priority = 3)
    {
        try
        {
            var s = (await settingsStore.GetAsync()).Notifications;
            if (!s.Enabled || !wanted(s)) return;
            await SendAsync(s, title, s.IncludeDetails ? detail : "Open Hearth to see more.", tag, conversationId, priority);
        }
        catch { /* notifications are best effort */ }
    }

    /// <summary>Sends one notification. Returns null on success, otherwise the problem.</summary>
    public async Task<string?> SendAsync(NotificationSettings s, string title, string message, string tag = "bell", string? conversationId = null,
        int priority = 3)
    {
        if (string.IsNullOrWhiteSpace(s.Topic)) return "Set a topic first.";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{s.Server.TrimEnd('/')}/{Uri.EscapeDataString(s.Topic)}")
        {
            Content = new StringContent(message.Length > 3500 ? message[..3500] + "…" : message, Encoding.UTF8),
        };
        // Header values must be ASCII; ntfy decodes RFC 2047 encoded titles.
        req.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title)) + "?=");
        req.Headers.TryAddWithoutValidation("Tags", tag);
        req.Headers.TryAddWithoutValidation("Priority", priority.ToString());
        if (!string.IsNullOrWhiteSpace(s.OpenUrl))
            req.Headers.TryAddWithoutValidation("Click", conversationId is null ? s.OpenUrl.TrimEnd('/') : $"{s.OpenUrl.TrimEnd('/')}/c/{conversationId}");
        if (!string.IsNullOrWhiteSpace(s.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        try
        {
            using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req);
            return res.IsSuccessStatusCode ? null : $"The ntfy server answered {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex) { return "Couldn't reach the ntfy server: " + ex.Message; }
    }
}
