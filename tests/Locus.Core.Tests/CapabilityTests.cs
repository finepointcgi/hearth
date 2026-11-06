using System.Text.Json.Nodes;
using Locus.Core;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class CapabilityTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-cap-").FullName;
    }

    /// <summary>A tool that changes something (asks first) and one that reads the web.</summary>
    private sealed class Fake(string name, string result, bool approval) : ITool
    {
        public string Name => name;
        public string DisplayName => name;
        public string Icon => "";
        public string Description => "does " + name;
        public JsonObject Parameters => Schema.Object();
        public bool RequiresApproval => approval;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult(result));
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;
    private readonly AppSettings _settings;

    public CapabilityTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        _settings = store.GetAsync().GetAwaiter().GetResult();
        _settings.Providers =
        [
            new ProviderProfile { Name = "local", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "qwen" },
            new ProviderProfile { Name = "claude", Kind = ProviderKind.OpenAICompatible, BaseUrl = "http://127.0.0.1:9", DefaultModel = "haiku" },
        ];
        _settings.ActiveProviderId = _settings.Providers[0].Id;
        _settings.AutoTitle = false;
        _settings.MemoryEnabled = false;
        _settings.SkillLearning.Enabled = false;
        store.SaveAsync(_settings).GetAwaiter().GetResult();
    }

    private async Task Save(Action<AppSettings> change)
    {
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        change(s);
        await store.SaveAsync(s);
    }

    private async Task<ChatMessage> AskAsync(string text, Bot? bot = null, IReadOnlyList<ITool>? extra = null, Func<ChatMessage, Task>? onUpdate = null,
        Conversation? conv = null)
    {
        conv ??= new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text });
        return (await _sp.GetRequiredService<ChatService>().RunAgentAsync(conv, bot, new SendOptions("qwen", false, false, true),
            () => onUpdate?.Invoke(conv.Messages.Last()) ?? Task.CompletedTask, CancellationToken.None, extraTools: extra))!;
    }

    [Fact]
    public async Task StarterSkillsAreAddedOnceAndDontReplaceYours()
    {
        var skills = _sp.GetRequiredService<SkillStore>();
        await skills.SaveAsync(new Skill { Name = "meal-plan", Instructions = "my own way" });
        Assert.Equal(StarterSkills.All.Count - 1, await StarterSkills.AddMissingAsync(skills));
        Assert.Equal(0, await StarterSkills.AddMissingAsync(skills));
        var all = await skills.ListAsync();
        Assert.Equal("my own way", all.Single(s => s.Name == "meal-plan").Instructions);
        var pipeline = all.Single(s => s.Name == "job-pipeline");
        Assert.Equal(StarterSkills.Source, pipeline.Source);
        Assert.False(pipeline.Pending);
        Assert.StartsWith("Goal: move the user's job search forward", pipeline.Instructions); // indentation trimmed
    }

    [Fact]
    public void RouterSendsHardWorkToTheStrongModel()
    {
        Assert.False(ModelRouter.Decide("hey, what's up?", 0, false, false).strong);
        Assert.False(ModelRouter.Decide("remind me to call mom at 5", 0, false, false).strong);
        Assert.True(ModelRouter.Decide("Research the best CRM for a 5-person team", 0, false, false).strong);
        Assert.True(ModelRouter.Decide("write me a cover letter for this role", 0, false, false).strong);
        Assert.True(ModelRouter.Decide("what's this?", 1, false, false).strong);
        Assert.True(ModelRouter.Decide("fix it", 0, true, false).strong);
        Assert.True(ModelRouter.Decide("1. book flights\n2. find a hotel\n3. rent a car", 0, false, false).strong);
    }

    [Fact]
    public async Task RoutedRepliesRunOnTheChosenServer()
    {
        await Save(s => s.ModelRouting = new ModelRoutingSettings
        {
            Enabled = true, FastProviderId = s.Providers[0].Id, FastModel = "qwen", StrongProviderId = s.Providers[1].Id, StrongModel = "haiku",
        });
        _team.Script = (_, _, req) => new LlmChunk("answer from " + req.Model);
        var quick = await AskAsync("hi there");
        Assert.Equal("answer from qwen", quick.Content);
        var hard = await AskAsync("Analyze the pros and cons of these two job offers");
        Assert.Equal("answer from haiku", hard.Content);
        Assert.Contains(_team.Calls, c => c is { server: "claude", model: "haiku" });
        Assert.Contains("strong model", hard.Trace.Single(t => t.Kind == "route").Name);
        // A bot pinned to a model keeps it.
        var pinned = await AskAsync("Analyze this in depth", new Bot { Name = "Pinned", Model = "qwen" });
        Assert.Equal("answer from qwen", pinned.Content);
    }

    [Fact]
    public async Task AlwaysAllowStopsAskingForThatBotOnly()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var scout = new Bot { Name = "Scout" };
        var other = new Bot { Name = "Other" };
        await bots.SaveAsync(scout);
        await bots.SaveAsync(other);
        var tool = new Fake("sheets_write", "saved", approval: true); // a tool in a permission area ("sheets") that asks by default
        _team.Script = (_, m, _) => m.Any(x => x.Role == "tool") ? new LlmChunk("done") : ScriptedTeam.Call("sheets_write", new { });
        var asked = 0;
        async Task Approve(ChatMessage reply, bool always)
        {
            if (reply.ToolCalls.FirstOrDefault(t => t.Status == ToolCallStatus.AwaitingApproval) is not { } tc || tc.Approval is null) return;
            asked++;
            Assert.Equal(always ? "Scout" : tc.AlwaysAllowFor, tc.AlwaysAllowFor);
            if (always) await tc.AlwaysAllow!();
            tc.Approval.TrySetResult(true);
        }

        await AskAsync("save it", scout, [tool], r => Approve(r, always: true));
        Assert.Equal(1, asked);
        var second = await AskAsync("save it again", scout, [tool], r => Approve(r, always: false));
        Assert.Equal(1, asked); // didn't ask this time
        Assert.Equal(ToolCallStatus.Done, second.ToolCalls.Single().Status);
        await AskAsync("save it", other, [tool], r => Approve(r, always: false));
        Assert.Equal(2, asked); // another bot still asks
        Assert.Equal(["sheets_write"], (await _sp.GetRequiredService<SettingsStore>().GetAsync()).AlwaysAllow[scout.Id]);
        Assert.False(ChatService.AlwaysAllowable("call_phone"));
    }

    [Fact]
    public async Task AnswersAreCheckedAgainstTheSourcesAndFixed()
    {
        var web = new Fake("drive_read", "Acme Pro costs $10 per user per month.", approval: false);
        var calls = 0;
        _team.Script = (_, m, _) =>
        {
            calls++;
            var last = m[^1].Content;
            if (last.Contains("fact-check your answer")) return new LlmChunk("Price: the page says $10, not $12.");
            if (last.Contains("Rewrite your answer")) return new LlmChunk("Acme Pro costs $10 per user per month [1].");
            return m.Any(x => x.Role == "tool") ? new LlmChunk("Acme Pro costs $12 per user per month [1].") : ScriptedTeam.Call("drive_read", new { });
        };
        var reply = await AskAsync("how much is Acme Pro?", extra: [web]);
        Assert.True(calls == 4, $"calls={calls} notice={reply.Notice} trace={string.Join(",", reply.Trace.Select(t => t.Name))} tools={string.Join(",", reply.ToolCalls.Select(t => t.Name + ":" + t.Status))}");
        Assert.Equal("Acme Pro costs $10 per user per month [1].", reply.Content);
        Assert.Contains("says $10, not $12", reply.Checked);
        Assert.Equal(4, calls); // call, answer, check, rewrite

        // A supported answer is left alone; a reply without sources isn't checked.
        calls = 0;
        _team.Script = (_, m, _) =>
        {
            calls++;
            if (m[^1].Content.Contains("fact-check")) return new LlmChunk("OK");
            return m.Any(x => x.Role == "tool") ? new LlmChunk("It's $10.") : ScriptedTeam.Call("drive_read", new { });
        };
        var fine = await AskAsync("how much?", extra: [web]);
        Assert.Equal(("It's $10.", (string?)null, 3), (fine.Content, fine.Checked, calls));
        calls = 0;
        _team.Script = (_, _, _) => { calls++; return new LlmChunk("Hello!"); };
        await AskAsync("hi");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PeopleNotesAreKeptAndLookedUp()
    {
        var people = _sp.GetRequiredService<PeopleStore>();
        await people.NoteAsync("Dana Ruiz", "Recruiter at Shield AI; reached out about the Director SE&A role", "person", ["Shield AI"]);
        await people.NoteAsync("dana ruiz", "Prefers calls on Tuesdays");
        await people.NoteAsync("Dana Ruiz", "Prefers calls on Tuesdays"); // duplicate skipped
        var dana = Assert.Single(await people.FindAsync("Dana"));
        Assert.Equal(2, dana.Notes.Count);
        Assert.Equal(["Shield AI"], dana.Related);

        var tools = await new PeopleToolProvider(people).GetToolsAsync(_settings, default);
        var who = (await tools.Single(t => t.Name == "who_is").ExecuteAsync(new JsonObject { ["name"] = "dana" }, null!, default)).Text;
        Assert.Contains("Dana Ruiz (person) — related: Shield AI", who);
        Assert.Contains("Prefers calls on Tuesdays", who);

        string? system = null;
        _team.Script = (_, m, _) => { system = m[0].Content; return new LlmChunk("ok"); };
        await AskAsync("hello");
        Assert.Contains("People and companies you have notes on", system);
        Assert.Contains("Dana Ruiz", system);
    }

    [Fact]
    public async Task LongTasksShowAPlan()
    {
        var conv = new Conversation();
        _team.Script = (_, m, _) => m.Any(x => x.Role == "tool") ? new LlmChunk("Started.")
            : ScriptedTeam.Call("update_plan", new { steps = new object[] { new { step = "Find flights", status = "completed" }, new { step = "Pick a hotel", status = "in_progress" }, new { step = "Book", status = "pending" } } });
        await AskAsync("plan my trip to Austin: flights, hotel, booking", conv: conv);
        Assert.Equal(["Find flights", "Pick a hotel", "Book"], conv.Plan.Select(p => p.Content));
        Assert.Equal(CodeTodoStatus.InProgress, conv.Plan[1].Status);
    }

    [Fact]
    public async Task AudioIsRecognisedAndSaysHowToSetUp()
    {
        Assert.True(AudioTranscriber.IsAudio("memo.m4a"));
        Assert.True(AudioTranscriber.IsAudio("x", "audio/ogg"));
        Assert.False(AudioTranscriber.IsAudio("notes.pdf", "application/pdf"));
        var (ready, how) = await _sp.GetRequiredService<AudioTranscriber>().StatusAsync();
        Assert.False(ready); // no server set, and this test host can't run programs
        Assert.Contains("speech-to-text server", how);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
