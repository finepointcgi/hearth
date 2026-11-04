using System.Text;

namespace Locus.Core.Llm;

/// <summary>
/// Splits a streamed text into "thinking" and "answer" parts for models that inline
/// their reasoning as &lt;think&gt;...&lt;/think&gt; (DeepSeek-R1 distills, QwQ, etc.).
/// Handles tags split across chunk boundaries.
/// </summary>
public sealed class ThinkTagParser
{
    private const string Open = "<think>";
    private const string Close = "</think>";
    private readonly StringBuilder _pending = new();
    private bool _inThink;

    public (string content, string thinking) Push(string chunk)
    {
        _pending.Append(chunk);
        var content = new StringBuilder();
        var thinking = new StringBuilder();

        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _inThink ? Close : Open;
            var idx = text.IndexOf(tag, StringComparison.Ordinal);
            if (idx >= 0)
            {
                (_inThink ? thinking : content).Append(text, 0, idx);
                _pending.Remove(0, idx + tag.Length);
                _inThink = !_inThink;
                continue;
            }

            // Keep a possible partial tag at the end for the next chunk.
            var keep = PartialSuffix(text, tag);
            (_inThink ? thinking : content).Append(text, 0, text.Length - keep);
            _pending.Remove(0, text.Length - keep);
            break;
        }
        return (content.ToString(), thinking.ToString());
    }

    public (string content, string thinking) Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return _inThink ? ("", rest) : (rest, "");
    }

    private static int PartialSuffix(string text, string tag)
    {
        for (var len = Math.Min(tag.Length - 1, text.Length); len > 0; len--)
            if (text.EndsWith(tag[..len], StringComparison.Ordinal)) return len;
        return 0;
    }
}
