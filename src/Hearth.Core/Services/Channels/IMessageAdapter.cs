using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Models;

namespace Hearth.Core.Services.Channels;

/// <summary>
/// iMessage on macOS: reads new messages from the Messages database (needs Full Disk Access for the app running
/// Hearth) and sends with AppleScript. Best with the Mac signed in to its own Apple ID, so you can text it.
/// </summary>
public sealed class IMessageAdapter : IChannelAdapter
{
    private readonly TextButtons _buttons = new();
    private CancellationTokenSource? _cts;
    private long _lastRowId;
    public string Channel => "imessage";
    public string? Identity { get; private set; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public int MaxMessageLength => 3000;

    public static string DatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Messages", "chat.db");

    private static async Task<(int code, string output)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(o, e).ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return (p.ExitCode, p.ExitCode == 0 ? o.Result : e.Result);
    }

    private static async Task<JsonArray> QueryAsync(string sql)
    {
        var (code, output) = await RunAsync("/usr/bin/sqlite3", "-readonly", "-json", DatabasePath, sql);
        if (code != 0)
            throw new InvalidOperationException(output.Contains("authorization denied") || output.Contains("unable to open")
                ? "Hearth can't read your Messages. Give Full Disk Access to the app running Hearth (System Settings → Privacy & Security → Full Disk Access), then reconnect."
                : "Messages database: " + output.Trim());
        return output.Trim().Length == 0 ? [] : JsonNode.Parse(output) as JsonArray ?? [];
    }

