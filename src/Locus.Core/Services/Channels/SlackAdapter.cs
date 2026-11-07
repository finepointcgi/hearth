using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Models;

namespace Locus.Core.Services.Channels;

/// <summary>
/// Slack app via Socket Mode (no public URL needed). The saved token is "xapp-…|xoxb-…":
/// the app-level token opens the socket, the bot token sends messages. Answers in DMs and when @mentioned.
/// </summary>
public sealed partial class SlackAdapter(IHttpClientFactory http) : IChannelAdapter
{
    private string _appToken = "";
    private string _botToken = "";
    private string _api = "https://slack.com/api";
    private string? _myId;
    private CancellationTokenSource? _cts;
    public string Channel => "slack";
    public string? Identity { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public int MaxMessageLength => 3500;

    /// <summary>Splits the stored "xapp|xoxb" pair.</summary>
    public static (string app, string bot) SplitToken(string token)
    {
        var parts = token.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !parts[0].StartsWith("xapp-") || !parts[1].StartsWith("xoxb-"))
            throw new InvalidOperationException("Slack needs both tokens: the app-level token (xapp-…) and the bot token (xoxb-…).");
        return (parts[0], parts[1]);
    }

    private async Task<JsonNode> CallAsync(string method, JsonObject? body, string? token = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/{method}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _botToken);
        req.Content = new StringContent((body ?? []).ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(text.Length == 0 ? "{}" : text) ?? new JsonObject();
        if (node["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"Slack {method}: {node["error"]?.ToString() ?? ((int)res.StatusCode).ToString()}");
        return node;
    }

    public async Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        await StopAsync();
        (_appToken, _botToken) = SplitToken(token);
        if (!string.IsNullOrWhiteSpace(apiBase)) _api = apiBase.TrimEnd('/');
        Error = null;
        var me = await CallAsync("auth.test", null, ct: ct);
        _myId = me["user_id"]?.ToString();
        Identity = "@" + me["user"];
        await CallAsync("apps.connections.open", null, _appToken, ct); // fail fast on a bad app token
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Running = true;
        _ = Task.Run(() => SocketLoopAsync(onMessage, onButton, _cts.Token));
    }

    private async Task SocketLoopAsync(Func<IncomingMessage, Task> onMessage, Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                var url = (await CallAsync("apps.connections.open", null, _appToken, ct))["url"]!.ToString();
                await ws.ConnectAsync(new Uri(url), ct);
                Error = null;
                backoff = 1;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var env = await ReceiveAsync(ws, ct);
                    if (env is null) break;
                    if (env["envelope_id"]?.ToString() is { } id)
                        await ws.SendAsync(Encoding.UTF8.GetBytes(new JsonObject { ["envelope_id"] = id }.ToJsonString()), WebSocketMessageType.Text, true, ct);
                    var type = env["type"]?.ToString();
                    if (type == "disconnect") break;
                    if (type == "events_api" && ParseEvent(env["payload"]?["event"]) is { } ev)
                        _ = Task.Run(() => DeliverAsync(ev, onMessage, ct), ct);
                    else if (type == "interactive" && env["payload"] is { } p && p["type"]?.ToString() == "block_actions"
                             && p["actions"]?[0]?["value"]?.ToString() is { Length: > 0 } value)
                        _ = Task.Run(() => ButtonAsync(p, value, onButton, ct), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Error = ex.Message; }
            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, backoff *= 2)), ct).ContinueWith(_ => { });
        }
        Running = false;
    }

    public sealed record SlackEvent(string ChatId, string UserId, string Text, bool IsDm, bool Mentioned, JsonArray? Files);

    /// <summary>Turns a Slack event into a message for Locus, or null for anything Locus should ignore.</summary>
    public SlackEvent? ParseEvent(JsonNode? e)
    {
        if (e is null || e["bot_id"] is not null || e["user"]?.ToString() is not { } user || user == _myId) return null;
        var subtype = e["subtype"]?.ToString();
        if (subtype is not (null or "file_share")) return null; // edits, joins, deletions…
        var type = e["type"]?.ToString();
        var isDm = e["channel_type"]?.ToString() == "im";
        // app_mention covers channels; plain message events only for DMs, so mentions aren't handled twice.
        if (type == "message" && !isDm) return null;
        if (type is not ("message" or "app_mention")) return null;
        var text = e["text"]?.ToString() ?? "";
        var mentioned = type == "app_mention" || (_myId is not null && text.Contains($"<@{_myId}>"));
        if (_myId is not null) text = text.Replace($"<@{_myId}>", "");
        return new SlackEvent(e["channel"]!.ToString(), user, FromMrkdwn(text).Trim(), isDm, mentioned, e["files"] as JsonArray);
    }

    private async Task DeliverAsync(SlackEvent ev, Func<IncomingMessage, Task> onMessage, CancellationToken ct)
    {
        try
        {
            var files = new List<IncomingFile>();
            foreach (var f in ev.Files ?? [])
            {
                if ((f?["size"]?.GetValue<long>() ?? 0) > 20_000_000 || f?["url_private_download"]?.ToString() is not { } url) continue;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
                using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) continue;
                var mime = f["mimetype"]?.ToString() ?? "application/octet-stream";
                files.Add(new IncomingFile(f["name"]?.ToString() ?? "file", mime, await res.Content.ReadAsByteArrayAsync(ct),
                    IsVoice: mime.StartsWith("audio/") || f["subtype"]?.ToString() == "slack_audio"));
            }
            var name = ev.UserId;
            try { name = (await CallAsync("users.info", new JsonObject { ["user"] = ev.UserId }, ct: ct))["user"]?["real_name"]?.ToString() ?? name; }
            catch { /* users:read scope is optional */ }
            await onMessage(new IncomingMessage(Channel, ev.ChatId, ev.UserId, name, ev.IsDm ? "Slack DM" : "Slack channel",
                ev.Text, ev.IsDm, ev.Mentioned, files));
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private async Task ButtonAsync(JsonNode p, string value, Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        try
        {
            var result = await onButton(p["channel"]?["id"]?.ToString() ?? "", value);
            if (!result.ClearButtons || p["response_url"]?.ToString() is not { } responseUrl) return;
            var original = p["message"]?["text"]?.ToString() ?? "";
            var body = new JsonObject { ["replace_original"] = true, ["text"] = original + (result.Text is null ? "" : $"\n*{result.Text}*") };
            await http.CreateClient(Llm.LlmClientFactory.HttpClientName).PostAsync(responseUrl,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        Running = false;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ sending

    private static JsonArray Blocks(string mrkdwn, IReadOnlyList<(string label, string data, string? style)>? buttons)
    {
        var blocks = new JsonArray(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = mrkdwn } });
        if (buttons is { Count: > 0 })
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray(buttons.Take(5).Select((b, i) =>
                {
                    var el = new JsonObject
                    {
                        ["type"] = "button", ["action_id"] = $"locus_{i}",
                        ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = b.label }, ["value"] = b.data,
                    };
                    if (b.style is not null) el["style"] = b.style;
                    return (JsonNode)el;
                }).ToArray()),
            });
        return blocks;
    }

    public async Task SendTextAsync(string chatId, string markdown, IReadOnlyList<(string label, string data)>? buttons = null)
    {
        var parts = ChannelText.Split(ToMrkdwn(markdown), MaxMessageLength).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var withButtons = i == parts.Count - 1 && buttons is { Count: > 0 };
            await CallAsync("chat.postMessage", new JsonObject
            {
                ["channel"] = chatId, ["text"] = parts[i],
                ["blocks"] = Blocks(parts[i], withButtons ? buttons!.Select(b => (b.label, b.data, (string?)null)).ToList() : null),
            });
        }
    }

    public Task SendImageAsync(string chatId, ImageAttachment image, string? caption) =>
        SendFileAsync(chatId, image.FileName.Contains('.') ? image.FileName : image.FileName + ".png", image.MimeType, Convert.FromBase64String(image.Base64), caption);

    public async Task SendFileAsync(string chatId, string name, string mimeType, byte[] bytes, string? caption)
    {
        // Uses form fields, as Slack documents for this method.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["filename"] = name, ["length"] = bytes.Length.ToString() });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_api}/files.getUploadURLExternal") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        var client = http.CreateClient(Llm.LlmClientFactory.HttpClientName);
        using var res = await client.SendAsync(req);
        var up = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (up?["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"Slack upload: {up?["error"]}");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new(mimeType);
        (await client.PostAsync(up["upload_url"]!.ToString(), content)).EnsureSuccessStatusCode();
        var done = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["id"] = up["file_id"]!.ToString(), ["title"] = name }),
            ["channel_id"] = chatId,
        };
        if (!string.IsNullOrWhiteSpace(caption)) done["initial_comment"] = ToMrkdwn(caption);
        await CallAsync("files.completeUploadExternal", done);
    }

    public Task SendApprovalAsync(string chatId, string approvalId, string text, string? alwaysFor = null)
    {
        var body = ToMrkdwn(text);
        if (body.Length > MaxMessageLength) body = body[..MaxMessageLength];
        return CallAsync("chat.postMessage", new JsonObject
        {
            ["channel"] = chatId, ["text"] = body,
            ["blocks"] = Blocks(body, alwaysFor is null
                ? [("Allow", "a:" + approvalId, "primary"), ("Deny", "d:" + approvalId, "danger")]
                : [("Allow", "a:" + approvalId, "primary"), ("Deny", "d:" + approvalId, "danger"), ($"Always allow for {alwaysFor}", "A:" + approvalId, null)]),
        });
    }

    public Task TypingAsync(string chatId) => Task.CompletedTask; // Slack has no typing indicator for bots

    // ------------------------------------------------------------------ formatting

    /// <summary>Converts common Markdown to Slack mrkdwn.</summary>
    public static string ToMrkdwn(string md)
    {
        var s = md.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        s = Link().Replace(s, m => $"<{m.Groups[2].Value}|{m.Groups[1].Value}>");
        s = Heading().Replace(s, m => $"*{m.Groups[1].Value.Trim()}*");
        s = Bold().Replace(s, m => $"*{m.Groups[1].Value}*");
        s = Strike().Replace(s, m => $"~{m.Groups[1].Value}~");
        s = Bullet().Replace(s, "$1• ");
        return s;
    }

    /// <summary>Turns Slack's escaped text and link syntax back into plain text.</summary>
    public static string FromMrkdwn(string text)
    {
        var s = SlackLink().Replace(text, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.Multiline)] private static partial Regex Heading();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"~~(.+?)~~")] private static partial Regex Strike();
    [GeneratedRegex(@"^(\s*)[-*]\s+", RegexOptions.Multiline)] private static partial Regex Bullet();
    [GeneratedRegex(@"<((?:https?|mailto):[^|>]+)(?:\|([^>]+))?>")] private static partial Regex SlackLink();
}
