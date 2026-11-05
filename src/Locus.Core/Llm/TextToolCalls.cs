using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Locus.Core.Llm;

/// <summary>
/// Local models sometimes write their tool calls as text instead of using the server's tool-call format
/// (<c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c>, <c>&lt;function_calls&gt;[…]</c>, a ```json block…). Without this the
/// reply just ends with the calls printed and nothing runs. This finds them so they run like real calls.
/// </summary>
public static partial class TextToolCalls
{
    [GeneratedRegex(@"<(tool_call|function_calls?|tool_calls)>\s*(?<body>[\s\S]*?)\s*(?:</\1>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Tagged();

    [GeneratedRegex(@"```(?:json|tool_call)?\s*(?<body>[\[{][\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex Fenced();

    /// <summary>
    /// Tool calls written as text in <paramref name="text"/> naming one of <paramref name="toolNames"/>, and the text with
    /// them removed. Returns no calls when nothing parses or a name isn't a known tool (so ordinary JSON in an
    /// answer is left alone).
    /// </summary>
    public static (List<LlmToolCall> calls, string rest) Find(string text, IReadOnlySet<string> toolNames)
    {
        if (string.IsNullOrEmpty(text) || toolNames.Count == 0) return ([], text);
        foreach (var pattern in new[] { Tagged(), Fenced() })
        {
            var calls = new List<LlmToolCall>();
            var rest = pattern.Replace(text, m =>
            {
                var found = Parse(m.Groups["body"].Value, toolNames);
                if (found.Count == 0) return m.Value;
                calls.AddRange(found);
                return "";
            });
            if (calls.Count > 0) return (calls, rest.Trim());
        }
        return ([], text);
    }

    private static List<LlmToolCall> Parse(string body, IReadOnlySet<string> toolNames)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body.Trim()); }
        catch { node = TryRepair(body); }
        var items = node switch
        {
            JsonArray a => a.ToList(),
            JsonObject o when o["tool_calls"] is JsonArray a => a.ToList(),
            JsonObject o => [o],
            _ => [],
        };
        var calls = new List<LlmToolCall>();
        foreach (var item in items.OfType<JsonObject>())
        {
            var fn = item["function"] as JsonObject ?? item;
            var name = fn["name"]?.GetValue<string>();
            if (name is null || !toolNames.Contains(name)) return []; // not a tool call: leave the text alone
            var args = fn["arguments"] ?? fn["parameters"] ?? fn["input"] ?? new JsonObject();
            var json = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args.ToJsonString();
            calls.Add(new LlmToolCall("text_" + Guid.NewGuid().ToString("N")[..10], name, json));
        }
        return calls;
    }

    /// <summary>Cut-off output: keep the complete objects of an unfinished array.</summary>
    private static JsonNode? TryRepair(string body)
    {
        var t = body.Trim();
        if (!t.StartsWith('[')) return null;
        for (var end = t.LastIndexOf('}'); end > 0; end = t.LastIndexOf('}', end - 1))
        {
            try { return JsonNode.Parse(t[..(end + 1)] + "]"); }
            catch { }
        }
        return null;
    }
}
