using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tests;

/// <summary>The Claude providers behind the fallback wrapper (FailoverLlmClient).</summary>
public class ClaudeFailoverTests
{
    private sealed class Paths(string dir) : IAppPaths { public string DataDirectory => dir; }

    private sealed class Backup : ILlmClient
    {
        public bool Called { get; private set; }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("llama3.2")]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);
        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Called = true;
            await Task.Yield();
            yield return new LlmChunk(Content: $"backup answer from {request.Model}");
        }
    }

    private static (string dir, string cli) FakeCli(string body)
    {
        var dir = Directory.CreateTempSubdirectory("locus-fo-").FullName;
        var cli = Path.Combine(dir, "claude");
        File.WriteAllText(cli, "#!/bin/bash\ncat > /dev/null\n" + body);
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (dir, cli);
    }

    private static FailoverLlmClient Chain(ILlmClient primary, string primaryName, Backup backup) => new(
    [
        (new ProviderProfile { Name = primaryName, Kind = ProviderKind.ClaudeCode }, primary),
        (new ProviderProfile { Name = "Ollama", Kind = ProviderKind.Ollama, DefaultModel = "llama3.2" }, backup),
    ]);

    private static async Task<(string text, List<LlmChunk> chunks)> Run(ILlmClient client, IReadOnlyList<ToolDefinition>? tools = null)
    {
        var chunks = new List<LlmChunk>();
        await foreach (var c in client.StreamChatAsync(new LlmRequest("haiku", [LlmMessage.User("6*7?")], 0.7, 0, 0, false, tools)))
            chunks.Add(c);
        return (string.Concat(chunks.Select(c => c.Content)), chunks);
    }

    [Fact]
    public async Task ClaudeCodeToolCallsPassThroughUnchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        var (dir, cli) = FakeCli("""
            echo '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Sure. <tool_calls>[{\"name\":\"calculate\",\"arguments\":{\"expression\":\"6*7\"}}]</tool_calls> made-up result"}}}'
            sleep 1
            """);
        var backup = new Backup();
        var cc = new ClaudeCodeClient(new ProviderProfile { Kind = ProviderKind.ClaudeCode, BaseUrl = cli }, new PlatformInfo("t", false), new Paths(dir));
        var (text, chunks) = await Run(Chain(cc, "Claude Code", backup), [new("calculate", "math", Tools.Schema.Object(("expression", "string", "", true)))]);

        Assert.Equal("Sure. ", text);
        Assert.Equal("calculate", Assert.Single(chunks.Last().ToolCalls!).Name);
        Assert.DoesNotContain(chunks, c => c.Notice is not null);
        Assert.False(backup.Called);
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("API Error: 529 {\\\"type\\\":\\\"overloaded_error\\\"}")]
    [InlineData("Claude usage limit reached. Your limit will reset at 5pm.")]
    [InlineData("API Error: Connection error.")]
    public async Task ClaudeCodeOutagesAndLimitsFallBack(string error)
    {
        if (OperatingSystem.IsWindows()) return;
        var (dir, cli) = FakeCli($$"""
            echo '{"type":"result","subtype":"success","is_error":true,"result":"{{error}}"}'
            """);
        var backup = new Backup();
        var cc = new ClaudeCodeClient(new ProviderProfile { Kind = ProviderKind.ClaudeCode, BaseUrl = cli }, new PlatformInfo("t", false), new Paths(dir));
        var (text, chunks) = await Run(Chain(cc, "Claude Code", backup));

        Assert.True(backup.Called);
        Assert.Equal("backup answer from llama3.2", text);
        Assert.Contains(chunks, c => c.Notice?.Contains("Claude Code couldn't be reached") == true);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ClaudeCodeSignInProblemsDoNotSilentlyFallBack()
    {
        if (OperatingSystem.IsWindows()) return;
        var (dir, cli) = FakeCli("""
            echo '{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired"}'
            """);
        var backup = new Backup();
        var cc = new ClaudeCodeClient(new ProviderProfile { Kind = ProviderKind.ClaudeCode, BaseUrl = cli }, new PlatformInfo("t", false), new Paths(dir));
        var ex = await Assert.ThrowsAsync<LlmException>(() => Run(Chain(cc, "Claude Code", backup)));
        Assert.Contains("/login", ex.Message);
        Assert.False(backup.Called);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ClaudeApiOverloadFallsBack()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                ctx.Response.StatusCode = 529;
                ctx.Response.ContentType = "application/json";
                var b = Encoding.UTF8.GetBytes("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""");
                await ctx.Response.OutputStream.WriteAsync(b);
                ctx.Response.Close();
            }
        });

        var backup = new Backup();
        var api = new ClaudeApiClient(new ProviderProfile { Kind = ProviderKind.Anthropic, BaseUrl = $"http://localhost:{port}", ApiKey = "k" });
        var (text, chunks) = await Run(Chain(api, "Claude API", backup));
        Assert.True(backup.Called);
        Assert.Equal("backup answer from llama3.2", text);
        Assert.Contains(chunks, c => c.Notice?.Contains("Claude API couldn't be reached") == true);
        listener.Stop();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
