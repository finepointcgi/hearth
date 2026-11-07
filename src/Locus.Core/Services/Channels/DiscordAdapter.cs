using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Services.Channels;

/// <summary>
/// Discord bot via the Gateway WebSocket (receive) and REST (send). Answers in DMs, and in server channels
/// when mentioned. Needs the "Message Content" intent enabled in the Discord Developer Portal.
/// </summary>
public sealed class DiscordAdapter(IHttpClientFactory http) : IChannelAdapter
{
    private const int Intents = (1 << 0) | (1 << 9) | (1 << 12) | (1 << 15); // guilds, guild msgs, DMs, message content
    private string _token = "";
    private string _api = "https://discord.com/api/v10";
    private string? _myId;
    private CancellationTokenSource? _cts;
    public string Channel => "discord";
    public string? Identity { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public int MaxMessageLength => 1900;

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, _api + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bot", _token);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<JsonNode?> CallAsync(HttpMethod m, string path, JsonNode? body = null, CancellationToken ct = default)
    {
        using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(Req(m, path, body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord: {(int)res.StatusCode} {(text.Length > 200 ? text[..200] : text)}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        await StopAsync();
        _token = token;
        if (!string.IsNullOrWhiteSpace(apiBase)) _api = apiBase.TrimEnd('/');
        Error = null;
        var me = await CallAsync(HttpMethod.Get, "/users/@me", ct: ct);
        _myId = me?["id"]?.ToString();
        Identity = me?["username"]?.ToString();
        var gateway = (await CallAsync(HttpMethod.Get, "/gateway/bot", ct: ct))?["url"]?.ToString() ?? "wss://gateway.discord.gg";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Running = true;
        _ = Task.Run(() => GatewayLoopAsync(gateway, onMessage, onButton, _cts.Token));
    }

    private async Task GatewayLoopAsync(string gateway, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            long? seq = null;
            try
            {
                await ws.ConnectAsync(new Uri($"{gateway}/?v=10&encoding=json"), ct);
                var hello = await ReceiveAsync(ws, ct);
                var interval = hello?["d"]?["heartbeat_interval"]?.GetValue<int>() ?? 41250;
                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(async () =>
                {
                    while (!hbCts.IsCancellationRequested)
                    {
                        await Task.Delay(interval, hbCts.Token);
                        await SendAsync(ws, new JsonObject { ["op"] = 1, ["d"] = seq }, hbCts.Token);
                    }
                }, hbCts.Token);
                await SendAsync(ws, new JsonObject
                {
                    ["op"] = 2,
                    ["d"] = new JsonObject
                    {
                        ["token"] = _token, ["intents"] = Intents,
                        ["properties"] = new JsonObject { ["os"] = Environment.OSVersion.Platform.ToString(), ["browser"] = "locus", ["device"] = "locus" },
                    },
                }, ct);
                Error = null;
                backoff = 1;

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var msg = await ReceiveAsync(ws, ct);
                    if (msg is null) break;
                    if (msg["s"] is JsonValue s && s.TryGetValue<long>(out var n)) seq = n;
                    var op = msg["op"]?.GetValue<int>();
                    if (op is 7 or 9) break; // reconnect / invalid session
                    if (op != 0) continue;
                    var t = msg["t"]?.ToString();
                    var d = msg["d"];
                    var hasAttachments = d?["attachments"] is JsonArray { Count: > 0 };
                    if (t == "MESSAGE_CREATE" && d?["author"]?["bot"]?.GetValue<bool>() != true
                        && (d?["content"]?.ToString() is { Length: > 0 } || hasAttachments) && d!["content"]?.ToString() is { } content)
                    {
                        var isDm = d["guild_id"] is null;
                        var mentions = _myId is not null && (content.Contains($"<@{_myId}>") || content.Contains($"<@!{_myId}>"));
                        var clean = content.Replace($"<@{_myId}>", "").Replace($"<@!{_myId}>", "").Trim();
                        var attachments = d["attachments"] as JsonArray;
                        _ = Task.Run(async () =>
                        {
                            var files = new List<IncomingFile>();
                            foreach (var a in attachments ?? [])
                            {
                                if ((a?["size"]?.GetValue<long>() ?? 0) > 20_000_000 || a?["url"]?.ToString() is not { } url) continue;
                                var mime = a["content_type"]?.ToString() ?? "application/octet-stream";
                                var bytes = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).GetByteArrayAsync(url, ct);
                                files.Add(new IncomingFile(a["filename"]?.ToString() ?? "file", mime, bytes,
                                    IsVoice: mime.StartsWith("audio/") || ((a["flags"]?.GetValue<int>() ?? 0) & 8192) != 0));
                            }
                            await onMessage(new IncomingMessage(Channel, d["channel_id"]!.ToString(), d["author"]!["id"]!.ToString(),
                                d["author"]!["global_name"]?.ToString() ?? d["author"]!["username"]!.ToString(),
                                isDm ? "Discord DM" : "Discord channel", clean, isDm, mentions, files));
                        }, ct);
                    }
                    else if (t == "INTERACTION_CREATE" && d?["data"]?["custom_id"]?.ToString() is { Length: > 2 } custom)
                    {
                        var result = await onButton(d["channel_id"]?.ToString() ?? "", custom);
                        await CallAsync(HttpMethod.Post, $"/interactions/{d["id"]}/{d["token"]}/callback", result.ClearButtons
                            ? new JsonObject
                            {
                                ["type"] = 7, // update the message: remove buttons, show the outcome
                                ["data"] = new JsonObject { ["content"] = (d["message"]?["content"]?.ToString() ?? "") + (result.Text is null ? "" : $"\n**{result.Text}**"), ["components"] = new JsonArray() },
                            }
                            : result.Text is { Length: > 0 } said
                                ? new JsonObject { ["type"] = 4, ["data"] = new JsonObject { ["content"] = said, ["flags"] = 64 } } // just for the tapper
                                : new JsonObject { ["type"] = 6 }, ct); // acknowledge silently
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Error = ex.Message; }
            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, backoff *= 2)), ct).ContinueWith(_ => { });
        }
        Running = false;
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

