using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>API key pools, outgoing webhooks and the skill catalog.</summary>
public sealed class ExtrasTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-extras-").FullName;
    }

    private readonly TempPaths _paths = new();
    private ServiceProvider? _sp;

    // ------------------------------------------------------------------ key pools

    private sealed class KeyClient(string name, Exception? fail = null) : ILlmClient
    {
        public int Calls;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);
        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            if (fail is not null) throw fail;
            yield return new LlmChunk($"from {name}");
        }
    }

    private static async Task<string> AskAsync(ILlmClient c)
    {
        var sb = new StringBuilder();
        await foreach (var ch in c.StreamChatAsync(new LlmRequest("m", [LlmMessage.User("hi")], 0, 10, 0, false))) sb.Append(ch.Content);
        return sb.ToString();
    }

    [Fact]
    public async Task KeyPoolMovesPastRateLimitedKeysAndRestsThem()
    {
        KeyPoolClient.ResetForTests();
        var limited = new KeyClient("a", new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests));
        var good = new KeyClient("b");
        var pool = new KeyPoolClient([("a", limited), ("b", good)]);

        for (var i = 0; i < 4; i++) Assert.Equal("from b", await AskAsync(pool));
        Assert.Equal(1, limited.Calls); // tried once, then resting
        Assert.Equal(4, good.Calls);

        // A bad request isn't a key problem: no retry on the other key.
        KeyPoolClient.ResetForTests();
        var bad = new KeyPoolClient([("x", new KeyClient("x", new LlmException("400 invalid model"))), ("y", new KeyClient("y", new LlmException("400 invalid model")))]);
        await Assert.ThrowsAsync<LlmException>(() => AskAsync(bad));
        Assert.True(KeyPoolClient.IsKeyProblem(new LlmException("Anthropic: rate_limit_error")));
        Assert.False(KeyPoolClient.IsKeyProblem(new LlmException("400 invalid model")));
    }

    [Fact]
    public async Task KeyPoolTakesTurns()
    {
        KeyPoolClient.ResetForTests();
        var a = new KeyClient("a");
        var b = new KeyClient("b");
        var pool = new KeyPoolClient([("a", a), ("b", b)]);
        for (var i = 0; i < 6; i++) await AskAsync(pool);
        Assert.Equal(3, a.Calls);
        Assert.Equal(3, b.Calls);
    }

    [Fact]
    public async Task ExtraKeysAreEncryptedAtRest()
    {
        var store = new SettingsStore(_paths);
        var s = await store.GetAsync();
        s.Providers = [new ProviderProfile { Name = "router", Kind = ProviderKind.OpenAICompatible, ApiKey = "sk-main", ExtraApiKeys = ["sk-extra-1", "sk-extra-2"] }];
        await store.SaveAsync(s);
        var raw = await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "settings.json"));
        Assert.DoesNotContain("sk-extra-1", raw);
        Assert.Equal(["sk-extra-1", "sk-extra-2"], (await new SettingsStore(_paths).GetAsync()).Providers[0].ExtraApiKeys);
    }

    // ------------------------------------------------------------------ webhooks

    private sealed class Capture : IHttpClientFactory
    {
        public readonly List<(HttpRequestMessage req, string body)> Sent = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Capture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
                lock (owner.Sent) owner.Sent.Add((r, body));
                return owner.Respond?.Invoke(r) ?? new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }

    private ServiceProvider Services(IHttpClientFactory http)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton(http));
        _sp = services.BuildServiceProvider();
        return _sp;
    }

    [Fact]
    public async Task WebhooksGetTheirEventsSigned()
    {
        var http = new Capture();
        var sp = Services(http);
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.Webhooks =
        [
            new OutgoingWebhook { Name = "all", Url = "https://hooks.test/all", Secret = "s3cret" },
            new OutgoingWebhook { Name = "tasks only", Url = "https://hooks.test/tasks", Events = ["task"] },
            new OutgoingWebhook { Name = "off", Url = "https://hooks.test/off", Enabled = false },
        ];
        s.Notifications.OpenUrl = "https://mac.ts.net";
        await store.SaveAsync(s);
        Assert.DoesNotContain("s3cret", await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "settings.json")));

        var hooks = sp.GetRequiredService<EventWebhooks>();
        await hooks.FireAsync("follow_up", "A bot followed up", "2 new roles", "conv1");
        await hooks.FireAsync("task", "Task: jobs", "Done", "conv2", new() { ["taskId"] = "t1" });

        Assert.Equal(["https://hooks.test/all", "https://hooks.test/all", "https://hooks.test/tasks"],
            http.Sent.Select(x => x.req.RequestUri!.ToString()).Order());
        var (req, body) = http.Sent.First(x => x.req.RequestUri!.AbsolutePath == "/all" && x.body.Contains("follow_up"));
        var json = JsonNode.Parse(body)!;
        Assert.Equal(("follow_up", "2 new roles", "https://mac.ts.net/c/conv1"), (json["event"]!.ToString(), json["text"]!.ToString(), json["link"]!.ToString()));
        Assert.Equal("sha256=" + EventWebhooks.Sign("s3cret", body), req.Headers.GetValues("X-Hearth-Signature").Single());
        Assert.Equal("t1", JsonNode.Parse(http.Sent.First(x => x.req.RequestUri!.AbsolutePath == "/tasks").body)!["taskId"]!.ToString());
        Assert.False(http.Sent.Any(x => x.req.RequestUri!.AbsolutePath == "/tasks" && x.body.Contains("follow_up")));

        http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Equal("answered 500", await hooks.SendAsync(s.Webhooks[0], "test", "{}"));
        Assert.Contains("answered 500", hooks.LastResult[s.Webhooks[0].Id]);
    }

    // ------------------------------------------------------------------ catalog

    [Fact]
    public async Task CatalogListsSkillFoldersAndDescriptions()
    {
        var http = new Capture
        {
            Respond = r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(r.RequestUri!.Host == "api.github.com"
                    ? """{"tree":[{"path":"skills/research/arxiv/SKILL.md"},{"path":"skills/research/arxiv/script.py"},{"path":"skills/pdf/SKILL.md"},{"path":"other/x/SKILL.md"}]}"""
                    : "---\nname: arxiv\ndescription: Search arXiv papers\n---\n\nSteps.", Encoding.UTF8, "application/json"),
            },
        };
        var sp = Services(http);
        var catalog = sp.GetRequiredService<SkillCatalog>();
        var skills = await catalog.ListAsync("Hermes");
        Assert.Equal([("pdf", ""), ("arxiv", "research")], skills.Select(s => (s.Name, s.Category)));
        Assert.Equal("https://github.com/NousResearch/hermes-agent/tree/HEAD/skills/research/arxiv", skills[1].GitHubUrl);
        Assert.Equal("Search arXiv papers", await catalog.DescriptionAsync(skills[1]));
        await catalog.ListAsync("Hermes");
        Assert.Single(http.Sent, x => x.req.RequestUri!.Host == "api.github.com"); // cached
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
