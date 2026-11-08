using System.Text;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// Keeps what's sent to the model inside its context window: long chats are summarised (the summary is saved on
/// the chat and grows as the chat does), older replies' tool details are dropped, and within a long reply older
/// tool results are shortened. Token counts are estimates (about 3 characters per token, on the safe side).
/// </summary>
public static class ContextWindow
{
    /// <summary>Share of the budget the recent messages may use once a chat is summarised.</summary>
    public const double RecentShare = 0.5;
    private const int ReserveTokens = 600;

    public static int Tokens(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 3 + 1;

    public static int Tokens(LlmMessage m) =>
        8 + Tokens(m.Content) + m.ToolCalls.Sum(c => Tokens(c.ArgumentsJson) + Tokens(c.Name) + 8) + m.Images.Count * 1200
        + Tokens(m.ProviderState) / 2;

    public static int Tokens(IEnumerable<LlmMessage> messages) => messages.Sum(Tokens);

    /// <summary>The model's context window: the server's setting if given, else a sensible default for its kind.</summary>
    public static int WindowFor(ProviderProfile p, AppSettings s) => p.ContextTokens is > 1000 and var t ? t : p.Kind switch
    {
        ProviderKind.Ollama => Math.Max(2048, s.ContextLength),
        ProviderKind.Anthropic or ProviderKind.ClaudeCode => 200_000,
        _ => 32_768,
    };

    /// <summary>Tokens available for the prompt (window minus room for the answer and a margin).</summary>
    public static int BudgetFor(ProviderProfile p, AppSettings s) =>
        Math.Max(1500, WindowFor(p, s) - Math.Max(512, s.MaxTokens > 0 ? s.MaxTokens : 2048) - ReserveTokens);

    /// <summary>
    /// Where the recent part of a chat starts so that it fits in <paramref name="tokens"/>: always at least the
    /// last two messages, and preferably starting at a user message so a question isn't cut from its answer.
    /// </summary>
    public static int RecentStart(IReadOnlyList<ChatMessage> history, Func<ChatMessage, int> cost, int tokens)
    {
        var start = history.Count;
        var used = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var c = cost(history[i]);
            if (used + c > tokens && history.Count - i > 2) break;
            used += c;
            start = i;
        }
        // Prefer to begin at a user message.
        for (var i = start; i < history.Count - 1; i++)
            if (history[i].Role == ChatRole.User) return i;
        return start;
    }

    /// <summary>A tool result cut to <paramref name="chars"/>, saying how much was left out.</summary>
    public static string ClipResult(string text, int chars) => text.Length <= chars
        ? text
        : text[..chars] + $"\n… [shortened to fit the context window: {text.Length - chars} more characters]";

    /// <summary>Share of the budget a long reply is brought back down to when it overflows.</summary>
    public const double RefitShare = 0.7;

    /// <summary>
    /// Makes room in a long reply by shortening older tool results for good (oldest first, newest few kept whole),
    /// freeing at least <paramref name="tokensToFree"/>. Shortening well below the limit in one go, rather than one
    /// result per step, keeps the prompt growing only at the end for the next several steps, which lets local model
    /// servers reuse what they already read instead of reading the whole prompt again each time.
    /// </summary>
    public static void ShortenOlderResults(IReadOnlyList<ToolCall> calls, int tokensToFree, int keepWhole = 3, int chars = 800)
    {
        var freed = 0;
        foreach (var tc in calls.Take(Math.Max(0, calls.Count - keepWhole)))
        {
            if (freed >= tokensToFree) break;
            if (tc.SentChars is not null || tc.Result is not { } r || r.Length <= chars) continue;
            freed += Tokens(r) - Tokens(ClipResult(r, chars));
            tc.SentChars = chars;
        }
    }

    /// <summary>
    /// Shortens older tool results (oldest first) until the messages fit, keeping the newest few whole.
    /// </summary>
    public static List<LlmMessage> FitToolResults(List<LlmMessage> messages, int budget, int keepWhole = 3, int minChars = 800)
    {
        var total = Tokens(messages);
        if (total <= budget) return messages;
        var toolIdx = messages.Select((m, i) => (m, i)).Where(x => x.m.Role == "tool").Select(x => x.i).ToList();
        var shrinkable = toolIdx.Take(Math.Max(0, toolIdx.Count - keepWhole)).ToList();
        var result = messages.ToList();
        foreach (var i in shrinkable)
        {
            if (total <= budget) break;
            var m = result[i];
            if (m.Content.Length <= minChars) continue;
            var clipped = m.Content[..minChars] + $"\n… [shortened to fit the context window: {m.Content.Length - minChars} more characters]";
            total -= Tokens(m.Content) - Tokens(clipped);
            result[i] = new LlmMessage { Role = m.Role, Content = clipped, ToolCallId = m.ToolCallId, ToolName = m.ToolName, Images = [] };
        }
        // Still too big (huge recent results): shorten the kept ones too, harder.
        foreach (var i in toolIdx)
        {
            if (total <= budget) break;
            var m = result[i];
            var limit = Math.Max(400, minChars / 2);
            if (m.Content.Length <= limit) continue;
            var clipped = m.Content[..limit] + "\n… [shortened to fit the context window]";
            total -= Tokens(m.Content) - Tokens(clipped);
            result[i] = new LlmMessage { Role = m.Role, Content = clipped, ToolCallId = m.ToolCallId, ToolName = m.ToolName, Images = [] };
        }
        return result;
    }

    /// <summary>The text a summary is made from: earlier summary plus the messages being folded in, each clipped.</summary>
    public static string TranscriptForSummary(string? earlierSummary, IEnumerable<ChatMessage> messages, Func<string?, string> botName, int maxTokens)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(earlierSummary)) sb.AppendLine("Summary so far:").AppendLine(earlierSummary.Trim()).AppendLine();
        var lines = new List<string>();
        foreach (var m in messages)
        {
            if (m.Content.Length == 0 && m.ToolCalls.Count == 0) continue;
            var who = m.Role == ChatRole.User ? (m.RoutineName is { } r ? $"[{r}]" : "User") : botName(m.BotId);
            var text = Clip(m.Content, 1500);
            var tools = m.ToolCalls.Count == 0 ? "" :
                " (tools: " + string.Join("; ", m.ToolCalls.Take(8).Select(t => $"{t.Name} → {Clip((t.Result ?? "").ReplaceLineEndings(" "), 160)}")) + ")";
            lines.Add($"{who}: {text}{tools}");
        }
        // Over budget: keep the newest lines (older ones are already in the earlier summary, or matter least).
        var budgetChars = Math.Max(2000, maxTokens * 3) - sb.Length;
        var kept = new List<string>();
        var used = 0;
        for (var i = lines.Count - 1; i >= 0 && used + lines[i].Length < budgetChars; i--)
        {
            kept.Insert(0, lines[i]);
            used += lines[i].Length + 1;
        }
        if (kept.Count < lines.Count) sb.AppendLine($"[{lines.Count - kept.Count} older messages not shown]");
        sb.AppendLine("Conversation:").AppendJoin('\n', kept);
        return sb.ToString();
    }

    public const string SummaryInstructions =
        "You compress conversations so an assistant can continue them without the full history. Write a summary " +
        "(at most 350 words, bullet points) covering: what the user wants and their constraints; facts learned about " +
        "the user; decisions made; work done and key results (keep names, numbers, links and file paths exactly); " +
        "open questions; and anything the assistant promised to do next. If an earlier summary is given, merge it in. " +
        "Output only the summary.";

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
