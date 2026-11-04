using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Services.Channels;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public class PermissionTests
{
    private sealed class FakeTool(string name, bool approval) : ITool
    {
        public string Name => name;
        public string DisplayName => name;
        public string Icon => "";
        public string Description => "";
        public JsonObject Parameters => [];
        public bool RequiresApproval => approval;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult("ok"));
    }

    private sealed class SandboxAware() : ITool, IContextualApproval
    {
        public string Name => "desktop_click";
        public string DisplayName => "";
        public string Icon => "";
        public string Description => "";
        public JsonObject Parameters => [];
        public bool RequiresApproval => true;
        public bool RequiresApprovalFor(ToolContext ctx) => ctx.Bot?.Abilities.Desktop != DesktopTarget.Sandbox;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult("ok"));
    }

    private static ToolContext Ctx(Bot? bot) => new()
    {
        Conversation = new Conversation(), Reply = new ChatMessage(), Settings = new AppSettings(), Client = null!, Model = "m", Bot = bot,
    };

    [Fact]
    public void LevelsOverrideToolDefaults()
    {
        var s = new AppSettings();
        var send = new FakeTool("gmail_send", true);
        var search = new FakeTool("web_search", false);
        Assert.True(PermissionCatalog.NeedsApproval(send, s));
        Assert.False(PermissionCatalog.NeedsApproval(search, s));

        s.Permissions[PermissionCatalog.Key(PermissionCatalog.Classify(search).area, false)] = PermissionLevel.Ask;
        Assert.True(PermissionCatalog.NeedsApproval(search, s));

        var (area, write) = PermissionCatalog.Classify(send);
        s.Permissions[PermissionCatalog.Key(area, write)] = PermissionLevel.Allow;
        Assert.False(PermissionCatalog.NeedsApproval(send, s));
        s.Permissions[PermissionCatalog.Key(area, write)] = PermissionLevel.Block;
        Assert.True(PermissionCatalog.IsBlocked(send, s));
    }

    [Fact]
    public void SandboxedDesktopSkipsApprovalOnlyForSandboxBots()
    {
        var s = new AppSettings();
        var tool = new SandboxAware();
        var sandboxBot = new Bot { Name = "Runner", Abilities = new() { ComputerUse = true, Desktop = DesktopTarget.Sandbox } };
        var hostBot = new Bot { Name = "Helper", Abilities = new() { ComputerUse = true } };
        Assert.False(PermissionCatalog.NeedsApproval(tool, s, Ctx(sandboxBot)));
        Assert.True(PermissionCatalog.NeedsApproval(tool, s, Ctx(hostBot)));
        Assert.True(PermissionCatalog.NeedsApproval(tool, s)); // no context: the safe default
        // An explicit "Ask" still wins, even in a sandbox.
        s.Permissions[PermissionCatalog.Key(PermissionCatalog.Classify(tool).area, PermissionCatalog.Classify(tool).write)] = PermissionLevel.Ask;
        Assert.True(PermissionCatalog.NeedsApproval(tool, s, Ctx(sandboxBot)));
    }
}

public class InboxAndSlackTests
{
    [Theory]
    [InlineData("Me@Example.com", true)]
    [InlineData("boss@company.com", true)]
    [InlineData("intern@sub.company.com", false)]
    [InlineData("stranger@evil.test", false)]
    [InlineData("", false)]
    public void InboxAllowlist(string sender, bool allowed) =>
        Assert.Equal(allowed, BotInbox.IsAllowed(sender, ["me@example.com", "@company.com"]));

    [Fact]
    public void InboxAllowsNobodyByDefault() => Assert.False(BotInbox.IsAllowed("me@example.com", []));

    [Fact]
    public void SlackMarkdownConversion()
    {
        var m = SlackAdapter.ToMrkdwn("## Plan\n**Bold** & <x> [site](https://a.test)\n- one\n~~old~~");
        Assert.Contains("*Plan*", m);
        Assert.Contains("*Bold* &amp; &lt;x&gt;", m);
        Assert.Contains("<https://a.test|site>", m);
        Assert.Contains("• one", m);
        Assert.Contains("~old~", m);
        Assert.Equal("see https://a.test & x<y", SlackAdapter.FromMrkdwn("see <https://a.test> &amp; x&lt;y"));
        Assert.Equal("mail me", SlackAdapter.FromMrkdwn("<mailto:a@b.c|mail me>"));
    }

