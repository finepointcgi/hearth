using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Locus.Core.Models;

namespace Locus.Core.Services.Channels;

/// <summary>Telegram Bot API over long polling — no public URL or port forwarding needed.</summary>
public sealed partial class TelegramAdapter(IHttpClientFactory http) : IChannelAdapter
{
    private string _base = "";
    private string _fileBase = "";
    private CancellationTokenSource? _cts;
    public string Channel => "telegram";
    public string? Identity { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public int MaxMessageLength => 3900;

    private HttpClient Http => http.CreateClient(Llm.LlmClientFactory.HttpClientName);

    private async Task<JsonNode?> CallAsync(string method, JsonObject? body = null, CancellationToken ct = default)
    {
        using var res = body is null
            ? await Http.GetAsync($"{_base}/{method}", ct)
            : await Http.PostAsJsonAsync($"{_base}/{method}", body, ct);
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (json?["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"Telegram: {json?["description"] ?? res.ReasonPhrase}");
        return json["result"];
    }

    public async Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        await StopAsync();
        var root = (apiBase ?? "https://api.telegram.org").TrimEnd('/');
        _base = $"{root}/bot{token}";
        _fileBase = $"{root}/file/bot{token}";
        Error = null;
        var me = await CallAsync("getMe", ct: ct);
        Identity = "@" + me?["username"];
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Running = true;
        _ = Task.Run(() => PollAsync(onMessage, onButton, _cts.Token));
    }

    /// <summary>Downloads a file the user sent (Bot API limit: 20 MB).</summary>
    private async Task<byte[]?> DownloadAsync(string fileId, CancellationToken ct)
    {
        var file = await CallAsync("getFile", new JsonObject { ["file_id"] = fileId }, ct);
        var path = file?["file_path"]?.ToString();
        return path is null ? null : await Http.GetByteArrayAsync($"{_fileBase}/{path}", ct);
    }

    private async Task<List<IncomingFile>> FilesAsync(JsonObject m, CancellationToken ct)
    {
        var files = new List<IncomingFile>();
        if (m["photo"] is JsonArray sizes && sizes.LastOrDefault()?["file_id"]?.ToString() is { } photoId
            && await DownloadAsync(photoId, ct) is { } photo)
            files.Add(new IncomingFile("photo.jpg", "image/jpeg", photo));
        if (m["document"] is JsonObject doc && doc["file_id"]?.ToString() is { } docId
            && (doc["file_size"]?.GetValue<long>() ?? 0) <= 20_000_000 && await DownloadAsync(docId, ct) is { } data)
            files.Add(new IncomingFile(doc["file_name"]?.ToString() ?? "file", doc["mime_type"]?.ToString() ?? "application/octet-stream", data));
        foreach (var kind in new[] { "voice", "audio" })
            if (m[kind] is JsonObject v && v["file_id"]?.ToString() is { } vId && await DownloadAsync(vId, ct) is { } audio)
                files.Add(new IncomingFile(kind == "voice" ? "voice.ogg" : v["file_name"]?.ToString() ?? "audio",
                    v["mime_type"]?.ToString() ?? "audio/ogg", audio, IsVoice: true));
        return files;
    }

    private async Task PollAsync(Func<IncomingMessage, Task> onMessage, Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        long offset = 0;
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await CallAsync("getUpdates", new JsonObject
                {
                    ["offset"] = offset, ["timeout"] = 25,
                    ["allowed_updates"] = new JsonArray("message", "callback_query"),
                }, ct) as JsonArray ?? [];
                Error = null;
                backoff = 1;
                foreach (var u in updates)
                {
                    offset = Math.Max(offset, u!["update_id"]!.GetValue<long>() + 1);
                    if (u["message"] is JsonObject m && (m["text"] ?? m["caption"] ?? m["photo"] ?? m["document"] ?? m["voice"] ?? m["audio"]) is not null)
                    {
                        var chat = m["chat"]!;
                        var from = m["from"];
                        var isPrivate = chat["type"]?.ToString() == "private";
                        var text = m["text"]?.ToString() ?? m["caption"]?.ToString() ?? "";
                        var hasFiles = (m["photo"] ?? m["document"] ?? m["voice"] ?? m["audio"]) is not null;
                        _ = Task.Run(async () =>
                        {
                            var files = hasFiles ? await FilesAsync(m, ct) : [];
                            await onMessage(new IncomingMessage(Channel, chat["id"]!.ToString(), from?["id"]?.ToString() ?? "",
                                from?["first_name"]?.ToString() ?? from?["username"]?.ToString() ?? "someone",
                                chat["title"]?.ToString() ?? from?["first_name"]?.ToString() ?? "Telegram chat",
                                text, isPrivate, Identity is not null && text.Contains(Identity, StringComparison.OrdinalIgnoreCase), files));
                        }, ct);
                    }
                    else if (u["callback_query"] is JsonObject cb && cb["data"]?.ToString() is { Length: > 2 } data)
                    {
                        var chatId = cb["message"]?["chat"]?["id"]?.ToString() ?? "";
                        var messageId = cb["message"]?["message_id"]?.GetValue<long>();
                        var result = await onButton(chatId, data);
                        await CallAsync("answerCallbackQuery", new JsonObject { ["callback_query_id"] = cb["id"]!.ToString(), ["text"] = result.Text ?? "" }, ct);
                        if (messageId is not null && result.ClearButtons)
                            await CallAsync("editMessageReplyMarkup", new JsonObject { ["chat_id"] = chatId, ["message_id"] = messageId, ["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() } }, ct);
                        if (messageId is not null && result.ClearButtons && result.Text is not null)
                            await CallAsync("sendMessage", new JsonObject { ["chat_id"] = chatId, ["text"] = result.Text, ["reply_to_message_id"] = messageId }, ct);
                    }
                }
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
            var msg = new JsonObject { ["chat_id"] = chatId, ["text"] = ToHtml(parts[i]), ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true };
            if (i == parts.Count - 1 && buttons is { Count: > 0 })
                msg["reply_markup"] = new JsonObject
                {
                    // Rows of three, so labels stay readable on a phone.
                    ["inline_keyboard"] = new JsonArray(buttons.Chunk(3).Select(row =>
                        (JsonNode)new JsonArray(row.Select(b => (JsonNode)new JsonObject { ["text"] = b.label, ["callback_data"] = b.data }).ToArray())).ToArray()),
                };
            try { await CallAsync("sendMessage", msg); }
            catch (InvalidOperationException)
            {
                msg["text"] = parts[i];
                msg.Remove("parse_mode");
                await CallAsync("sendMessage", msg);
            }
        }
    }

    public async Task SendImageAsync(string chatId, ImageAttachment image, string? caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        if (caption is not null) form.Add(new StringContent(caption.Length > 1000 ? caption[..1000] : caption), "caption");
        var bytes = new ByteArrayContent(Convert.FromBase64String(image.Base64));
        bytes.Headers.ContentType = new(image.MimeType);
        form.Add(bytes, "photo", image.FileName.Contains('.') ? image.FileName : image.FileName + ".png");
        using var res = await Http.PostAsync($"{_base}/sendPhoto", form);
    }

    public async Task SendFileAsync(string chatId, string fileName, string mimeType, byte[] data, string? caption) =>
        await UploadAsync("sendDocument", "document", chatId, fileName, mimeType, data, caption);

    public async Task SendVoiceAsync(string chatId, byte[] audio, string mimeType)
    {
        if (mimeType.Contains("ogg")) await UploadAsync("sendVoice", "voice", chatId, "reply.ogg", mimeType, audio, null);
        else await UploadAsync("sendAudio", "audio", chatId, "reply.mp3", mimeType, audio, null);
    }

    private async Task UploadAsync(string method, string field, string chatId, string fileName, string mimeType, byte[] data, string? caption)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        if (caption is not null) form.Add(new StringContent(caption.Length > 1000 ? caption[..1000] : caption), "caption");
        var bytes = new ByteArrayContent(data);
        bytes.Headers.ContentType = new(mimeType);
        form.Add(bytes, field, fileName);
        using var res = await Http.PostAsync($"{_base}/{method}", form);
        var json = await res.Content.ReadFromJsonAsync<JsonObject>();
        if (json?["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"Telegram: {json?["description"] ?? res.ReasonPhrase}");
    }

    public Task SendApprovalAsync(string chatId, string approvalId, string text, string? alwaysFor = null)
    {
        var row = new JsonArray(
            new JsonObject { ["text"] = "✅ Allow", ["callback_data"] = "a:" + approvalId },
            new JsonObject { ["text"] = "✖ Deny", ["callback_data"] = "d:" + approvalId });
        var keyboard = new JsonArray(row);
        if (alwaysFor is not null)
            keyboard.Add(new JsonArray(new JsonObject { ["text"] = $"♾ Always allow for {alwaysFor}", ["callback_data"] = "A:" + approvalId }));
        return CallAsync("sendMessage", new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["reply_markup"] = new JsonObject { ["inline_keyboard"] = keyboard },
        });
    }

    public Task TypingAsync(string chatId) =>
        CallAsync("sendChatAction", new JsonObject { ["chat_id"] = chatId, ["action"] = "typing" }).ContinueWith(_ => { });

    /// <summary>Markdown → the small HTML subset Telegram accepts (b, i, code, pre, a).</summary>
    public static string ToHtml(string md)
    {
        var parts = CodeBlock().Split(md);
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) { sb.Append("<pre>").Append(WebUtility.HtmlEncode(parts[i].Trim('\n'))).Append("</pre>"); continue; }
            var t = WebUtility.HtmlEncode(parts[i]);
            t = InlineCode().Replace(t, "<code>$1</code>");
            t = Bold().Replace(t, "<b>$1</b>");
            t = Link().Replace(t, "<a href=\"$2\">$1</a>");
            t = Heading().Replace(t, "<b>$1</b>");
            sb.Append(t);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"```[a-zA-Z0-9#+.-]*\n?([\s\S]*?)```")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex Bold();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")] private static partial Regex Link();
    [GeneratedRegex(@"(?m)^#{1,6}\s+(.+)$")] private static partial Regex Heading();
}