    private static Task SendAsync(ClientWebSocket ws, JsonNode payload, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        Running = false;
        await Task.CompletedTask;
    }

    public async Task SendTextAsync(string chatId, string markdown, IReadOnlyList<(string label, string data)>? buttons = null)
    {
        var parts = ChannelText.Split(markdown, MaxMessageLength).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var msg = new JsonObject { ["content"] = parts[i] };
            if (i == parts.Count - 1 && buttons is { Count: > 0 })
                msg["components"] = new JsonArray(buttons.Take(25).Chunk(5).Select(row => (JsonNode)new JsonObject
                {
                    ["type"] = 1,
                    ["components"] = new JsonArray(row.Select(b => (JsonNode)new JsonObject { ["type"] = 2, ["style"] = 2, ["label"] = b.label, ["custom_id"] = b.data }).ToArray()),
                }).ToArray());
            await CallAsync(HttpMethod.Post, $"/channels/{chatId}/messages", msg);
        }
    }

    public async Task SendImageAsync(string chatId, ImageAttachment image, string? caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(new JsonObject { ["content"] = caption ?? "" }.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(Convert.FromBase64String(image.Base64));
        file.Headers.ContentType = new(image.MimeType);
        form.Add(file, "files[0]", image.FileName.Contains('.') ? image.FileName : image.FileName + ".png");
        var req = Req(HttpMethod.Post, $"/channels/{chatId}/messages");
        req.Content = form;
        using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req);
    }

    public async Task SendFileAsync(string chatId, string fileName, string mimeType, byte[] data, string? caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(new JsonObject { ["content"] = caption ?? "" }.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new(mimeType);
        form.Add(file, "files[0]", fileName);
        var req = Req(HttpMethod.Post, $"/channels/{chatId}/messages");
        req.Content = form;
        using var res = await http.CreateClient(Llm.LlmClientFactory.HttpClientName).SendAsync(req);
    }

    public Task SendApprovalAsync(string chatId, string approvalId, string text, string? alwaysFor = null)
    {
        var buttons = new JsonArray(
            new JsonObject { ["type"] = 2, ["style"] = 3, ["label"] = "Allow", ["custom_id"] = "a:" + approvalId },
            new JsonObject { ["type"] = 2, ["style"] = 4, ["label"] = "Deny", ["custom_id"] = "d:" + approvalId });
        if (alwaysFor is not null)
            buttons.Add(new JsonObject { ["type"] = 2, ["style"] = 2, ["label"] = $"Always allow for {alwaysFor}"[..Math.Min(80, $"Always allow for {alwaysFor}".Length)], ["custom_id"] = "A:" + approvalId });
        return CallAsync(HttpMethod.Post, $"/channels/{chatId}/messages", new JsonObject
        {
            ["content"] = text.Length > MaxMessageLength ? text[..MaxMessageLength] : text,
            ["components"] = new JsonArray(new JsonObject { ["type"] = 1, ["components"] = buttons }),
        });
    }

    public Task TypingAsync(string chatId) =>
        CallAsync(HttpMethod.Post, $"/channels/{chatId}/typing").ContinueWith(_ => { });
}
