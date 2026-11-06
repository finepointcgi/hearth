using System.Text.Json;
using System.Text.Json.Nodes;

namespace Locus.Core.Tools;

/// <summary>
/// Reads a tool call's arguments the way models actually send them (double-encoded strings, code fences, trailing
/// commas) and checks the required ones are there. On a problem, returns a message that tells the model what it sent
/// and what the tool expects, so it can fix the call next round.
/// </summary>
public static class ToolArguments
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static (JsonObject args, string? problem) Check(string? json, JsonObject schema)
    {
        var text = (json ?? "").Trim();
        if (text.StartsWith("```")) text = text.Trim('`').Trim().TrimStart("json".ToCharArray()).Trim();
        if (text.Length == 0) text = "{}";

        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: Lenient); }
        catch (JsonException ex) { return ([], Problem($"The arguments weren't valid JSON ({ex.Message.Split('.')[0]}).", text, schema)); }

        // Some models send the arguments object as a JSON string.
        if (node is JsonValue v && v.TryGetValue<string>(out var inner))
            try { node = JsonNode.Parse(inner, documentOptions: Lenient); }
            catch (JsonException) { }

        if (node is not JsonObject args) return ([], Problem("The arguments must be a JSON object.", text, schema));

        var missing = (schema["required"] as JsonArray ?? [])
            .Select(r => r?.ToString())
            .Where(r => r is not null && (args[r] is null || args[r] is JsonValue s && s.TryGetValue<string>(out var str) && str.Length == 0))
            .ToList();
        if (missing.Count > 0)
            return ([], Problem($"Missing required {(missing.Count == 1 ? "argument" : "arguments")}: {string.Join(", ", missing)}.", text, schema));
        return (args, null);
    }

    private static string Problem(string what, string sent, JsonObject schema)
    {
        var shown = sent.Length > 300 ? sent[..300] + "…" : sent;
        return $"Not run: {what} You sent: {shown}\nExpected parameters (JSON Schema): {schema.ToJsonString()}\nCall it again with corrected arguments.";
    }
}
