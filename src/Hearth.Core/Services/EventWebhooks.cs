using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>A URL Hearth calls when something happens (for Home Assistant, n8n, Zapier, your own scripts…).</summary>
public sealed class OutgoingWebhook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Webhook";
    public string Url { get; set; } = "";
    /// <summary>Signs each call (X-Hearth-Signature: sha256=HMAC of the body) so the receiver can check it's Hearth.</summary>
    public string? Secret { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Which events it gets (see <see cref="EventWebhooks.Events"/>); empty = all.</summary>
    public List<string> Events { get; set; } = [];
}

/// <summary>
/// Calls your webhooks when approvals are needed, the heartbeat finds something, a task or routine reaches a
/// milestone, a bot follows up, or something goes wrong: a JSON POST with the event, a title and the text.
/// </summary>
public sealed class EventWebhooks(SettingsStore settingsStore, IHttpClientFactory http, ApprovalCenter approvals, HeartbeatRunner heartbeat,
    TaskRunner tasks, RoutineRunner routines, FollowUpRunner followUps, ActivityLog activity, BotStore bots)
{
    public static readonly (string key, string label)[] Events =
    [
        ("approval", "A bot needs your OK"),
        ("heartbeat", "Heartbeat found something"),
        ("task", "Task milestones (plan, question, done, failed)"),
        ("routine", "A routine finished"),
        ("follow_up", "A bot followed up"),
        ("problem", "Something went wrong"),
    ];

    private bool _started;
    private DateTimeOffset _lastProblem;
    /// <summary>Last result per webhook id (for the settings page).</summary>
    public ConcurrentDictionary<string, string> LastResult { get; } = new();

    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        approvals.Requested += p => _ = Task.Run(async () =>
        {
            var bot = p.BotId is null ? null : await bots.GetAsync(p.BotId);
            await FireAsync("approval", $"{bot?.Name ?? "Hearth"} needs your OK",
                $"Wants to run {p.ToolName.Replace('_', ' ')}{(p.Reason is null ? "" : " — " + p.Reason)}", p.ConversationId);
        });
        heartbeat.Alert += (conv, text) => _ = FireAsync("heartbeat", "Hearth checked in", text, conv);
        tasks.Milestone += (task, text) => _ = FireAsync("task", $"Task: {task.Title}", text, task.ConversationId,
            new() { ["taskId"] = task.Id, ["state"] = task.State.ToString() });
        routines.RoutineFinished += conv => _ = FireAsync("routine", "Routine finished", "A routine has new results.", conv);
        followUps.Posted += (conv, text) => _ = FireAsync("follow_up", "A bot followed up", text, conv);
        activity.Added += a =>
        {
            if (a.Ok || a.Kind is not ActivityKind.Error || DateTimeOffset.Now - _lastProblem < TimeSpan.FromMinutes(10)) return;
            _lastProblem = DateTimeOffset.Now;
            _ = FireAsync("problem", "Hearth has a problem", a.Title + (a.Detail is null ? "" : $": {a.Detail}"), a.ConversationId);
        };
    }

    /// <summary>Sends an event to every enabled webhook that wants it.</summary>
    public async Task FireAsync(string ev, string title, string text, string? conversationId, Dictionary<string, string>? extra = null)
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            var hooks = settings.Webhooks.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Url) && (h.Events.Count == 0 || h.Events.Contains(ev))).ToList();
            if (hooks.Count == 0) return;
            var open = settings.Notifications.OpenUrl?.TrimEnd('/');
            var payload = new Dictionary<string, object?>
            {
                ["event"] = ev, ["title"] = title, ["text"] = text, ["conversationId"] = conversationId,
                ["link"] = open is null ? null : conversationId is null ? open : $"{open}/c/{conversationId}",
                ["at"] = DateTimeOffset.Now,
            };
            foreach (var (k, v) in extra ?? []) payload[k] = v;
            var body = JsonSerializer.Serialize(payload);
            await Task.WhenAll(hooks.Select(h => SendAsync(h, ev, body)));
        }
        catch { /* webhooks are best effort */ }
    }

    /// <summary>Posts to one webhook. Returns null on success, otherwise what went wrong.</summary>
    public async Task<string?> SendAsync(OutgoingWebhook hook, string ev, string body)
    {
        string? problem;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("X-Hearth-Event", ev);
            req.Headers.UserAgent.ParseAdd("Hearth/1.0");
            if (!string.IsNullOrEmpty(hook.Secret)) req.Headers.TryAddWithoutValidation("X-Hearth-Signature", "sha256=" + Sign(hook.Secret, body));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req, cts.Token);
            problem = res.IsSuccessStatusCode ? null : $"answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { problem = ex is TaskCanceledException ? "timed out" : ex.Message; }
        LastResult[hook.Id] = $"{DateTimeOffset.Now:MMM d h:mm tt}: {ev} — {problem ?? "OK"}";
        return problem;
    }

    public static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
