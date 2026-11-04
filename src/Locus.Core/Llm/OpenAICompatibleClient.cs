using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Locus.Core.Models;

namespace Locus.Core.Llm;

/// <summary>
/// OpenAI-compatible /v1 client with SSE streaming and tool calls.
/// Works with LM Studio, llama.cpp server, vLLM, Jan, LocalAI, KoboldCpp and Ollama's /v1 endpoint.
/// </summary>
public sealed class OpenAICompatibleClient(HttpClient http, ProviderProfile profile) : ILlmClient
{
    private string Url(string path)
    {
        var b = profile.BaseUrl.TrimEnd('/');
        if (!b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b += "/v1";
        return b + path;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var msg = new HttpRequestMessage(method, Url(path));
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        return msg;
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, "/models"), ct);
            res.EnsureSuccessStatusCode();
            var doc = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            return doc?["data"]?.AsArray()
                .Select(m => new ModelInfo(m!["id"]!.GetValue<string>()))
                .OrderBy(m => m.Id)
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LlmException($"Could not reach {profile.Name} at {profile.BaseUrl}: {ex.Message}", ex);
        }
    }

    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<ModelCapabilities?>(null);

    public async Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var msg = Request(HttpMethod.Post, "/embeddings");
        msg.Content = LlmJson.Body(new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
        });
        using var res = await http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new LlmException($"Embedding failed: {ExtractError(body)}");
        return JsonNode.Parse(body)!["data"]!.AsArray()
            .OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray())
            .ToArray();
    }

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = req.Model,
            ["stream"] = true,
            ["temperature"] = req.Temperature,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray(req.Messages.Select(ToJson).ToArray()),
        };
        if (req.MaxTokens > 0) payload["max_tokens"] = req.MaxTokens;
        if (req.Tools is { Count: > 0 }) payload["tools"] = LlmJson.ToolsArray(req.Tools);

        // Hosted APIs differ: newer OpenAI models reject max_tokens (want max_completion_tokens) and custom
        // temperature. On a 400 that names one of these, adjust and retry instead of failing.
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            var msg = Request(HttpMethod.Post, "/chat/completions");
            msg.Content = LlmJson.Body(payload);
            response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) break;
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status == 400 && attempt < 3)
            {
                if (body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) && payload["max_tokens"] is { } mt)
                {
                    payload.Remove("max_tokens");
                    payload["max_completion_tokens"] = mt.DeepClone();
                    continue;
                }
                if (body.Contains("temperature", StringComparison.OrdinalIgnoreCase) && payload.ContainsKey("temperature"))
                {
                    payload.Remove("temperature");
                    continue;
                }
                if (body.Contains("stream_options", StringComparison.OrdinalIgnoreCase) && payload.ContainsKey("stream_options"))
                {
                    payload.Remove("stream_options");
                    continue;
                }
            }
            throw new LlmException($"{status}: {ExtractError(body)}");
        }
        using var _ = response;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var parser = new ThinkTagParser();
        DateTime? firstToken = null;
        var chunks = 0;
        LlmStats? stats = null;
        var sawDone = false;
        // Tool call deltas arrive in fragments keyed by index.
        var calls = new SortedDictionary<int, (string id, string name, StringBuilder args)>();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { sawDone = true; break; }

            var node = JsonNode.Parse(data)!;
            if (node["error"] is { } err) throw new LlmException(err["message"]?.ToString() ?? err.ToString());

            if (node["usage"] is JsonObject usage)
            {
                var completion = usage["completion_tokens"]?.GetValue<int>();
                var secs = firstToken is null ? 0 : (DateTime.UtcNow - firstToken.Value).TotalSeconds;
                stats = new LlmStats(usage["prompt_tokens"]?.GetValue<int>(), completion,
                    completion is > 0 && secs > 0 ? completion / secs : null);
            }

            var delta = node["choices"]?.AsArray().FirstOrDefault()?["delta"];
            if (delta is null) continue;

            if (delta["tool_calls"] is JsonArray tcs)
                foreach (var tc in tcs)
                {
                    var index = tc?["index"]?.GetValue<int>() ?? calls.Count;
                    if (!calls.TryGetValue(index, out var entry))
                        entry = (tc?["id"]?.ToString() ?? "call_" + Guid.NewGuid().ToString("N")[..12], "", new StringBuilder());
                    var fn = tc?["function"];
                    if (fn?["name"]?.ToString() is { Length: > 0 } n) entry.name += n;
                    if (fn?["arguments"]?.ToString() is { } a) entry.args.Append(a);
                    calls[index] = entry;
                }

            var thinking = (delta["reasoning_content"] ?? delta["reasoning"])?.GetValue<string>();
            var content = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content))
            {
                var (c, t) = parser.Push(content);
                content = c;
                thinking = (thinking ?? "") + t;
            }
            if (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(thinking))
            {
                firstToken ??= DateTime.UtcNow;
                chunks++;
                yield return new LlmChunk(content, thinking);
            }
        }

        if (!sawDone && firstToken is null && calls.Count == 0)
            throw new LlmException("The model server closed the connection before sending a reply.");
        var (fc, ft) = parser.Flush();
        if (stats is null && firstToken is not null)
        {
            var secs = (DateTime.UtcNow - firstToken.Value).TotalSeconds;
            stats = new LlmStats(null, chunks, secs > 0 ? chunks / secs : null);
        }
        var toolCalls = calls.Values.Where(c => c.name.Length > 0)
            .Select(c => new LlmToolCall(c.id, c.name, c.args.Length > 0 ? c.args.ToString() : "{}"))
            .ToList();
        yield return new LlmChunk(fc, ft, stats, toolCalls.Count > 0 ? toolCalls : null);
    }

    private static JsonNode ToJson(LlmMessage m)
    {
        if (m.Role == "tool")
            return new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content };

        var o = new JsonObject { ["role"] = m.Role };
        if (m.Images.Count == 0)
        {
            o["content"] = m.Content;
        }
        else
        {
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
            foreach (var img in m.Images)
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = img.DataUrl } });
            o["content"] = parts;
        }
        if (m.ToolCalls.Count > 0)
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        return o;
    }

    private static string ExtractError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.ToString() ?? n?["error"]?.ToString() ?? body;
        }
        catch (JsonException) { return body; }
    }
}
