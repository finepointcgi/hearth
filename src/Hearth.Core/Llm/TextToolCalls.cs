using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hearth.Core.Llm;

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
        foreach (var pattern in new[] { Tagged(), Fenced(), Invokes() })
        {
            var calls = new List<LlmToolCall>();
            var rest = pattern.Replace(text, m =>
            {
                var found = Parse(m.Groups["body"].Value, toolNames);
                if (found.Count == 0) return m.Value;
                calls.AddRange(found);
                return "";
            });
            if (calls.Count > 0) return (Distinct(calls), rest.Trim());
        }
        return ([], text);
    }

    /// <summary>The same call written several times in a row (it happens) runs once.</summary>
    private static List<LlmToolCall> Distinct(List<LlmToolCall> calls) =>
        calls.DistinctBy(c => (c.Name, Normalize(c.ArgumentsJson))).ToList();

    private static string Normalize(string json)
    {
        try { return JsonNode.Parse(json)?.ToJsonString() ?? json; }
        catch { return json; }
    }

    [GeneratedRegex(@"<(?:tool_call|function_calls?|tool_calls|invoke|parameter)\b|<function=|</(?:invoke|parameter|function|tool_call|function_calls)>", RegexOptions.IgnoreCase)]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?:<(tool_call|function_calls?|tool_calls)>[\s\S]*?(?:</\1>|$))|(?:<invoke\b[\s\S]*?(?:</invoke>|$))|(?:<function=[\s\S]*?(?:</function>|$))|</?(?:tool_call|function_calls?|tool_calls)>", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupBlock();

    /// <summary>True when <paramref name="text"/> still holds tool-call markup that never ran.</summary>
    public static bool HasMarkup(string? text) => !string.IsNullOrEmpty(text) && Markup().IsMatch(text);

    /// <summary>
    /// <paramref name="text"/> without any tool-call markup left in it (calls that never ran), so a half-finished
    /// reply isn't sent to someone as a message. Empty when there was nothing else.
    /// </summary>
    public static string WithoutMarkup(string? text) =>
        string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(MarkupBlock().Replace(text, ""), @"\n{3,}", "\n\n").Trim();

    // Claude-style XML, with or without a <function_calls> wrapper:
    // <invoke name="tool"><parameter name="key">value</parameter></invoke>
    [GeneratedRegex(@"(?<body>(?:<invoke\s+name=""[\w.-]+""\s*>[\s\S]*?(?:</invoke>|$)\s*)+)", RegexOptions.IgnoreCase)]
    private static partial Regex Invokes();

    [GeneratedRegex(@"<invoke\s+name=""(?<name>[\w.-]+)""\s*>(?<params>[\s\S]*?)(?:</invoke>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InvokeCall();

    [GeneratedRegex(@"<parameter\s+name=""(?<key>[\w.-]+)""\s*>(?<value>[\s\S]*?)(?:</parameter>|(?=<parameter\s)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InvokeParameter();

    private static List<LlmToolCall> ParseInvokes(string body, IReadOnlySet<string> toolNames)
    {
        var calls = new List<LlmToolCall>();
        foreach (Match f in InvokeCall().Matches(body))
        {
            var name = f.Groups["name"].Value;
            if (!toolNames.Contains(name)) return [];
            var args = new JsonObject();
            foreach (Match p in InvokeParameter().Matches(f.Groups["params"].Value))
                args[p.Groups["key"].Value] = Value(p.Groups["value"].Value.Trim());
            calls.Add(new LlmToolCall("text_" + Guid.NewGuid().ToString("N")[..10], name, args.ToJsonString()));
        }
        return calls;
    }

    /// <summary>A parameter written as text: a number, true/false or JSON when it looks like one, else a string.</summary>
    private static JsonNode? Value(string raw)
    {
        try { return raw.Length > 0 && (char.IsDigit(raw[0]) || raw[0] is '-' or '[' or '{' || raw is "true" or "false") ? JsonNode.Parse(raw) : JsonValue.Create(raw); }
        catch { return JsonValue.Create(raw); }
    }

    // Qwen3 / Qwen3-Coder's own format: <function=name><parameter=key>value</parameter></function>
    [GeneratedRegex(@"<function=(?<name>[\w.-]+)>(?<params>[\s\S]*?)(?:</function>|$)")]
    private static partial Regex XmlFunction();

    [GeneratedRegex(@"<parameter=(?<key>[\w.-]+)>\s*(?<value>[\s\S]*?)\s*(?:</parameter>|(?=<parameter=)|$)")]
    private static partial Regex XmlParameter();

    private static List<LlmToolCall> ParseXml(string body, IReadOnlySet<string> toolNames)
    {
        var calls = new List<LlmToolCall>();
        foreach (Match f in XmlFunction().Matches(body))
        {
            var name = f.Groups["name"].Value;
            if (!toolNames.Contains(name)) return [];
            var args = new JsonObject();
            foreach (Match p in XmlParameter().Matches(f.Groups["params"].Value))
            {
                args[p.Groups["key"].Value] = Value(p.Groups["value"].Value);
            }
            calls.Add(new LlmToolCall("text_" + Guid.NewGuid().ToString("N")[..10], name, args.ToJsonString()));
        }
        return calls;
    }

    private static List<LlmToolCall> Parse(string body, IReadOnlySet<string> toolNames)
    {
        if (body.Contains("<function=")) return ParseXml(body, toolNames);
        if (body.Contains("<invoke", StringComparison.OrdinalIgnoreCase)) return ParseInvokes(body, toolNames);
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