    [Fact]
    public void SlackTokensMustBePaired()
    {
        Assert.Equal(("xapp-1", "xoxb-2"), SlackAdapter.SplitToken("xapp-1|xoxb-2"));
        Assert.Throws<InvalidOperationException>(() => SlackAdapter.SplitToken("xoxb-2"));
        Assert.Throws<InvalidOperationException>(() => SlackAdapter.SplitToken("xoxb-2|xapp-1"));
    }

    [Fact]
    public void SlackEventsAreFiltered()
    {
        var a = new SlackAdapter(null!);
        JsonNode E(string json) => JsonNode.Parse(json)!;
        var dm = a.ParseEvent(E("""{"type":"message","channel_type":"im","user":"U1","channel":"D1","text":"hi &amp; bye"}"""));
        Assert.NotNull(dm);
        Assert.True(dm!.IsDm);
        Assert.Equal("hi & bye", dm.Text);
        var mention = a.ParseEvent(E("""{"type":"app_mention","user":"U1","channel":"C1","text":"<@UBOT> status?"}"""));
        Assert.True(mention!.Mentioned);
        // Channel chatter without a mention, bot posts and edits are ignored.
        Assert.Null(a.ParseEvent(E("""{"type":"message","channel_type":"channel","user":"U1","channel":"C1","text":"hello"}""")));
        Assert.Null(a.ParseEvent(E("""{"type":"message","channel_type":"im","bot_id":"B1","user":"U2","channel":"D1","text":"echo"}""")));
        Assert.Null(a.ParseEvent(E("""{"type":"message","subtype":"message_changed","channel_type":"im","user":"U1","channel":"D1"}""")));
    }
}

public class TaskRunnerTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-task-").FullName;
    }

    /// <summary>Plays a model that plans, does one step, and finishes — but only inside a task.</summary>
    private sealed class ScriptedModel : ILlmClient, ILlmClientFactory
    {
        public int TaskTurns;
        public ILlmClient Create(ProviderProfile profile) => this;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("fake:1b")]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);

        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (request.Tools?.Any(t => t.Name == "task_plan") != true) { yield return new LlmChunk("ok"); yield break; }
            var last = request.Messages[^1];
            // After a tool result, say a word and stop this turn; the runner starts the next cycle.
            if (last.Role == "tool") { yield return new LlmChunk("Noted."); yield break; }
            var n = Interlocked.Increment(ref TaskTurns);
            yield return n switch
            {
                1 => Call("task_plan", """{"steps":["Find the numbers","Write the summary"]}"""),
                2 => Call("task_step", """{"step":1,"status":"done","note":"Found 3 numbers"}"""),
                _ => Call("task_done", """{"report":"All done: the total is 42."}"""),
            };
        }

        private static LlmChunk Call(string name, string args) => new(ToolCalls: [new LlmToolCall(Guid.NewGuid().ToString("N"), name, args)]);
    }

    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;
    private readonly ScriptedModel _model = new();

    public TaskRunnerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_model));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "fake", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "fake:1b" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RunsAPlanToCompletionInTheBackground()
    {
        var runner = _sp.GetRequiredService<TaskRunner>();
        var tasks = _sp.GetRequiredService<TaskStore>();
        var milestones = new List<string>();
        runner.Milestone += (_, m) => { lock (milestones) milestones.Add(m); };

        var task = await runner.StartAsync("Add up the numbers in my notes", null);
        AgentTask? latest = null;
        for (var i = 0; i < 100 && latest?.State != TaskState.Done; i++)
        {
            await Task.Delay(100);
            latest = await tasks.GetAsync(task.Id);
        }

        Assert.Equal(TaskState.Done, latest!.State);
        Assert.Equal(["Find the numbers", "Write the summary"], latest.Plan.Select(p => p.Text));
        Assert.All(latest.Plan, p => Assert.Equal("done", p.Status));
        Assert.Equal("Found 3 numbers", latest.Plan[0].Note);
        Assert.Contains("42", latest.Result);
        Assert.Contains(latest.Log, e => e.Text.StartsWith("Plan:"));
        lock (milestones) Assert.NotEmpty(milestones);
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writer may still hold a file */ }
    }
}
