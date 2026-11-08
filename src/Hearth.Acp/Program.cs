using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// hearth-acp: an Agent Client Protocol (ACP) agent that forwards to Hearth's OpenAI-compatible API, so editors that
// speak ACP (Zed, JetBrains, VS Code extensions) can use Hearth and its bots as their coding agent.
//
//   HEARTH_URL      Hearth address (default http://localhost:5290)
//   HEARTH_API_KEY  the API key from Hearth → Connections → More → API
//   HEARTH_MODEL    "hearth" or "hearth/<bot>", e.g. hearth/engineer (default hearth)
//
// Transport: JSON-RPC 2.0, one message per line on stdin/stdout. Logs go to stderr.

var baseUrl = ((Environment.GetEnvironmentVariable("HEARTH_URL") ?? Environment.GetEnvironmentVariable("LOCUS_URL")) ?? "http://localhost:5290").TrimEnd('/');
var apiKey = Environment.GetEnvironmentVariable("HEARTH_API_KEY") ?? Environment.GetEnvironmentVariable("LOCUS_API_KEY") ?? "";
var model = Environment.GetEnvironmentVariable("HEARTH_MODEL") ?? "hearth";

var bridge = new AcpBridge(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, baseUrl, apiKey, model, Console.Out);
await bridge.RunAsync(Console.In);

/// <summary>The ACP side: sessions (with their message history), prompts streamed back as message chunks.</summary>
public sealed class AcpBridge(HttpClient http, string baseUrl, string apiKey, string model, TextWriter output)
{
    public const int ProtocolVersion = 1;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    private sealed class Session(string cwd)
    {
        public string Cwd { get; } = cwd;
        public List<JsonObject> Messages { get; } = [];
        public CancellationTokenSource? Turn { get; set; }
    }

    public async Task RunAsync(TextReader input)
    {
        var pending = new List<Task>();
        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { await SendAsync(Error(null, -32700, "Parse error")); continue; }
            if (msg is null) continue;
            // Prompts run in the background so session/cancel can arrive while they stream.
            pending.Add(Task.Run(() => HandleAsync(msg)));
            pending.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(pending);
    }

    public async Task HandleAsync(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.ToString();
        var p = msg["params"] as JsonObject ?? [];
        try
        {
            switch (method)
            {
                case "initialize":
                    await ReplyAsync(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["agentCapabilities"] = new JsonObject
                        {
                            ["loadSession"] = false,
                            ["promptCapabilities"] = new JsonObject { ["image"] = false, ["audio"] = false, ["embeddedContext"] = true },
                        },
                        ["authMethods"] = new JsonArray(),
                    });
                    break;
                case "authenticate":
                    await ReplyAsync(id, new JsonObject());
                    break;
                case "session/new":
                    var sessionId = "hearth_" + Guid.NewGuid().ToString("N")[..16];
                    _sessions[sessionId] = new Session(p["cwd"]?.ToString() ?? "");
                    await ReplyAsync(id, new JsonObject { ["sessionId"] = sessionId });
                    break;
                case "session/prompt":
                    await PromptAsync(id, p);
                    break;
                case "session/cancel":
                    if (_sessions.TryGetValue(p["sessionId"]?.ToString() ?? "", out var s)) s.Turn?.Cancel();
                    break;
                default:
                    if (id is not null) await SendAsync(Error(id, -32601, $"Method not found: {method}"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hearth-acp: {method}: {ex.Message}");
            if (id is not null) await SendAsync(Error(id, -32603, ex.Message));
        }
    }

    private async Task PromptAsync(JsonNode? id, JsonObject p)
    {
        var sessionId = p["sessionId"]?.ToString() ?? "";
        if (!_sessions.TryGetValue(sessionId, out var session)) { await SendAsync(Error(id, -32602, "Unknown session")); return; }

        var text = PromptText(p["prompt"] as JsonArray ?? []);
        if (session.Messages.Count == 0 && session.Cwd.Length > 0)
            text = $"[The user is working in the folder {session.Cwd} in their code editor.]\n\n{text}";
        session.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

        using var turn = new CancellationTokenSource();
        session.Turn = turn;
        var answer = new StringBuilder();
        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(new JsonObject
            {
                ["model"] = model, ["stream"] = true,
                ["messages"] = new JsonArray(session.Messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            }.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, turn.Token);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(turn.Token);
                var message = (JsonNode.Parse(body) as JsonObject)?["error"]?["message"]?.ToString() ?? body;
                await ChunkAsync(sessionId, messageId, $"Hearth said no ({(int)res.StatusCode}): {message}");
                session.Messages.RemoveAt(session.Messages.Count - 1);
                await ReplyAsync(id, new JsonObject { ["stopReason"] = "refusal" });
                return;
            }
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(turn.Token));
            while (await reader.ReadLineAsync(turn.Token) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..];
                if (data == "[DONE]") break;
                var delta = (JsonNode.Parse(data) as JsonObject)?["choices"]?[0]?["delta"]?["content"]?.ToString();
                if (string.IsNullOrEmpty(delta)) continue;
                answer.Append(delta);
                await ChunkAsync(sessionId, messageId, delta);
            }
            session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() });
            await ReplyAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
        }
        catch (OperationCanceledException)
        {
            if (answer.Length > 0) session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() });
            await ReplyAsync(id, new JsonObject { ["stopReason"] = "cancelled" });
        }
        catch (HttpRequestException ex)
        {
            session.Messages.RemoveAt(session.Messages.Count - 1);
            await ChunkAsync(sessionId, messageId, $"Couldn't reach Hearth at {baseUrl}: {ex.Message}");
            await ReplyAsync(id, new JsonObject { ["stopReason"] = "refusal" });
        }
        finally { session.Turn = null; }
    }

    /// <summary>Text blocks as they are; files the editor attached (resources) inline; links by name.</summary>
    public static string PromptText(JsonArray blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks.OfType<JsonObject>())
            switch (b["type"]?.ToString())
            {
                case "text":
                    sb.AppendLine(b["text"]?.ToString());
                    break;
                case "resource" when b["resource"] is JsonObject r && r["text"] is { } t:
                    sb.AppendLine($"\n--- {r["uri"]} ---\n{t}\n");
                    break;
                case "resource_link":
                    sb.AppendLine($"[file: {b["uri"]}]");
                    break;
            }
        return sb.ToString().Trim();
    }

    private Task ChunkAsync(string sessionId, string messageId, string text) => SendAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["method"] = "session/update",
        ["params"] = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["update"] = new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk", ["messageId"] = messageId,
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
            },
        },
    });

    private Task ReplyAsync(JsonNode? id, JsonObject result) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task SendAsync(JsonObject message)
    {
        await _write.WaitAsync();
        try
        {
            await output.WriteLineAsync(message.ToJsonString());
            await output.FlushAsync();
        }
        finally { _write.Release(); }
    }
}
