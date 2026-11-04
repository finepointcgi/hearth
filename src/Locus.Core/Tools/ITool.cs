using System.Globalization;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Tools;

public sealed record ToolResult(
    string Text,
    IReadOnlyList<ImageAttachment>? Images = null,
    IReadOnlyList<SearchResult>? Sources = null,
    bool IsError = false,
    /// <summary>An image the model should see (e.g. a screenshot after a computer-use action).</summary>
    ImageAttachment? ModelImage = null)
{
    public static ToolResult Error(string message) => new(message, IsError: true);
}

public sealed class ToolContext
{
    public required Conversation Conversation { get; init; }
    public required ChatMessage Reply { get; init; }
    public required AppSettings Settings { get; init; }
    public required ILlmClient Client { get; init; }
    public required string Model { get; init; }
    /// <summary>The bot replying, if this is a bot chat.</summary>
    public Bot? Bot { get; init; }
    /// <summary>Nesting depth of bot-to-bot calls (0 = answering the user).</summary>
    public int Depth { get; init; }
    /// <summary>No one is watching (routines): tools that need approval are declined.</summary>
    public bool Unattended { get; init; }
}

public interface ITool
{
    /// <summary>Function name the model calls ([a-zA-Z0-9_-], ≤ 64 chars).</summary>
    string Name { get; }
    string DisplayName { get; }
    string Icon { get; }
    string Description { get; }
    JsonObject Parameters { get; }
    /// <summary>True when the tool can change things or run code — the user is asked first.</summary>
    bool RequiresApproval { get; }
    Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct);
}

/// <summary>A tool whose need for approval depends on who's calling (e.g. a bot's own sandbox vs. your screen).</summary>
public interface IContextualApproval
{
    bool RequiresApprovalFor(ToolContext ctx);
}

/// <summary>Supplies the tools that are currently available (depends on settings and platform).</summary>
public interface IToolProvider
{
    Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct);
}

public static class Schema
{
    public static JsonObject Object(params (string name, string type, string description, bool required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
            properties[p.name] = new JsonObject { ["type"] = p.type, ["description"] = p.description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.required).Select(p => (JsonNode)JsonValue.Create(p.name)!).ToArray()),
        };
    }

    public static string? Str(this JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var n => n.ToJsonString(),
    };

    public static double? Num(this JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) => d2,
        _ => null,
    };

    public static bool Bool(this JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n…[truncated {text.Length - max} characters]";
}
