using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Locus.Core.Models;

namespace Locus.Core.Llm;

/// <summary>
/// Claude through the Anthropic API, using the official Anthropic C# SDK (beta Messages endpoint so
/// server-side refusal fallbacks can be enabled). Streams text, summarized thinking and tool calls.
/// </summary>
public sealed class ClaudeApiClient(ProviderProfile profile) : ILlmClient
{
    private AnthropicClient Client()
    {
        var key = string.IsNullOrWhiteSpace(profile.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : profile.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new LlmException("Add your Anthropic API key to this provider in Settings (console.anthropic.com → API keys).");
        return !string.IsNullOrWhiteSpace(profile.BaseUrl) && !profile.BaseUrl.Contains("api.anthropic.com")
            ? new AnthropicClient { ApiKey = key, BaseUrl = profile.BaseUrl.TrimEnd('/') }
            : new AnthropicClient { ApiKey = key };
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await Client().Models.List(cancellationToken: ct);
            return page.Items.Select(m => new ModelInfo(m.ID, "claude", null, true)).ToList();
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException) { throw Wrap(ex); }
    }

    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<ModelCapabilities?>(new ModelCapabilities(Tools: true, Vision: true, Thinking: true, Embedding: false));

    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
        throw new LlmException("Claude doesn't provide embeddings. In Documents, pick a local embedding provider (e.g. Ollama with nomic-embed-text).");

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = Client();
        var system = string.Join("\n\n", req.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var parameters = new MessageCreateParams
        {
            Model = req.Model,
            // Thinking shares max_tokens with the answer; keep plenty of room.
            MaxTokens = Math.Max(req.MaxTokens <= 0 ? 32000 : req.MaxTokens, 16000),
            Messages = ToMessages(req.Messages.Where(m => m.Role != "system").ToList()),
            // Show a readable summary of Claude's reasoning; Think raises effort.
            Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized },
            // Server-side refusal fallbacks: if a safety classifier declines, Anthropic re-serves the
            // request on a suitable model instead of returning a bare refusal.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new BetaFallbacksParam(JsonSerializer.SerializeToElement("default")),
        };
        if (system.Length > 0) parameters = parameters with { System = system };
        if (req.Think) parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = Effort.High } };
        if (req.Tools is { Count: > 0 }) parameters = parameters with { Tools = req.Tools.Select(ToTool).ToList() };

        var tools = new Dictionary<long, (string id, string name, StringBuilder json)>();
        var thinkingBlocks = new JsonArray();
        var currentThinking = new StringBuilder();
        string? currentSignature = null;
        long? inputTokens = null, outputTokens = null;
        string? stopReason = null;
        var started = DateTime.UtcNow;
        DateTime? firstToken = null;

        IAsyncEnumerator<BetaRawMessageStreamEvent> events;
        try { events = client.Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct); }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException) { throw Wrap(ex); }

        await using (events)
        {
            while (true)
            {
                BetaRawMessageStreamEvent ev;
                try
                {
                    if (!await events.MoveNextAsync()) break;
                    ev = events.Current;
                }
                catch (Exception ex) when (ex is AnthropicException or HttpRequestException) { throw Wrap(ex); }

                if (ev.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (ev.TryPickContentBlockStart(out var bs))
                {
                    if (bs.ContentBlock.TryPickBetaToolUse(out var tu))
                        tools[bs.Index] = (tu.ID, tu.Name, new StringBuilder());
                    else if (bs.ContentBlock.TryPickBetaThinking(out _))
                    {
                        currentThinking.Clear();
                        currentSignature = null;
                    }
                    else if (bs.ContentBlock.TryPickBetaRedactedThinking(out var rt))
                        thinkingBlocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = rt.Data });
                }
                else if (ev.TryPickContentBlockDelta(out var d))
                {
                    if (d.Delta.TryPickText(out var text))
                    {
                        firstToken ??= DateTime.UtcNow;
                        yield return new LlmChunk(Content: text.Text);
                    }
                    else if (d.Delta.TryPickThinking(out var th))
                    {
                        currentThinking.Append(th.Thinking);
                        yield return new LlmChunk(Thinking: th.Thinking);
                    }
                    else if (d.Delta.TryPickSignature(out var sig))
                        currentSignature = sig.Signature;
                    else if (d.Delta.TryPickInputJson(out var ij) && tools.TryGetValue(d.Index, out var t))
                        t.json.Append(ij.PartialJson);
                }
                else if (ev.TryPickContentBlockStop(out _))
                {
                    if (currentSignature is not null)
                    {
                        // Keep thinking blocks (with signatures) so they can be replayed with tool results.
                        thinkingBlocks.Add(new JsonObject
                        {
                            ["type"] = "thinking", ["thinking"] = currentThinking.ToString(), ["signature"] = currentSignature,
                        });
                        currentSignature = null;
                        currentThinking.Clear();
                    }
                }
                else if (ev.TryPickDelta(out var md))
                {
                    stopReason = md.Delta.StopReason?.ToString();
                    outputTokens = md.Usage.OutputTokens;
                }
            }
        }

        if (stopReason?.Contains("refusal", StringComparison.OrdinalIgnoreCase) == true)
            yield return new LlmChunk(Notice: "Claude declined to continue this request.");
        else if (stopReason?.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) == true)
            yield return new LlmChunk(Notice: "The reply hit the max-token limit and was cut off.");

        var secs = firstToken is null ? 0 : (DateTime.UtcNow - firstToken.Value).TotalSeconds;
        var calls = tools.OrderBy(t => t.Key)
            .Select(t => new LlmToolCall(t.Value.id, t.Value.name, t.Value.json.Length > 0 ? t.Value.json.ToString() : "{}"))
            .ToList();
        yield return new LlmChunk(
            Stats: new LlmStats((int?)inputTokens, (int?)outputTokens, outputTokens > 0 && secs > 0 ? outputTokens / secs : null),
            ToolCalls: calls.Count > 0 ? calls : null,
            ProviderState: thinkingBlocks.Count > 0 ? thinkingBlocks.ToJsonString() : null);
        _ = started;
    }

    /// <summary>
    /// Outages, overloads (5xx/529), rate limits and network failures are worded "Could not reach…" so the fallback
    /// servers take over; request problems (bad key, invalid request) are reported as-is.
    /// </summary>
    public static LlmException Wrap(Exception ex) => ex switch
    {
        Anthropic5xxException or AnthropicIOException or AnthropicSseException or HttpRequestException
            => new LlmException($"Could not reach the Anthropic API: {ex.Message}", ex),
        AnthropicRateLimitException
            => new LlmException($"Could not reach the Anthropic API right now (rate limited): {ex.Message}", ex),
        _ => new LlmException($"Anthropic API: {ex.Message}", ex),
    };

    private static BetaToolUnion ToTool(ToolDefinition t)
    {
        var props = t.Parameters["properties"] as JsonObject ?? [];
        var required = (t.Parameters["required"] as JsonArray)?.Select(r => r!.ToString()).ToList() ?? [];
        return new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = props.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                Required = required,
            },
            // Stream large tool inputs as they're generated; the tool loop validates the parsed JSON.
            EagerInputStreaming = true,
        };
    }

    /// <summary>Converts the provider-neutral history; consecutive same-role turns are merged and tool results grouped.</summary>
    private static List<BetaMessageParam> ToMessages(List<LlmMessage> history)
    {
        var result = new List<(string role, List<BetaContentBlockParam> blocks)>();
        void Add(string role, BetaContentBlockParam block)
        {
            if (result.Count > 0 && result[^1].role == role) result[^1].blocks.Add(block);
            else result.Add((role, [block]));
        }

        foreach (var m in history)
        {
            switch (m.Role)
            {
                case "user":
                    foreach (var img in m.Images)
                        Add("user", new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = img.Base64, MediaType = img.MimeType } });
                    if (m.Content.Length > 0 || m.Images.Count == 0) Add("user", new BetaTextBlockParam { Text = m.Content.Length > 0 ? m.Content : "(image)" });
                    break;
                case "tool":
                    Add("user", new BetaToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Content.Length > 0 ? m.Content : "(no output)" });
                    break;
                case "assistant":
                    if (m.ProviderState is not null && JsonNode.Parse(m.ProviderState) is JsonArray blocks)
                        foreach (var b in blocks)
                        {
                            if (b?["type"]?.ToString() == "thinking")
                                Add("assistant", new BetaThinkingBlockParam { Thinking = b["thinking"]!.ToString(), Signature = b["signature"]!.ToString() });
                            else if (b?["type"]?.ToString() == "redacted_thinking")
                                Add("assistant", new BetaRedactedThinkingBlockParam { Data = b["data"]!.ToString() });
                        }
                    if (m.Content.Length > 0) Add("assistant", new BetaTextBlockParam { Text = m.Content });
                    foreach (var c in m.ToolCalls)
                        Add("assistant", new BetaToolUseBlockParam
                        {
                            ID = c.Id,
                            Name = c.Name,
                            Input = (JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) as JsonObject ?? [])
                                .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                        });
                    break;
            }
        }

        return result.Select(r => r.role == "user"
            ? (BetaMessageParam)new BetaUserMessageParam { Content = r.blocks }
            : new BetaAssistantMessageParam { Content = r.blocks }).ToList();
    }
}
