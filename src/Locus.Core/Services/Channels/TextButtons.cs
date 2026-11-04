using System.Collections.Concurrent;

namespace Locus.Core.Services.Channels;

/// <summary>
/// For channels without tappable buttons (iMessage, Signal): buttons become "Reply 1 Allow · 2 Deny", and a reply
/// with the number or the label counts as tapping it.
/// </summary>
public sealed class TextButtons
{
    private readonly ConcurrentDictionary<string, List<(string label, string data)>> _pending = new();

    public string Render(string chatId, IReadOnlyList<(string label, string data)> buttons)
    {
        _pending[chatId] = buttons.ToList();
        return "\n\nReply " + string.Join(" · ", buttons.Select((b, i) => $"{i + 1} {b.label}"));
    }

    /// <summary>The button data when <paramref name="text"/> answers the last buttons sent to this chat.</summary>
    public string? Match(string chatId, string text)
    {
        if (!_pending.TryGetValue(chatId, out var buttons)) return null;
        var t = text.Trim().TrimEnd('.', '!').ToLowerInvariant();
        var hit = int.TryParse(t, out var n) && n >= 1 && n <= buttons.Count ? buttons[n - 1]
            : buttons.FirstOrDefault(b => b.label.ToLowerInvariant().Trim() == t || StripEmoji(b.label) == t);
        if (hit == default) return null;
        _pending.TryRemove(chatId, out _);
        return hit.data;
    }

    private static string StripEmoji(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim().ToLowerInvariant();
}

internal static class PlainText
{
    /// <summary>Markdown → readable plain text for SMS-like channels.</summary>
    public static string FromMarkdown(string md)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(md, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1 ($2)");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        s = s.Replace("**", "").Replace("__", "").Replace("```", "").Replace("`", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"^(\s*)[-*]\s+", "$1• ", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
    }
}
