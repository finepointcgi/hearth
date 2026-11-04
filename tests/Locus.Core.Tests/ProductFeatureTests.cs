using System.IO.Compression;
using System.Net;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class ProductFeatureTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-prod-").FullName;
    }

    /// <summary>Captures outgoing HTTP requests (for ntfy).</summary>
    private sealed class CaptureHttp : IHttpClientFactory
    {
        public readonly List<(HttpRequestMessage req, string body)> Sent = [];
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(CaptureHttp owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
                lock (owner.Sent) owner.Sent.Add((r, body));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private ServiceProvider? _sp;

    private ServiceProvider Services(IHttpClientFactory? http = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        if (http is not null) services.Replace(ServiceDescriptor.Singleton(http));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = true;
        store.SaveAsync(s).GetAwaiter().GetResult();
        return _sp;
    }

    [Fact]
    public void PricesClaudeAndTreatsLocalAsFree()
    {
        var api = new ProviderProfile { Kind = ProviderKind.Anthropic };
        Assert.Equal(4m * 1 + 20m * 0.5m, Pricing.Estimate(api, "claude-opus-5-5", 1_000_000, 500_000).cost);
        Assert.Equal(0.10m, Pricing.Estimate(api, "claude-haiku-5-5", 1_000_000, 0).cost);
        Assert.Null(Pricing.Estimate(api, "some-future-model", 10, 10).cost);
        Assert.Equal((null, true), Pricing.Estimate(new ProviderProfile { Kind = ProviderKind.ClaudeCode }, "haiku", 100, 100));
        Assert.Equal(0m, Pricing.Estimate(new ProviderProfile { Kind = ProviderKind.Ollama }, "llama", 100, 100).cost);
        var router = new ProviderProfile { Kind = ProviderKind.OpenAICompatible, InputPricePerMillion = 1m, OutputPricePerMillion = 2m };
        Assert.Equal(3m, Pricing.Estimate(router, "x", 1_000_000, 1_000_000).cost);
    }

    private sealed class FixedClient(LlmStats stats) : ILlmClient
    {
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);
        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(20, ct);
            yield return new LlmChunk("hi", Stats: stats);
        }
    }

    [Fact]
    public async Task MetersEveryCallWithItsScope()
    {
        var store = new UsageStore(_paths);
        var client = new MeteredLlmClient(new FixedClient(new LlmStats(1200, 300, 50)), new ProviderProfile { Name = "Claude", Kind = ProviderKind.Anthropic }, store);
        using (UsageScope.Begin("Routine", "bot1", "conv1"))
            await foreach (var _ in client.StreamChatAsync(new LlmRequest("claude-sonnet-5-5", [LlmMessage.User("x")], 0, 10, 0, false))) { }
        await Task.Delay(200);
        var rows = await store.SinceAsync(DateTimeOffset.Now.AddMinutes(-1));
        var r = Assert.Single(rows);
        Assert.Equal(("Routine", "bot1", "conv1", 1200, 300), (r.Source, r.BotId, r.ConversationId, r.InputTokens, r.OutputTokens));
        Assert.Equal((1200 * 2m + 300 * 10m) / 1_000_000m, r.Cost!.Value, 6);
        Assert.True(r.Milliseconds >= 15);
    }

    [Fact]
    public async Task RepliesKeepATrace()
    {
        var sp = Services();
        _team.Script = (_, messages, _) => messages.Any(m => m.Role == "tool") ? new LlmChunk("4") : ScriptedTeam.Call("calculate", new { expression = "2+2" });
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "2+2?" });
        var reply = await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(["model", "tool", "model"], reply!.Trace.Select(t => t.Kind));
        Assert.Equal("calculate", reply.Trace[1].Name);
    }

    [Fact]
    public async Task NotificationsAreTerseUnlessDetailsAreOn()
    {
        var http = new CaptureHttp();
        var sp = Services(http);
        var settings = sp.GetRequiredService<SettingsStore>();
        var s = await settings.GetAsync();
        s.Notifications = new NotificationSettings { Enabled = true, Server = "https://ntfy.test", Topic = "locus-secret", OpenUrl = "https://mac.ts.net", Token = "tk_123" };
        await settings.SaveAsync(s);
        Assert.DoesNotContain("tk_123", await File.ReadAllTextAsync(Path.Combine(_paths.DataDirectory, "settings.json"))); // token encrypted

        var notifier = sp.GetRequiredService<PushNotifier>();
        notifier.EnsureStarted();
        var heartbeatAlert = typeof(HeartbeatRunner).GetField("Alert", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(sp.GetRequiredService<HeartbeatRunner>()) as Action<string, string>;
        heartbeatAlert!("conv9", "Your bank balance is low");
        await Task.Delay(300);
        var (req, body) = Assert.Single(http.Sent);
        Assert.Equal("https://ntfy.test/locus-secret", req.RequestUri!.ToString());
        Assert.Equal("https://mac.ts.net/c/conv9", req.Headers.GetValues("Click").Single());
        Assert.Equal("Bearer tk_123", req.Headers.Authorization!.ToString());
        Assert.DoesNotContain("bank", body); // details stay in Locus by default
    }

    [Fact]
    public async Task BackupRestoresEverythingAtNextStart()
    {
        var sp = Services();
        await sp.GetRequiredService<BotStore>().SaveAsync(new Bot { Name = "Keeper" });
        var settingsStore = sp.GetRequiredService<SettingsStore>();
        var withSecret = await settingsStore.GetAsync();
        withSecret.Providers[0].ApiKey = "sk-secret-restore";
        await settingsStore.SaveAsync(withSecret);
        await sp.GetRequiredService<MemoryStore>().AddAsync("Likes tea");
        var backups = sp.GetRequiredService<BackupService>();
        using var ms = new MemoryStream();
        await backups.WriteAsync(ms);
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray())))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "bots.json");
            Assert.Contains(zip.Entries, e => e.FullName == "vault.key");
            Assert.Contains(zip.Entries, e => e.FullName == "locus.db");
        }

        // A different machine: fresh folder, stage the backup, "restart".
        var other = new TempPaths();
        await File.WriteAllTextAsync(Path.Combine(other.DataDirectory, "bots.json"), "[]");
        var otherBackups = new BackupService(other, new SettingsStore(other));
        ms.Position = 0;
        Assert.Null(await otherBackups.StageRestoreAsync(ms));
        var aside = BackupService.ApplyPendingRestore(other.DataDirectory);
        Assert.NotNull(aside);
        Assert.Equal("[]", await File.ReadAllTextAsync(Path.Combine(aside!, "bots.json"))); // old data kept aside
        Assert.Contains("Keeper", await File.ReadAllTextAsync(Path.Combine(other.DataDirectory, "bots.json")));
        using var restored = new SqliteMemoryProvider(other, NoEmbedder.Instance);
        Assert.Equal("Likes tea", Assert.Single(await restored.ListAsync(null)).Text);
        Assert.False(File.Exists(Path.Combine(other.DataDirectory, BackupService.PendingRestore)));
        Assert.Equal("sk-secret-restore", (await new SettingsStore(other).GetAsync()).Providers[0].ApiKey); // secrets still decrypt

        Assert.Equal("That file isn't a valid zip.", await otherBackups.StageRestoreAsync(new MemoryStream("nope"u8.ToArray())));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(other.DataDirectory, true); } catch { }
    }

    [Fact]
    public void CatalogBuildsServerConfigs()
    {
        var gh = McpCatalog.All.Single(i => i.Id == "github");
        Assert.False(gh.IsComplete(new Dictionary<string, string>()));
        var c = gh.Build(new Dictionary<string, string> { ["Personal access token"] = "github_pat_x" });
        Assert.Equal(("https://api.githubcopilot.com/mcp/", "Bearer github_pat_x"), (c.Url, c.Headers["Authorization"]));
        var ha = McpCatalog.All.Single(i => i.Id == "home-assistant").Build(new Dictionary<string, string>
            { ["Home Assistant address"] = "http://ha.local:8123/", ["Long-lived access token"] = "abc" });
        Assert.Equal("http://ha.local:8123/api/mcp", ha.Url);
        var files = McpCatalog.All.Single(i => i.Id == "files").Build(new Dictionary<string, string> { ["Folder"] = "/Users/me/My Notes" });
        Assert.EndsWith("\"/Users/me/My Notes\"", files.Arguments);
        Assert.All(McpCatalog.All.Where(i => i.Command == "npx"), i => Assert.Matches(@"@\d", i.Arguments)); // pinned versions
    }

    [Fact]
    public async Task ChecksJudgeAnswersWithoutSideEffects()
    {
        var sp = Services();
        var bot = new Bot { Name = "Mathy", Title = "Math" };
        await sp.GetRequiredService<BotStore>().SaveAsync(bot);
        _team.Script = (who, messages, _) =>
            messages[0].Content.StartsWith("You grade") ? new LlmChunk(messages[^1].Content.Contains("386") ? "PASS: right number" : "FAIL: wrong number")
            : new LlmChunk("About $386.66 a month.");
        var runner = sp.GetRequiredService<BotCheckRunner>();
        var before = (await sp.GetRequiredService<ConversationStore>().ListAsync()).Count;

        var good = await runner.RunAsync(bot, new BotCheck { Question = "Payment?", Expect = "gives about $386.66", MustInclude = "386" });
        Assert.True(good.Pass);
        Assert.Equal("right number", good.Reason);
        var missing = await runner.RunAsync(bot, new BotCheck { Question = "Payment?", MustInclude = "412" });
        Assert.False(missing.Pass);
        Assert.Contains("412", missing.Reason);

        await Task.Delay(300);
        Assert.Equal(before, (await sp.GetRequiredService<ConversationStore>().ListAsync()).Count); // throwaway chats removed
        Assert.Empty(await sp.GetRequiredService<MemoryStore>().ListAsync(bot.Id)); // no memory learned
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writers */ }
    }
}
