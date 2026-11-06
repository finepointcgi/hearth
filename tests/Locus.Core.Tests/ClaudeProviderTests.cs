using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tests;

/// <summary>Runs the real Anthropic SDK against a local fake of the Messages API (no network, no key).</summary>
public class ClaudeApiClientTests
{
    private const string Sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":42,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Need the calculator."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG123"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Let me check."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"calculate","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"expression\": "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"2+2\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":30}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task StreamsThinkingTextAndToolCallsAndSendsTheRightRequest()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var bodies = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                using (var r = new StreamReader(ctx.Request.InputStream)) bodies.Add(await r.ReadToEndAsync());
                ctx.Response.ContentType = "text/event-stream";
                var bytes = Encoding.UTF8.GetBytes(Sse.Replace("\r\n", "\n"));
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var client = new ClaudeApiClient(new ProviderProfile
        {
            Kind = ProviderKind.Anthropic, BaseUrl = $"http://localhost:{port}", ApiKey = "test-key",
        });
        var tools = new[] { new ToolDefinition("calculate", "math", Tools.Schema.Object(("expression", "string", "expr", true))) };
        var history = new List<LlmMessage>
        {
            LlmMessage.System("You are Locus."),
            LlmMessage.User("What is 2+2?"),
        };

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        LlmChunk? last = null;
        await foreach (var c in client.StreamChatAsync(new LlmRequest("claude-opus-5-5", history, 0.7, 2048, 0, true, tools)))
        {
            text.Append(c.Content);
            thinking.Append(c.Thinking);
            last = c;
        }

        Assert.Equal("Let me check.", text.ToString());
        Assert.Equal("Need the calculator.", thinking.ToString());
        var call = Assert.Single(last!.ToolCalls!);
        Assert.Equal(("toolu_1", "calculate"), (call.Id, call.Name));
        Assert.Equal("2+2", JsonNode.Parse(call.ArgumentsJson)!["expression"]!.ToString());
        Assert.Equal(30, last.Stats!.CompletionTokens);
        Assert.Contains("SIG123", last.ProviderState);

        var req = JsonNode.Parse(bodies[0])!;
        Assert.Equal("claude-opus-5-5", req["model"]!.ToString());
        // The system prompt is cached (marker on it) and the conversation tail is cached automatically.
        Assert.Equal("You are Locus.", req["system"]![0]!["text"]!.ToString());
        Assert.Equal("ephemeral", req["system"]![0]!["cache_control"]!["type"]!.ToString());
        Assert.Equal("ephemeral", req["cache_control"]!["type"]!.ToString());
        Assert.Equal("adaptive", req["thinking"]!["type"]!.ToString());
        Assert.Equal("summarized", req["thinking"]!["display"]!.ToString());
        Assert.Equal("high", req["output_config"]!["effort"]!.ToString());
        Assert.Equal("default", req["fallbacks"]!.ToString());
        Assert.Null(req["temperature"]);
        Assert.True(req["max_tokens"]!.GetValue<int>() >= 16000);
        Assert.True(req["tools"]![0]!["eager_input_streaming"]!.GetValue<bool>());

        // Second round: the tool result goes back with the signed thinking block replayed first.
        history.Add(new LlmMessage { Role = "assistant", Content = "", ToolCalls = [call], ProviderState = last.ProviderState });
        history.Add(new LlmMessage { Role = "tool", Content = "2+2 = 4", ToolCallId = call.Id, ToolName = call.Name });
        history.Add(LlmMessage.User("[screenshot]"));
        await foreach (var _ in client.StreamChatAsync(new LlmRequest("claude-opus-5-5", history, 0.7, 2048, 0, false, tools))) { }

        var msgs = JsonNode.Parse(bodies[1])!["messages"]!.AsArray();
        Assert.Equal(3, msgs.Count); // user, assistant, user(tool_result + following user text merged)
        var assistant = msgs[1]!["content"]!.AsArray();
        Assert.Equal("thinking", assistant[0]!["type"]!.ToString());
        Assert.Equal("SIG123", assistant[0]!["signature"]!.ToString());
        Assert.Equal("tool_use", assistant[1]!["type"]!.ToString());
        var userTurn = msgs[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", userTurn[0]!["type"]!.ToString());
        Assert.Equal("toolu_1", userTurn[0]!["tool_use_id"]!.ToString());
        Assert.Equal("text", userTurn[1]!["type"]!.ToString());
        listener.Stop();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public class ClaudeCodeTests
{
    [Fact]
    public void FilterHoldsBackToolCallBlockAcrossChunks()
    {
        var f = new ClaudeCodeClient.ToolCallFilter();
        var shown = f.Push("Checking the math. <tool") + f.Push("_calls>\n[{\"name\":\"calculate\",") + f.Push("\"arguments\":{\"expression\":\"2+2\"}}]\n</tool_calls>") + f.Flush();
        Assert.Equal("Checking the math. ", shown);
        var tools = new[] { new ToolDefinition("calculate", "", Tools.Schema.Object()) };
        var calls = ClaudeCodeClient.ParseToolCalls(f.ToolBlock, tools);
        Assert.Equal("calculate", Assert.Single(calls).Name);
        Assert.Equal("2+2", JsonNode.Parse(calls[0].ArgumentsJson)!["expression"]!.ToString());
    }

    [Fact]
    public void PlainTextPassesThrough()
    {
        var f = new ClaudeCodeClient.ToolCallFilter();
        Assert.Equal("a < b and <tool", f.Push("a < b and <tool") + f.Flush());
        Assert.Null(f.ToolBlock);
    }

    [Fact]
    public void BadJsonBecomesAFailedCallTheModelCanRetry() =>
        Assert.Equal("invalid_tool_call", Assert.Single(ClaudeCodeClient.ParseToolCalls("[{nope", [new("x", "", Tools.Schema.Object())])).Name);

    /// <summary>Drives the real client against a fake `claude` executable that prints stream-json.</summary>
    [Fact]
    public async Task RunsTheCliAndParsesStreamJson()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("locus-cc-").FullName;
        var fake = Path.Combine(dir, "claude");
        var argsFile = Path.Combine(dir, "args.txt");
        var stdinFile = Path.Combine(dir, "stdin.txt");
        await File.WriteAllTextAsync(fake, """
            #!/bin/bash
            printf '%s\0' "$@" > "ARGS_FILE"
            cat > "STDIN_FILE"
            echo '{"type":"system","subtype":"init","model":"claude-haiku-5-5"}'
            echo '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}}'
            echo '{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"On it. <tool_calls>[{\"name\":\"calculate\",\"arguments\":{\"expression\":\"6*7\"}}]</tool_calls>"}}}'
            echo '{"type":"result","subtype":"success","is_error":false,"result":"On it.","usage":{"input_tokens":12,"output_tokens":9}}'
            """.Replace("ARGS_FILE", argsFile).Replace("STDIN_FILE", stdinFile));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var paths = new FixedPaths(dir);
        var client = new ClaudeCodeClient(new ProviderProfile { Kind = ProviderKind.ClaudeCode, BaseUrl = fake },
            new PlatformInfo("test", false), paths);
        var tools = new[] { new ToolDefinition("calculate", "math", Tools.Schema.Object(("expression", "string", "", true))) };
        var history = new List<LlmMessage> { LlmMessage.System("Be Locus."), LlmMessage.User("6 times 7?") };

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        LlmChunk? last = null;
        await foreach (var c in client.StreamChatAsync(new LlmRequest("haiku", history, 0.7, 0, 0, false, tools)))
        {
            text.Append(c.Content); thinking.Append(c.Thinking); last = c;
        }

        Assert.Equal("On it. ", text.ToString());
        Assert.Equal("hmm", thinking.ToString());
        Assert.Equal("calculate", Assert.Single(last!.ToolCalls!).Name);
        Assert.Null(last.Stats); // stopped at </tool_calls>, before the CLI's result line

        var args = (await File.ReadAllTextAsync(argsFile)).Split('\0');
        Assert.Contains("--model", args);
        Assert.Equal("haiku", args[Array.IndexOf(args, "--model") + 1]);
        Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]); // Claude Code's own tools are off
        Assert.Contains("<tool_calls>", args[Array.IndexOf(args, "--system-prompt") + 1]);
        var input = JsonNode.Parse(await File.ReadAllTextAsync(stdinFile))!;
        Assert.Equal("6 times 7?", input["message"]!["content"]![0]!["text"]!.ToString());
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExplainsHowToSignInWhenAuthFails()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("locus-cc-").FullName;
        var fake = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(fake, """
            #!/bin/bash
            cat > /dev/null
            echo '{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired"}'
            """);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var client = new ClaudeCodeClient(new ProviderProfile { Kind = ProviderKind.ClaudeCode, BaseUrl = fake },
            new PlatformInfo("test", false), new FixedPaths(dir));
        var ex = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(new LlmRequest("haiku", [LlmMessage.User("hi")], 0, 0, 0, false))) { }
        });
        Assert.Contains("/login", ex.Message);
        Directory.Delete(dir, true);
    }

    private sealed class FixedPaths(string dir) : IAppPaths { public string DataDirectory => dir; }
}