    public async Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        await StopAsync();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("iMessage works when Hearth runs on a Mac.");
        Error = null;
        var max = await QueryAsync("SELECT MAX(ROWID) AS id FROM message;");
        _lastRowId = max.FirstOrDefault()?["id"]?.GetValue<long>() ?? 0; // only messages from now on
        var account = await RunAsync("/usr/bin/osascript", "-e", "tell application \"Messages\" to get id of 1st account whose service type = iMessage");
        Identity = account.code == 0 ? "iMessage on this Mac" : "iMessage (sign in to Messages first)";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Running = true;
        _ = Task.Run(() => PollAsync(onMessage, onButton, _cts.Token));
    }

    private async Task PollAsync(Func<IncomingMessage, Task> onMessage, Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rows = await QueryAsync(
                    "SELECT m.ROWID AS id, m.text AS text, hex(m.attributedBody) AS body, h.id AS handle, c.guid AS chat, " +
                    "c.style AS style, c.display_name AS name FROM message m " +
                    "LEFT JOIN handle h ON m.handle_id = h.ROWID " +
                    "LEFT JOIN chat_message_join j ON j.message_id = m.ROWID LEFT JOIN chat c ON c.ROWID = j.chat_id " +
                    $"WHERE m.ROWID > {_lastRowId} AND m.is_from_me = 0 ORDER BY m.ROWID LIMIT 50;");
                foreach (var r in rows)
                {
                    _lastRowId = Math.Max(_lastRowId, r?["id"]?.GetValue<long>() ?? 0);
                    var text = r?["text"]?.ToString() is { Length: > 0 } t ? t : DecodeAttributedBody(r?["body"]?.ToString());
                    var handle = r?["handle"]?.ToString();
                    if (string.IsNullOrWhiteSpace(text) || handle is null) continue;
                    var isGroup = r?["style"]?.GetValue<int>() == 43;
                    var chatId = isGroup ? "chat:" + r!["chat"] : handle;
                    if (_buttons.Match(chatId, text) is { } data) { await onButton(chatId, data); continue; }
                    await onMessage(new IncomingMessage(Channel, chatId, handle, handle,
                        isGroup ? (r?["name"]?.ToString() is { Length: > 0 } n ? n : "iMessage group") : "iMessage",
                        text.Trim(), !isGroup, text.Contains("hearth", StringComparison.OrdinalIgnoreCase)));
                }
                Error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Error = ex.Message; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ContinueWith(_ => { });
        }
        Running = false;
    }

    /// <summary>Newer macOS stores message text only in attributedBody (an NSAttributedString "typedstream").</summary>
    public static string? DecodeAttributedBody(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var b = Convert.FromHexString(hex);
        var marker = Encoding.ASCII.GetBytes("NSString");
        var at = b.AsSpan().IndexOf(marker);
        if (at < 0) return null;
        var i = at + marker.Length;
        while (i < b.Length && b[i] != 0x2B) i++; // '+' precedes the string
        if (++i >= b.Length) return null;
        int len;
        switch (b[i])
        {
            case 0x81: if (i + 2 >= b.Length) return null; len = b[i + 1] | (b[i + 2] << 8); i += 3; break;
            case 0x82: if (i + 4 >= b.Length) return null; len = b[i + 1] | (b[i + 2] << 8) | (b[i + 3] << 16) | (b[i + 4] << 24); i += 5; break;
            default: len = b[i]; i += 1; break;
        }
        return len <= 0 || i + len > b.Length ? null : Encoding.UTF8.GetString(b, i, len);
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        Running = false;
        return Task.CompletedTask;
    }

    private static Task SendAsync(string chatId, string script, string payload) =>
        RunAsync("/usr/bin/osascript", "-e", "on run argv", "-e", "tell application \"Messages\"",
            "-e", chatId.StartsWith("chat:")
                ? $"{script} to chat id (item 1 of argv)"
                : $"{script} to participant (item 1 of argv) of (1st account whose service type = iMessage)",
            "-e", "end tell", "-e", "end run", chatId.StartsWith("chat:") ? chatId[5..] : chatId, payload);

    public async Task SendTextAsync(string chatId, string markdown, IReadOnlyList<(string label, string data)>? buttons = null)
    {
        var parts = ChannelText.Split(PlainText.FromMarkdown(markdown), MaxMessageLength).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i];
            // Only the important choices become numbered replies; the "again/shorter" extras would just be noise in SMS.
            if (i == parts.Count - 1 && buttons is { Count: > 0 } && !buttons.All(b => TextButtons.UnderReply(b.data)))
                text += _buttons.Render(chatId, buttons);
            var (code, output) = await RunAsync("/usr/bin/osascript", "-e", "on run argv", "-e", "tell application \"Messages\"",
                "-e", chatId.StartsWith("chat:") ? "send (item 2 of argv) to chat id (item 1 of argv)"
                    : "send (item 2 of argv) to participant (item 1 of argv) of (1st account whose service type = iMessage)",
                "-e", "end tell", "-e", "end run", chatId.StartsWith("chat:") ? chatId[5..] : chatId, text);
            if (code != 0) throw new InvalidOperationException("Sending with Messages failed: " + output.Trim());
        }
    }

    public async Task SendImageAsync(string chatId, ImageAttachment image, string? caption)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Hearth");
        Directory.CreateDirectory(dir); // Messages can only send files from places it may read
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}{(image.MimeType.Contains("jpeg") ? ".jpg" : ".png")}");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(image.Base64));
        await SendAsync(chatId, "send (POSIX file (item 2 of argv))", path);
        if (!string.IsNullOrWhiteSpace(caption)) await SendTextAsync(chatId, caption);
    }

    public async Task SendFileAsync(string chatId, string fileName, string mimeType, byte[] data, string? caption)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Hearth");
        Directory.CreateDirectory(dir); // Messages can only send files from places it may read
        var safe = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(dir, $"{Guid.NewGuid().ToString("N")[..6]}-{(safe.Length > 0 ? safe : "file")}");
        await File.WriteAllBytesAsync(path, data);
        await SendAsync(chatId, "send (POSIX file (item 2 of argv))", path);
        if (!string.IsNullOrWhiteSpace(caption)) await SendTextAsync(chatId, caption);
    }

    public Task SendApprovalAsync(string chatId, string approvalId, string text, string? alwaysFor = null) =>
        SendTextAsync(chatId, text, alwaysFor is null
            ? [("Allow", "a:" + approvalId), ("Deny", "d:" + approvalId)]
            : [("Allow", "a:" + approvalId), ("Deny", "d:" + approvalId), ($"Always allow for {alwaysFor}", "A:" + approvalId)]);

    public Task TypingAsync(string chatId) => Task.CompletedTask;
}
