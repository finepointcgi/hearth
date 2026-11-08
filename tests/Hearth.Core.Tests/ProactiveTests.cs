using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Bots taking initiative: follow-ups they schedule themselves, handing big jobs to tasks, and the prompt.</summary>
public sealed class ProactiveTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-proactive-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private ServiceProvider? _sp;

    private ServiceProvider Services(bool initiative = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        s.TakeInitiative = initiative;
        store.SaveAsync(s).GetAwaiter().GetResult();
        return _sp;
    }

    private static ToolContext Ctx(IServiceProvider sp, Conversation conv, Bot? bot = null) => new()
    {
        Conversation = conv,
        Reply = new ChatMessage { Role = ChatRole.Assistant },
        Settings = sp.GetRequiredService<SettingsStore>().GetAsync().GetAwaiter().GetResult(),
        Client = null!,
        Model = "m",
        Bot = bot,
    };

    private static async Task<Conversation> ChatAsync(IServiceProvider sp, string question)
    {
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = question });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
        return conv;
    }

    [Fact]
    public async Task BotsScheduleListAndCancelTheirOwnFollowUps()
    {
        var sp = Services();
        var store = sp.GetRequiredService<FollowUpStore>();
        var conv = await ChatAsync(sp, "find me jobs");
        var other = await ChatAsync(sp, "other");
        var before = DateTimeOffset.Now;

        var tool = new FollowUpTool(store);
        var ok = await tool.ExecuteAsync(new JsonObject { ["note"] = "check new postings", ["in_minutes"] = 1, ["repeat"] = "daily" }, Ctx(sp, conv), default);
        Assert.False(ok.IsError);
        var f = Assert.Single(await store.ListAsync());
        Assert.Equal((conv.Id, "check new postings", "daily"), (f.ConversationId, f.Note, f.Repeat));
        Assert.True(f.DueAt >= before + FollowUpTool.Soonest - TimeSpan.FromSeconds(1)); // never sooner than 5 minutes

        Assert.True((await tool.ExecuteAsync(new JsonObject { ["note"] = "x" }, Ctx(sp, conv), default)).IsError); // needs a time
        Assert.True((await tool.ExecuteAsync(new JsonObject { ["note"] = "x", ["in_minutes"] = 60 * 24 * 365 }, Ctx(sp, conv), default)).IsError);
        var at = await tool.ExecuteAsync(new JsonObject { ["note"] = "at a time", ["at"] = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd 09:00") }, Ctx(sp, conv), default);
        Assert.False(at.IsError, at.Text);

        Assert.Contains("check new postings", (await new ListFollowUpsTool(store).ExecuteAsync([], Ctx(sp, conv), default)).Text);
        Assert.Contains("No follow-ups", (await new ListFollowUpsTool(store).ExecuteAsync([], Ctx(sp, other), default)).Text);
        Assert.True((await new CancelFollowUpTool(store).ExecuteAsync(new JsonObject { ["id"] = f.Id }, Ctx(sp, other), default)).IsError); // other chats can't cancel it
        Assert.False((await new CancelFollowUpTool(store).ExecuteAsync(new JsonObject { ["id"] = f.Id }, Ctx(sp, conv), default)).IsError);
        Assert.DoesNotContain(await store.ListAsync(), x => x.Id == f.Id);
    }

    [Fact]
    public async Task DueFollowUpPostsInTheChatOnce()
    {
        var sp = Services();
        _team.Script = (_, msgs, _) => new LlmChunk(msgs.Last().Content.Contains("Follow-up you scheduled") ? "2 new roles at GDLS and Ford." : "?");
        var conv = await ChatAsync(sp, "find me jobs");
        var store = sp.GetRequiredService<FollowUpStore>();
        await store.AddAsync(new FollowUp { ConversationId = conv.Id, Note = "check new postings", DueAt = DateTimeOffset.Now.AddMinutes(-1) });
        var runner = sp.GetRequiredService<FollowUpRunner>();
        string? pushed = null;
        runner.Posted += (_, text) => pushed = text;

        await runner.TickAsync();

        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Equal("2 new roles at GDLS and Ford.", saved!.Messages.Last().Content);
        Assert.Equal("Follow-up", saved.Messages[^2].RoutineName);
        Assert.True(saved.Unread);
        Assert.Equal("2 new roles at GDLS and Ford.", pushed);
        Assert.Empty(await store.ListAsync()); // one-off: gone
        Assert.False(sp.GetRequiredService<ChatRuns>().IsRunning(conv.Id));
    }

    [Fact]
    public async Task RepeatingFollowUpStaysQuietWhenNothingIsNew()
    {
        var sp = Services();
        _team.Script = (_, _, _) => new LlmChunk(FollowUpRunner.NothingNew);
        var conv = await ChatAsync(sp, "watch the price");
        var store = sp.GetRequiredService<FollowUpStore>();
        var due = DateTimeOffset.Now.AddMinutes(-2);
        await store.AddAsync(new FollowUp { ConversationId = conv.Id, Note = "check the price", DueAt = due, Repeat = "daily" });

        await sp.GetRequiredService<FollowUpRunner>().TickAsync();

        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Single(saved!.Messages); // nothing added
        Assert.False(saved.Unread);
        var f = Assert.Single(await store.ListAsync());
        Assert.Equal(due.AddDays(1), f.DueAt);
        Assert.Equal((1, "Nothing new."), (f.Runs, f.LastResult));
    }

    [Fact]
    public async Task HandedOffTaskReportsBackInTheChat()
    {
        var sp = Services();
        var conv = await ChatAsync(sp, "find me director roles");
        var result = await new StartTaskTool(sp).ExecuteAsync(new JsonObject { ["goal"] = "Find director-level software roles near Detroit" }, Ctx(sp, conv), default);
        Assert.False(result.IsError, result.Text);
        var task = Assert.Single(await sp.GetRequiredService<TaskStore>().ListAsync());
        Assert.Equal(conv.Id, task.ReportTo);

        // A finished task posts its result into the chat that started it (step-by-step progress doesn't).
        var runner = sp.GetRequiredService<TaskRunner>();
        var announce = typeof(TaskRunner).GetMethod("Announce", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        announce.Invoke(runner, [task, "📍 step 1 done"]);
        announce.Invoke(runner, [task, "🏁 Done: roles\n\n3 good matches."]);
        for (var i = 0; i < 100; i++)
        {
            var c = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
            if (c!.Messages.Any(m => m.Content.Contains("3 good matches."))) break;
            await Task.Delay(20);
        }
        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Contains(saved!.Messages, m => m.Content.Contains("3 good matches."));
        Assert.DoesNotContain(saved.Messages, m => m.Content.Contains("step 1 done"));

        // No tasks inside tasks.
        var inTask = new Conversation();
        inTask.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "work", RoutineName = "Task" });
        Assert.True((await new StartTaskTool(sp).ExecuteAsync(new JsonObject { ["goal"] = "more" }, Ctx(sp, inTask), default)).IsError);
    }

    [Fact]
    public async Task InitiativeIsASettingAndAlwaysAllowedForBots()
    {
        var sp = Services(initiative: true);
        var settings = await sp.GetRequiredService<SettingsStore>().GetAsync();
        var tools = await sp.GetRequiredService<ToolRegistry>().GetToolsAsync(settings, default);
        Assert.Contains(tools, t => t.Name == "follow_up");
        Assert.Contains(tools, t => t.Name == "start_task");
        var guide = ChatService.InitiativeGuide(tools.ToDictionary(t => t.Name), unattended: false);
        Assert.Contains("follow_up", guide);
        Assert.Contains("start_task", guide);
        Assert.DoesNotContain("start_task", ChatService.InitiativeGuide(tools.ToDictionary(t => t.Name), unattended: true));

        // The prompt the model sees includes it.
        LlmRequest? seen = null;
        _team.Script = (_, _, r) => { seen = r; return new LlmChunk("ok"); };
        var conv = await ChatAsync(sp, "hi");
        await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask, default);
        Assert.Contains("Take initiative", seen!.Messages[0].Content);

        // Bots get these tools whatever their other abilities.
        var bot = new Bot { Name = "B", Abilities = new BotAbilities { Mcp = false } };
        Assert.True(bot.Abilities.Allows("follow_up", bot.Memory));
        Assert.True(bot.Abilities.Allows("start_task", bot.Memory));

        settings.TakeInitiative = false;
        Assert.DoesNotContain(await sp.GetRequiredService<ToolRegistry>().GetToolsAsync(settings, default), t => t.Name == "follow_up");
    }

    [Fact]
    public async Task HiringABotAlwaysAsksAndGivesOnlyTheAbilitiesNeeded()
    {
        var sp = Services();
        var settings = await sp.GetRequiredService<SettingsStore>().GetAsync();
        var tool = (await sp.GetRequiredService<ToolRegistry>().GetToolsAsync(settings, default)).Single(t => t.Name == "propose_new_bot");
        Assert.True(tool.RequiresApproval);
        Assert.Equal(("hiring", true), PermissionCatalog.Classify(tool));
        Assert.Contains(PermissionCatalog.Areas, a => a.Key == "hiring");

        var conv = await ChatAsync(sp, "jobs again");
        var hired = await tool.ExecuteAsync(new JsonObject
        {
            ["name"] = "Scout", ["title"] = "Job hunter", ["emoji"] = "🧭",
            ["instructions"] = "You find director-level software roles near Detroit.",
            ["why"] = "Job searches come up every week",
            ["abilities"] = new JsonArray("web", "sheets"),
        }, Ctx(sp, conv), default);
        Assert.False(hired.IsError, hired.Text);
        var scout = (await sp.GetRequiredService<BotStore>().ListAsync()).Single(b => b.Name == "Scout");
        Assert.Equal(("Job hunter", "🧭"), (scout.Title, scout.Emoji));
        Assert.True(scout.Abilities.WebSearch && scout.Abilities.GoogleSheets);
        Assert.False(scout.Abilities.Code || scout.Abilities.Gmail || scout.Abilities.Browser || scout.Abilities.Mcp || scout.Abilities.ComputerUse);

        var again = await tool.ExecuteAsync(new JsonObject { ["name"] = "scout", ["title"] = "x", ["instructions"] = "y", ["why"] = "z" }, Ctx(sp, conv), default);
        Assert.True(again.IsError); // no duplicate names
        Assert.Contains("propose_new_bot", ChatService.InitiativeGuide(new Dictionary<string, ITool> { ["propose_new_bot"] = tool }, false));
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
