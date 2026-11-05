using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

/// <summary>The OpenAI-compatible API and the ACP editor bridge.</summary>
public sealed class ApiTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-api-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private ServiceProvider? _sp;

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
        return _sp;
    }

    [Fact]
    public async Task BotsAreModelsAndAnswersStream()
    {
        var sp = Services();
        await sp.GetRequiredService<BotStore>().SaveAsync(new Bot { Name = "Chief of Staff", Title = "Coordinator" });
        string? system = null, lastUser = null;
        _team.Script = (bot, msgs, _) =>
        {
            system = msgs[0].Content;
            lastUser = msgs.Last().Content;
            return new LlmChunk(bot == "Chief of Staff" ? "Hello from the chief." : "Hello from Locus.");
        };
        var api = sp.GetRequiredService<OpenAiApi>();
        Assert.Equal(["locus", "locus/chief-of-staff"], (await api.ModelsAsync()).Select(m => m.id));

        var deltas = new StringBuilder();
        var answer = await api.CompleteAsync("locus/chief-of-staff",
            [new ApiMessage("system", "Answer in English."), new ApiMessage("user", "Hi"), new ApiMessage("assistant", "Hello"), new ApiMessage("user", "Again")],
            d => { deltas.Append(d); return Task.CompletedTask; }, default);
        Assert.Equal("Hello from the chief.", answer);
        Assert.Equal(answer, deltas.ToString());
        Assert.StartsWith("You are Chief of Staff", system);
        Assert.Equal("Again", lastUser);
        Assert.Empty(await sp.GetRequiredService<ConversationStore>().ListAsync()); // no chat kept

        Assert.Equal("Hello from Locus.", await api.CompleteAsync("locus", [new ApiMessage("user", "Hi")], null, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.CompleteAsync("locus/nobody", [new ApiMessage("user", "Hi")], null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => api.CompleteAsync("locus", [new ApiMessage("system", "x")], null, default));
    }

    [Fact]
    public async Task KeyIsCheckedAndEncrypted()
    {
        var sp = Services();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.Api = new ApiSettings { Enabled = true, Key = ApiSettings.NewKey() };
        await store.SaveAsync(s);
        Assert.DoesNotContain(s.Api.Key, await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "settings.json")));

        Assert.True(s.Api.Accepts("Bearer " + s.Api.Key));
        Assert.False(s.Api.Accepts("Bearer wrong"));
        Assert.False(s.Api.Accepts(null));
        s.Api.Enabled = false;
        Assert.False(s.Api.Accepts("Bearer " + s.Api.Key)); // off means off
    }

    /// <summary>Fake Locus API returning a streamed answer, recording what it was sent.</summary>
    private sealed class FakeLocus : HttpMessageHandler
    {
        public readonly List<JsonObject> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!.AsObject();
            lock (Requests) Requests.Add(body);
            if (r.Headers.Authorization?.Parameter != "k") return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Missing or wrong API key."}}""") };
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static async Task<List<JsonObject>> RunBridgeAsync(FakeLocus locus, string key, params string[] lines)
    {
        var output = new StringWriter();
        var bridge = new AcpBridge(new HttpClient(locus), "http://locus.test", key, "locus/engineer", output);
        foreach (var line in lines) await bridge.HandleAsync(JsonNode.Parse(line)!.AsObject());
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
    }

    [Fact]
    public async Task EditorBridgeSpeaksAcp()
    {
        var locus = new FakeLocus();
        var output = new StringWriter();
        var bridge = new AcpBridge(new HttpClient(locus), "http://locus.test", "k", "locus/engineer", output);
        await bridge.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}""")!.AsObject());
        await bridge.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/Users/me/project","mcpServers":[]}}""")!.AsObject());
        var messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(1, messages[0]["result"]!["protocolVersion"]!.GetValue<int>());
        var session = messages[1]["result"]!["sessionId"]!.ToString();

        foreach (var (id, text) in new[] { (3, "Fix the bug"), (4, "Thanks") })
            await bridge.HandleAsync(JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"session/prompt","params":{"sessionId":"{{{session}}}","prompt":[{"type":"text","text":"{{{text}}}"},{"type":"resource","resource":{"uri":"file:///p/a.cs","text":"class A {}"}}]}}""")!.AsObject());
        messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

        var chunks = messages.Where(m => m["method"]?.ToString() == "session/update").Select(m => m["params"]!["update"]!).ToList();
        Assert.All(chunks, c => Assert.Equal("agent_message_chunk", c["sessionUpdate"]!.ToString()));
        Assert.Equal("HelloHello", string.Concat(chunks.Select(c => c["content"]!["text"]!.ToString())));
        Assert.Equal(2, messages.Count(m => m["result"]?["stopReason"]?.ToString() == "end_turn"));

        // The second request carries the whole conversation; the first mentions the folder and the attached file.
        var second = locus.Requests[1]["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], second.Select(m => m!["role"]!.ToString()));
        Assert.Contains("/Users/me/project", second[0]!["content"]!.ToString());
        Assert.Contains("class A {}", second[0]!["content"]!.ToString());
        Assert.Equal("Hello", second[1]!["content"]!.ToString());
        Assert.Equal("locus/engineer", locus.Requests[0]["model"]!.ToString());
    }

    [Fact]
    public async Task EditorBridgeReportsAWrongKey()
    {
        var messages = await RunBridgeAsync(new FakeLocus(), "wrong",
            """{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/p","mcpServers":[]}}""");
        var session = messages[0]["result"]!["sessionId"]!.ToString();
        var output = new StringWriter();
        var bridge = new AcpBridge(new HttpClient(new FakeLocus()), "http://locus.test", "wrong", "locus", output);
        await bridge.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"session/new","params":{"cwd":"/p","mcpServers":[]}}""")!.AsObject());
        var sid = JsonNode.Parse(output.ToString().Split('\n')[0])!["result"]!["sessionId"]!.ToString();
        await bridge.HandleAsync(JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":2,"method":"session/prompt","params":{"sessionId":"{{{sid}}}","prompt":[{"type":"text","text":"hi"}]}}""")!.AsObject());
        Assert.Contains("wrong API key", output.ToString());
        Assert.Contains("\"stopReason\":\"refusal\"", output.ToString());
        Assert.NotEmpty(session);
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
