using System.Text.Json.Nodes;
using Locus.Core;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class ToolSelectionTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-sel-").FullName;
    }

    private sealed class Fake(string name) : ITool
    {
        public string Name => name;
        public string DisplayName => name;
        public string Icon => "";
        public string Description => "does " + name;
        public JsonObject Parameters => Schema.Object();
        public bool RequiresApproval => false;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult(name + " ran"));
    }

    private static readonly string[] Many =
    [
        "calculate", "get_current_datetime", "web_search", "fetch_url", "save_memory", "recall_memory", "message_bot",
        "gmail_search", "gmail_read", "gmail_send", "outlook_search", "outlook_read", "calendar_events", "outlook_calendar",
        "sheets_read", "excel_read", "onedrive_search", "drive_search", "docs_create", "browser_open", "browser_click",
        "run_python", "generate_image", "call_phone", "find_contact", "todo", "add_routine", "github_create_issue",
    ];

    private sealed class FakeProvider(IReadOnlyList<ITool> tools) : IToolProvider
    {
        public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct) => Task.FromResult(tools);
    }

    private static List<ITool> Tools(params string[] names) => names.Select(n => (ITool)new Fake(n)).ToList();
    private static HashSet<string> Names(IEnumerable<ITool> t) => t.Select(x => x.Name).ToHashSet();

    [Fact]
    public void OffersCoreToolsPlusWhatTheChatIsAbout()
    {
        var (offered, notLoaded) = ToolSelector.Select(Tools(Many), "what's 2+2", new HashSet<string>(), new HashSet<string>());
        Assert.Equal(["calculate", "get_current_datetime", "web_search", "fetch_url", "save_memory", "recall_memory", "message_bot"], offered.Select(t => t.Name));
        Assert.Contains(notLoaded, g => g.Name == "email");
        Assert.Contains(notLoaded, g => g.Name == "integrations" && g.Summary.Contains("github_create_issue"));

        var email = Names(ToolSelector.Select(Tools(Many), "any unread email from Shield AI?", new HashSet<string>(), new HashSet<string>()).offered);
        Assert.Superset(new HashSet<string> { "gmail_search", "gmail_read", "gmail_send", "outlook_search", "outlook_read" }, email);
        Assert.DoesNotContain("browser_open", email);
        Assert.DoesNotContain("run_python", email);

        // Used recently: stays offered for follow-ups ("and the second one?").
        var followUp = Names(ToolSelector.Select(Tools(Many), "and the second one?", new HashSet<string> { "excel_read" }, new HashSet<string>()).offered);
        Assert.Contains("sheets_read", followUp);
        // Integrations come in when named.
        Assert.Contains("github_create_issue", Names(ToolSelector.Select(Tools(Many), "open a GitHub issue for this", new HashSet<string>(), new HashSet<string>()).offered));
        // A small set isn't trimmed.
        Assert.Equal(5, ToolSelector.Select(Tools("calculate", "gmail_send", "run_python", "browser_open", "todo"), "hi", new HashSet<string>(), new HashSet<string>()).offered.Count);
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;

    public ToolSelectionTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        // Pretend email, calendar, Office, browser, phone… are connected.
        services.AddSingleton<IToolProvider>(new FakeProvider(Tools(Many.Except(ToolSelector.Core).ToArray())));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryEnabled = false;
        s.SkillLearning.Enabled = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    private async Task<Conversation> AskAsync(string text, IReadOnlyList<ITool>? extra = null)
    {
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text });
        await _sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask,
            CancellationToken.None, extraTools: extra);
        return conv;
    }

    [Fact]
    public async Task TheModelCanLoadAGroupItWasntOffered()
    {
        var requests = new List<LlmRequest>();
        _team.Script = (_, messages, req) =>
        {
            requests.Add(req);
            var results = messages.Count(m => m.Role == "tool");
            return results switch
            {
                0 => ScriptedTeam.Call("load_tools", new { group = "email" }),
                1 => ScriptedTeam.Call("gmail_search", new { query = "x" }),
                _ => new LlmChunk("done"),
            };
        };
        var conv = await AskAsync("hello there");

        Assert.True(requests[0].Tools!.All(t => t.Name != "gmail_search"), string.Join(",", requests[0].Tools!.Select(t => t.Name)));
        Assert.Contains(requests[0].Tools!, t => t.Name == "load_tools");
        Assert.Contains(requests[1].Tools!, t => t.Name == "gmail_search"); // loaded for the next step
        var reply = conv.Messages.Last();
        Assert.EndsWith("gmail_search ran", reply.ToolCalls[1].Result); // labelled as untrusted email content
        Assert.Equal("done", reply.Content);
    }

    [Fact]
    public async Task CutOffRepliesAreContinued()
    {
        var calls = 0;
        _team.Script = (_, messages, req) => ++calls switch
        {
            1 => new LlmChunk("The plan has three parts: first, research the", Notice: OpenAICompatibleClient.CutOffNotice(4096)),
            2 => Assert.IsType<LlmChunk>(messages[^1].Content.Contains("cut off by the length limit") && messages[^2].Role == "assistant" && req.Tools is null
                ? new LlmChunk(" companies; second, apply; third, follow up.") : new LlmChunk("WRONG")),
            _ => new LlmChunk("extra"),
        };
        var conv = await AskAsync("make me a plan");
        var reply = conv.Messages.Last();
        Assert.Equal("The plan has three parts: first, research the companies; second, apply; third, follow up.", reply.Content);
        Assert.Null(reply.Notice);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ContinuingCanBeTurnedOff()
    {
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.ContinueCutOffReplies = false;
        await store.SaveAsync(s);
        _team.Script = (_, _, _) => new LlmChunk("partial", Notice: OpenAICompatibleClient.CutOffNotice(4096));
        var reply = (await AskAsync("make me a plan")).Messages.Last();
        Assert.Equal("partial", reply.Content);
        Assert.Contains("cut off", reply.Notice);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
