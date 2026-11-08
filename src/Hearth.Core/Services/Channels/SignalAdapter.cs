using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;

namespace Hearth.Core.Services.Channels;

/// <summary>
/// Signal through signal-cli-rest-api (a small Docker container that holds the linked Signal account).
/// The saved token is "http://host:8080|+15551234567" (API address | the bot's Signal number).
/// </summary>
public sealed class SignalAdapter(IHttpClientFactory http) : IChannelAdapter
{
    private readonly TextButtons _buttons = new();
    private string _api = "";
    private string _number = "";
    private CancellationTokenSource? _cts;
    public string Channel => "signal";
    public string? Identity { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public int MaxMessageLength => 3500;

    private HttpClient Client => http.CreateClient(Llm.LlmClientFactory.HttpClientName);

    public static (string api, string number) SplitToken(string token)
    {
        var p = token.Split('|', 2, StringSplitOptions.TrimEntries);
        if (p.Length != 2 || !Uri.TryCreate(p[0], UriKind.Absolute, out _) || !p[1].StartsWith('+'))
            throw new InvalidOperationException("Signal needs the bridge address (e.g. http://localhost:8080) and the number in +country format.");
        return (p[0].TrimEnd('/'), p[1]);
    }

    public async Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        await StopAsync();
        (_api, _number) = SplitToken(token);
        Error = null;
        using (var res = await Client.GetAsync($"{_api}/v1/about", ct))
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Signal bridge at {_api} answered {(int)res.StatusCode}.");
        Identity = _number;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Running = true;
        _ = Task.Run(() => PollAsync(onMessage, onButton, _cts.Token));
    }

    private async Task PollAsync(Func<IncomingMessage, Task> onMessage, Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var items = await Client.GetFromJsonAsync<JsonArray>($"{_api}/v1/receive/{Uri.EscapeDataString(_number)}?timeout=20", ct) ?? [];
                foreach (var item in items)
                    if (Parse(item) is { } msg)
                    {
                        if (_buttons.Match(msg.chatId, msg.text) is { } data) { await onButton(msg.chatId, data); continue; }
                        var files = new List<IncomingFile>();
                        foreach (var a in msg.attachments)
                        {
                            if (a?["id"]?.ToString() is not { } id) continue;
                            var bytes = await Client.GetByteArrayAsync($"{_api}/v1/attachments/{id}", ct);
                            var mime = a["contentType"]?.ToString() ?? "application/octet-stream";
                            files.Add(new IncomingFile(a["filename"]?.ToString() ?? id, mime, bytes, IsVoice: mime.StartsWith("audio/")));
                        }
                        await onMessage(new IncomingMessage(Channel, msg.chatId, msg.userId, msg.userName, msg.isGroup ? "Signal group" : "Signal",
                            msg.text, !msg.isGroup, msg.text.Contains("hearth", StringComparison.OrdinalIgnoreCase), files));
                    }
                Error = null;
                backoff = 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Error = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, backoff *= 2)), ct).ContinueWith(_ => { });
            }
        }
        Running = false;
    }

    /// <summary>A received envelope as (chat, sender, text, attachments), or null for receipts, typing and our own messages.</summary>
    public static (string chatId, string userId, string userName, string text, bool isGroup, JsonArray attachments)? Parse(JsonNode? item)
    {
        var env = item?["envelope"];
        var data = env?["dataMessage"];
        if (data is null) return null;
        var text = data["message"]?.ToString() ?? "";
        var attachments = data["attachments"] as JsonArray ?? [];
        if (text.Length == 0 && attachments.Count == 0) return null;
        var user = env!["sourceNumber"]?.ToString() ?? env["source"]?.ToString() ?? env["sourceUuid"]?.ToString();
        if (user is null) return null;
        var group = data["groupInfo"]?["groupId"]?.ToString();
        var chat = group is null ? user : "group." + Convert.ToBase64String(Encoding.UTF8.GetBytes(group));
        return (chat, user, env["sourceName"]?.ToString() is { Length: > 0 } n ? n : user, text, group is not null, attachments);
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        Running = false;
        return Task.CompletedTask;
    }

    private async Task PostAsync(JsonObject body)
    {
        body["number"] = _number;
        using var res = await Client.PostAsync($"{_api}/v2/send", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Signal send failed: {await res.Content.ReadAsStringAsync()}");
    }

    public async Task SendTextAsync(string chatId, string markdown, IReadOnlyList<(string label, string data)>? buttons = null)
    {
        var parts = ChannelText.Split(PlainText.FromMarkdown(markdown), MaxMessageLength).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i];
            if (i == parts.Count - 1 && buttons is { Count: > 0 } && !buttons.All(b => TextButtons.UnderReply(b.data)))
                text += _buttons.Render(chatId, buttons);
            await PostAsync(new JsonObject { ["message"] = text, ["recipients"] = new JsonArray(chatId) });
        }
    }

    public Task SendImageAsync(string chatId, ImageAttachment image, string? caption) =>
        PostAsync(new JsonObject
        {
            ["message"] = caption ?? "", ["recipients"] = new JsonArray(chatId),
            ["base64_attachments"] = new JsonArray($"data:{image.MimeType};base64,{image.Base64}"),
        });

    public Task SendFileAsync(string chatId, string fileName, string mimeType, byte[] data, string? caption) =>
        PostAsync(new JsonObject
        {
            ["message"] = caption ?? "", ["recipients"] = new JsonArray(chatId),
            ["base64_attachments"] = new JsonArray($"data:{mimeType};filename={fileName};base64,{Convert.ToBase64String(data)}"),
        });

    public Task SendApprovalAsync(string chatId, string approvalId, string text, string? alwaysFor = null) =>
        SendTextAsync(chatId, text, alwaysFor is null
            ? [("Allow", "a:" + approvalId), ("Deny", "d:" + approvalId)]
            : [("Allow", "a:" + approvalId), ("Deny", "d:" + approvalId), ($"Always allow for {alwaysFor}", "A:" + approvalId)]);

    public async Task TypingAsync(string chatId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{_api}/v1/typing-indicator/{Uri.EscapeDataString(_number)}")
            { Content = new StringContent(new JsonObject { ["recipient"] = chatId }.ToJsonString(), Encoding.UTF8, "application/json") };
            using var _ = await Client.SendAsync(req);
        }
        catch { /* cosmetic */ }
    }
}
