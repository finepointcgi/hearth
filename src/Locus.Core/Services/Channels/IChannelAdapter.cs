using Locus.Core.Models;

namespace Locus.Core.Services.Channels;

/// <summary>A file sent with a message (photo, document or voice note), already downloaded.</summary>
public sealed record IncomingFile(string Name, string MimeType, byte[] Data, bool IsVoice = false);

public sealed record IncomingMessage(string Channel, string ChatId, string UserId, string UserName, string ChatName, string Text,
    bool IsPrivate, bool MentionsMe, IReadOnlyList<IncomingFile>? Files = null);

/// <summary>What to do after a button tap: an optional short confirmation, and whether to remove the buttons.</summary>
public sealed record ButtonResult(string? Text, bool ClearButtons);

public interface IChannelAdapter
{
    string Channel { get; }
    /// <summary>The bot's own name on the platform (e.g. @my_locus_bot), once connected.</summary>
    string? Identity { get; }
    bool Running { get; }
    string? Error { get; }
    int MaxMessageLength { get; }

    /// <param name="onButton">(chatId, button data) — called when someone taps a button Locus sent.</param>
    Task StartAsync(string token, string? apiBase, Func<IncomingMessage, Task> onMessage,
        Func<string, string, Task<ButtonResult>> onButton, CancellationToken ct);
    Task StopAsync();
    /// <summary>Sends text; <paramref name="buttons"/> (label, data) are attached under the last part.</summary>
    Task SendTextAsync(string chatId, string markdown, IReadOnlyList<(string label, string data)>? buttons = null);
    Task SendImageAsync(string chatId, ImageAttachment image, string? caption);
    Task SendApprovalAsync(string chatId, string approvalId, string text);
    Task TypingAsync(string chatId);
}

internal static class ChannelText
{
    /// <summary>Splits long text on paragraph/line boundaries.</summary>
    public static IEnumerable<string> Split(string text, int max)
    {
        while (text.Length > max)
        {
            var cut = text.LastIndexOf("\n\n", max, StringComparison.Ordinal);
            if (cut < max / 2) cut = text.LastIndexOf('\n', max);
            if (cut < max / 2) cut = text.LastIndexOf(' ', max);
            if (cut < max / 2) cut = max;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
