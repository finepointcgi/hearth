using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Llm;

/// <summary>Provider-neutral message sent to a model (adds tool plumbing on top of <see cref="ChatMessage"/>).</summary>
public sealed class LlmMessage
{
    public required string Role { get; init; } // system | user | assistant | tool
    public string Content { get; init; } = "";
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    /// <summary>Opaque provider data replayed with an assistant tool-call turn (e.g. Claude thinking blocks + signatures).</summary>
    public string? ProviderState { get; init; }

    public static LlmMessage System(string text) => new() { Role = "system", Content = text };
    public static LlmMessage User(string text, IReadOnlyList<ImageAttachment>? images = null) =>
        new() { Role = "user", Content = text, Images = images ?? [] };
}

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A function the model may call. <paramref name="Parameters"/> is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record LlmRequest(
    string Model,
    IReadOnlyList<LlmMessage> Messages,
    double Temperature,
    int MaxTokens,
    int ContextLength,
    bool Think,
    IReadOnlyList<ToolDefinition>? Tools = null);

public sealed record LlmChunk(
    string? Content = null,
    string? Thinking = null,
    LlmStats? Stats = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? Notice = null,
    string? ProviderState = null);

public interface ILlmClient
{
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default);
    IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, CancellationToken ct = default);
    Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default);
    /// <summary>Returns null when the server can't tell us.</summary>
    Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

public interface ILlmClientFactory
{
    ILlmClient Create(ProviderProfile profile);

    /// <summary>The active server, falling back to the configured backup servers when it can't be reached.</summary>
    ILlmClient CreateForChat(AppSettings settings) => CreateForBot(settings, null);

    /// <summary>
    /// The bot's own server (or the active one), falling back to the active server and then the backup servers.
    /// </summary>
    ILlmClient CreateForBot(AppSettings settings, Bot? bot)
    {
        var first = settings.ProviderFor(bot);
        var chain = new List<(ProviderProfile, ILlmClient)> { (first, Create(first)) };
        IEnumerable<string> next = first.Id == settings.ActiveProvider.Id
            ? settings.FallbackProviderIds
            : [settings.ActiveProvider.Id, .. settings.FallbackProviderIds];
        foreach (var id in next)
            if (chain.All(c => c.Item1.Id != id) && settings.Providers.FirstOrDefault(p => p.Id == id) is { } p)
                chain.Add((p, Create(p)));
        return chain.Count == 1 ? chain[0].Item2 : new FailoverLlmClient(chain);
    }
}

public sealed class LlmClientFactory(IHttpClientFactory http, PlatformInfo platform, IAppPaths paths) : ILlmClientFactory
{
    public const string HttpClientName = "llm";

    public ILlmClient Create(ProviderProfile profile)
    {
        var client = http.CreateClient(HttpClientName);
        return profile.Kind switch
        {
            ProviderKind.Ollama => new OllamaClient(client, profile),
            ProviderKind.Anthropic => new ClaudeApiClient(profile),
            ProviderKind.ClaudeCode => new ClaudeCodeClient(profile, platform, paths),
            _ => new OpenAICompatibleClient(client, profile),
        };
    }
}

internal static class LlmJson
{
    public static StringContent Body(JsonNode payload) =>
        new(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    public static JsonArray ToolsArray(IReadOnlyList<ToolDefinition> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
            },
        }).ToArray());

    public static JsonNode ParseArgs(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }
}