/// <summary>OpenAI-style servers that reject max_tokens / temperature get a corrected retry.</summary>
public class OpenAICompatTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests.Add(body);
            if (body["max_tokens"] is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("""{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."}}""") };
            if (body["temperature"] is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.7 with this model."}}""") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    [Fact]
    public async Task RetriesWithMaxCompletionTokensAndNoTemperature()
    {
        var handler = new Handler();
        var client = new OpenAICompatibleClient(new HttpClient(handler), new ProviderProfile { BaseUrl = "https://api.example.test/v1", ApiKey = "k" });
        var text = new StringBuilder();
        await foreach (var c in client.StreamChatAsync(new LlmRequest("gpt-x", [LlmMessage.User("hi")], 0.7, 500, 0, false)))
            text.Append(c.Content);
        Assert.Equal("ok", text.ToString());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(500, handler.Requests[^1]["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(handler.Requests[^1]["temperature"]);
    }

    private sealed class Sse(string events, bool rejectTemplateKwargs = false) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests.Add(body);
            if (rejectTemplateKwargs && body["chat_template_kwargs"] is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"Unrecognized request argument supplied: chat_template_kwargs"}}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
    }

    [Fact]
    public async Task ThinkSwitchReachesTheServerAndCutOffsAreFlagged()
    {
        var handler = new Sse("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n");
        var client = new OpenAICompatibleClient(new HttpClient(handler), new ProviderProfile { BaseUrl = "http://mlx.test/v1" });
        var notices = new List<string>();
        await foreach (var c in client.StreamChatAsync(new LlmRequest("qwen", [LlmMessage.User("hi")], 0.7, 2048, 0, Think: false)))
            if (c.Notice is not null) notices.Add(c.Notice);
        Assert.False(handler.Requests[0]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Contains("cut off", Assert.Single(notices));

        // Servers that don't know the field get the request without it.
        var strict = new Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", rejectTemplateKwargs: true);
        var hosted = new OpenAICompatibleClient(new HttpClient(strict), new ProviderProfile { BaseUrl = "https://api.example.test/v1" });
        await foreach (var c in hosted.StreamChatAsync(new LlmRequest("gpt-x", [LlmMessage.User("hi")], 0.7, 0, 0, Think: true)))
            Assert.Null(c.Notice);
        Assert.Equal(2, strict.Requests.Count);
        Assert.Null(strict.Requests[1]["chat_template_kwargs"]);
    }
}

public class ClaudeCodeRunOnTests
{
    [Fact]
    public void IgnoresInventedResultsAfterTheBlock()
    {
        // What Haiku actually produced in a live test: the call, then a made-up result.
        var f = new ClaudeCodeClient.ToolCallFilter();
        var shown = f.Push("I'll calculate that for you.\n<tool_calls>\n[{\"name\": \"calculate\", \"arguments\": {\"expression\": \"48213 * 9177 + 1\"}}]\n</tool_calls>\n\n")
                  + f.Push("<tool_result>\n442,545,702\n</tool_result>\n\nThe result is **442,545,702**.") + f.Flush();
        Assert.Equal("I'll calculate that for you.\n", shown);
        Assert.True(f.Closed);
        var call = Assert.Single(ClaudeCodeClient.ParseToolCalls(f.ToolBlock, [new("calculate", "", Locus.Core.Tools.Schema.Object())]));
        Assert.Equal("calculate", call.Name);
    }
}
