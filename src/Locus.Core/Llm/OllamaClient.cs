using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Llm;

/// <summary>Talks to Ollama's native API (/api/chat, /api/tags, /api/embed, /api/show) with NDJSON streaming.</summary>
public sealed class OllamaClient(HttpClient http, ProviderProfile profile) : ILlmClient
{
    private string Url(string path) => profile.BaseUrl.TrimEnd('/') + path;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await http.GetFromJsonAsync<JsonObject>(Url("/api/tags"), ct);
            return doc?["models"]?.AsArray()
                .Select(m => new ModelInfo(
                    m!["name"]!.GetValue<string>(),
                    m["details"]?["family"]?.GetValue<string>(),
                    m["size"]?.GetValue<long>()))
                .OrderBy(m => m.Id)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LlmException($"Could not reach Ollama at {profile.BaseUrl}: {ex.Message}", ex);
        }
    }

    public async Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.PostAsync(Url("/api/show"), LlmJson.Body(new JsonObject { ["model"] = model }), ct);
            if (!res.IsSuccessStatusCode) return null;
            var doc = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            var caps = doc?["capabilities"]?.AsArray().Select(c => c?.ToString()).ToHashSet();
            if (caps is null) return null;
            return new ModelCapabilities(caps.Contains("tools"), caps.Contains("vision"),
                caps.Contains("thinking"), caps.Contains("embedding"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
        };
        using var res = await http.PostAsync(Url("/api/embed"), LlmJson.Body(payload), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new LlmException($"Embedding failed: {ExtractError(body)}");
        return JsonNode.Parse(body)!["embeddings"]!.AsArray()
            .Select(e => e!.AsArray().Select(v => v!.GetValue<float>()).ToArray())
            .ToArray();
    }

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var think = req.Think;
        var tools = req.Tools is { Count: > 0 } ? req.Tools : null;
        HttpResponseMessage response;
        string? notice = null;

        // Ollama returns 400 when a model lacks a capability we asked for; drop it and retry.
        while (true)
        {
            response = await SendAsync(req, think, tools, ct);
            if (response.StatusCode != HttpStatusCode.BadRequest) break;
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (think && body.Contains("think", StringComparison.OrdinalIgnoreCase)) { think = false; continue; }
            if (tools is not null && body.Contains("tools", StringComparison.OrdinalIgnoreCase))
            {
                tools = null;
                notice = $"{req.Model} doesn't support tool calling, so tools were turned off for this reply.";
                continue;
            }
            throw new LlmException(ExtractError(body));
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw new LlmException(ExtractError(await response.Content.ReadAsStringAsync(ct)));

        if (notice is not null) yield return new LlmChunk(Notice: notice);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var parser = new ThinkTagParser();
        var toolCalls = new List<LlmToolCall>();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonNode.Parse(line)!;
            if (node["error"] is { } err) throw new LlmException(err.ToString());

            if (node["message"]?["tool_calls"] is JsonArray calls)
                foreach (var c in calls)
                {
                    var fn = c?["function"];
                    if (fn?["name"] is null) continue;
                    toolCalls.Add(new LlmToolCall(
                        c!["id"]?.ToString() ?? "call_" + Guid.NewGuid().ToString("N")[..12],
                        fn["name"]!.ToString(),
                        fn["arguments"]?.ToJsonString() ?? "{}"));
                }

            var thinking = node["message"]?["thinking"]?.GetValue<string>();
            var content = node["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content))
            {
                var (c, t) = parser.Push(content);
                content = c;
                thinking = (thinking ?? "") + t;
            }
            if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(thinking))
                yield return new LlmChunk(content, thinking);

            if (node["done"]?.GetValue<bool>() == true)
            {
                var (fc, ft) = parser.Flush();
                var evalCount = node["eval_count"]?.GetValue<int>();
                var evalNs = node["eval_duration"]?.GetValue<long>();
                double? tps = evalCount is > 0 && evalNs is > 0 ? evalCount / (evalNs / 1e9) : null;
                yield return new LlmChunk(fc, ft,
                    new LlmStats(node["prompt_eval_count"]?.GetValue<int>(), evalCount, tps),
                    toolCalls.Count > 0 ? toolCalls : null);
                yield break;
            }
        }
        throw new LlmException("The model server closed the connection before finishing the reply.");
    }

    private Task<HttpResponseMessage> SendAsync(LlmRequest req, bool think, IReadOnlyList<ToolDefinition>? tools, CancellationToken ct)
    {
        var options = new JsonObject
        {
            ["temperature"] = req.Temperature,
            ["num_predict"] = req.MaxTokens > 0 ? req.MaxTokens : -1,
        };
        if (req.ContextLength > 0) options["num_ctx"] = req.ContextLength;

        var payload = new JsonObject
        {
            ["model"] = req.Model,
            ["stream"] = true,
            ["options"] = options,
            ["messages"] = new JsonArray(req.Messages.Select(ToJson).ToArray()),
        };
        if (think) payload["think"] = true;
        if (tools is not null) payload["tools"] = LlmJson.ToolsArray(tools);

        var msg = new HttpRequestMessage(HttpMethod.Post, Url("/api/chat")) { Content = LlmJson.Body(payload) };
        return http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static JsonNode ToJson(LlmMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.Images.Count > 0)
            o["images"] = new JsonArray(m.Images.Select(i => (JsonNode)JsonValue.Create(i.Base64)!).ToArray());
        if (m.ToolCalls.Count > 0)
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = LlmJson.ParseArgs(c.ArgumentsJson) },
            }).ToArray());
        if (m.ToolName is not null) o["tool_name"] = m.ToolName;
        return o;
    }

    private static string ExtractError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?.ToString() ?? body; }
        catch (JsonException) { return body; }
    }
}
