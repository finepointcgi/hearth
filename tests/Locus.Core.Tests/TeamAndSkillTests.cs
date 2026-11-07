using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

/// <summary>
/// A fake model server that plays a scripted team. It tells bots apart by their system prompt ("You are X")
/// and records which server and model each bot was run on.
/// </summary>
internal sealed class ScriptedTeam : ILlmClientFactory
{
    public readonly ConcurrentQueue<(string server, string? bot, string model)> Calls = new();
    public Func<string, IReadOnlyList<LlmMessage>, LlmRequest, LlmChunk>? Script;
    /// <summary>Simulated thinking time per bot (async, like a real model call), and how many overlapped.</summary>
    public Func<string, TimeSpan>? DelayFor;
    public int Running, MaxRunning;
    public readonly Dictionary<string, string[]> ModelsByServer = new();

    public ILlmClient Create(ProviderProfile profile) => new Client(this, profile.Name);

    private sealed class Client(ScriptedTeam team, string server) : ILlmClient
    {
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>(team.ModelsByServer.GetValueOrDefault(server, ["model"]).Select(m => new ModelInfo(m)).ToList());
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);

        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var system = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            var bot = system.StartsWith("You are ") ? system[8..system.IndexOf(',')] : null;
            team.Calls.Enqueue((server, bot, request.Model));
            if (team.DelayFor?.Invoke(bot ?? "") is { } delay && delay > TimeSpan.Zero)
            {
                var now = Interlocked.Increment(ref team.Running);
                lock (team) team.MaxRunning = Math.Max(team.MaxRunning, now);
                await Task.Delay(delay, ct);
                Interlocked.Decrement(ref team.Running);
            }
            yield return team.Script?.Invoke(bot ?? "", request.Messages, request) ?? new LlmChunk("ok");
        }
    }

    public static LlmChunk Call(string name, object args) =>
        new(ToolCalls: [new LlmToolCall(Guid.NewGuid().ToString("N"), name, System.Text.Json.JsonSerializer.Serialize(args))]);
}

public sealed class TeamAndSkillTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-team-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;
    private readonly AppSettings _settings;

    public TeamAndSkillTests()
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
            new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9" },
            new ProviderProfile { Name = "cloud", Kind = ProviderKind.OpenAICompatible, BaseUrl = "http://127.0.0.1:9" },
        ];
        _settings.ActiveProviderId = _settings.Providers[0].Id;
        _settings.AutoTitle = false;
        _settings.MemoryEnabled = false;
        store.SaveAsync(_settings).GetAwaiter().GetResult();
        _team.ModelsByServer["main"] = ["main-model"];
        _team.ModelsByServer["cloud"] = ["cloud-model"];
    }

    private static int ToolResults(IReadOnlyList<LlmMessage> m, string tool) => m.Count(x => x.Role == "tool" && x.ToolName == tool);

    [Fact]
    public async Task CoordinatorHandsOffInsteadOfDoingTheWorkFirst()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot { Name = "Researcher", Title = "Research" };
        foreach (var b in new[] { chief, researcher }) await bots.SaveAsync(b);
        string? chiefSystem = null;
        _team.Script = (bot, messages, _) =>
        {
            if (bot == "Chief" && chiefSystem is null)
            {
                chiefSystem = messages[0].Content;
                // What models did: hand off AND start researching in the same breath.
                return new LlmChunk("Let me get Researcher on this. @Researcher please research Google's stock.",
                    ToolCalls: [new LlmToolCall("c1", "calculate", "{\"expression\": \"1+1\"}")]);
            }
            return new LlmChunk(bot == "Researcher" ? "FACTS: GOOGL up 4% this week." : "ok");
        };

        var conv = new Conversation { BotIds = [chief.Id] }; // Chief alone: Researcher gets pulled in
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief research Google and its stock" });
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        Assert.Contains("delegate first", chiefSystem);
        var replies = conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Notice is null).ToList();
        Assert.Equal(chief.Id, replies[0].BotId);
        Assert.Equal("Let me get Researcher on this. @Researcher please research Google's stock.", replies[0].Content);
        Assert.Empty(replies[0].ToolCalls); // it didn't go and do it too
        Assert.Contains(replies, r => r.BotId == researcher.Id && r.Content.StartsWith("FACTS"));
    }

    [Fact]
    public async Task CoordinatorPicksABotVersionOnAnotherModel()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot
        {
            Name = "Researcher", Title = "Research",
            Versions = [new BotVersion { Name = "strong", ProviderId = _settings.Providers[1].Id, Model = "cloud-model", Note = "hard research" }],
        };
        foreach (var b in new[] { chief, researcher }) await bots.SaveAsync(b);
        string? chiefSystem = null;
        JsonObject? schema = null;
        _team.Script = (bot, messages, req) =>
        {
            if (bot != "Chief") return new LlmChunk("FACTS from " + req.Model);
            chiefSystem ??= messages[0].Content;
            schema ??= req.Tools?.First(t => t.Name == "message_bot").Parameters;
            return ToolResults(messages, "message_bot") == 0
                ? ScriptedTeam.Call("message_bot", new { bot = "Researcher", message = "Dig into the merger.", version = "strong" })
                : new LlmChunk("DONE — " + messages.Last(m => m.Role == "tool").Content);
        };

        var conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief look into the merger" });
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        Assert.Contains("version \"strong\": cloud-model on cloud — hard research", chiefSystem);
        Assert.Contains("Researcher: usually default model on main", chiefSystem);
        Assert.NotNull(schema!["properties"]!["version"]);
        Assert.Contains(_team.Calls, c => c is { bot: "Researcher", server: "cloud", model: "cloud-model" });
        Assert.Contains("Researcher (strong) replied:\n\nFACTS from cloud-model", conv.Messages.Last().Content);
        // The saved bot is unchanged: the version only applied to that hand-off.
        Assert.Null((await bots.GetAsync(researcher.Id))!.ProviderId);
    }

    [Fact]
    public async Task PulledInBotsShowInTheChatNotTheSidebar()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var store = _sp.GetRequiredService<ConversationStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot { Name = "Researcher", Title = "Research" };
        foreach (var b in new[] { chief, researcher }) await bots.SaveAsync(b);
        var seenWhileWorking = new List<string>();
        Conversation? conv = null;
        _team.Script = (bot, messages, _) =>
        {
            if (bot == "Researcher")
            {
                // While Researcher works, the chat already shows its (streaming) reply on Chief's hand-off.
                var live = conv!.Messages.SelectMany(m => m.ToolCalls).FirstOrDefault(t => t.Name == "message_bot")?.Handoff;
                seenWhileWorking.Add(live is { IsStreaming: true } ? "live" : "not shown");
                return new LlmChunk("**FACTS:** GOOGL up 4%.");
            }
            var thisTurn = messages.Skip(messages.ToList().FindLastIndex(m => m.Role == "user")).ToList();
            return ToolResults(thisTurn, "message_bot") == 0
                ? ScriptedTeam.Call("message_bot", new { bot = "Researcher", message = "Research Google's stock." })
                : new LlmChunk("Here's what Researcher found.");
        };

        conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief look into Google" });
        await store.SaveAsync(conv);
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(["live"], seenWhileWorking);
        var handoff = conv.Messages.Last().ToolCalls.Single().Handoff!;
        Assert.Equal(researcher.Id, handoff.BotId);
        Assert.Equal("**FACTS:** GOOGL up 4%.", handoff.Content);
        Assert.False(handoff.IsStreaming);
        // Saved with the chat, so it's still there after a reload.
        Assert.NotNull((await store.GetAsync(conv.Id))!.Messages.Last().ToolCalls.Single().Handoff);

        // Not in the sidebar, and no unread 1:1 chat with Researcher.
        var listed = await store.ListAsync();
        Assert.Equal([conv.Id], listed.Select(c => c.Id).ToList());
        var behind = await store.GetAsync(conv.Id + "-handoff-" + researcher.Id);
        Assert.Equal(conv.Id, behind!.HandoffFrom);
        Assert.False(behind.Unread);

        // A second hand-off in the same chat continues the same behind-the-scenes chat (Researcher remembers).
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief and its earnings?" });
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(4, (await store.GetAsync(behind.Id))!.Messages.Count);

        await store.DeleteAsync(conv.Id);
        Assert.Null(await store.GetAsync(behind.Id));
    }

    [Fact]
    public async Task BotsCanGiveATeammateAWeeklyRoutine()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var chef = new Bot { Name = "Chef", Title = "Meal planner" };
        foreach (var b in new[] { chief, chef }) await bots.SaveAsync(b);
        var tool = new AddRoutineTool(bots);
        Assert.True(tool.RequiresApproval); // the user OKs it, like hiring a bot
        var ctx = new ToolContext
        {
            Conversation = new Conversation(), Reply = new ChatMessage(), Settings = _settings,
            Client = _team.Create(_settings.Providers[0]), Model = "main-model", Bot = chief,
        };

        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["bot"] = "@Chef", ["name"] = "Weekly meal plan", ["schedule"] = "weekly", ["day"] = "sun", ["time"] = "9:30",
            ["prompt"] = "Plan 7 dinners for 2 people, no chicken, then a shopping list by store section.",
        }, ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Sundays at 09:30", result.Text);
        var routine = Assert.Single((await bots.GetAsync(chef.Id))!.Routines);
        Assert.Equal((RoutineSchedule.Weekly, DayOfWeek.Sunday, "09:30"), (routine.Schedule, routine.Day, routine.TimeOfDay));
        Assert.Empty((await bots.GetAsync(chief.Id))!.Routines);

        // Incomplete or wrong input says what's needed.
        var noDay = await tool.ExecuteAsync(new JsonObject { ["bot"] = "Chef", ["name"] = "x", ["prompt"] = "y", ["schedule"] = "weekly", ["time"] = "09:00" }, ctx, CancellationToken.None);
        Assert.Contains("weekly also needs day", noDay.Text);
        var nobody = await tool.ExecuteAsync(new JsonObject { ["bot"] = "Baker", ["name"] = "x", ["prompt"] = "y", ["schedule"] = "daily", ["time"] = "09:00" }, ctx, CancellationToken.None);
        Assert.Contains("No bot named 'Baker'. Bots: Chief, Chef", nobody.Text);
    }

    [Fact]
    public void WeeklyRoutinesRunOnTheirDayOnly()
    {
        var r = new Routine { Prompt = "plan meals", Schedule = RoutineSchedule.Weekly, Day = DayOfWeek.Sunday, TimeOfDay = "09:00" };
        var sunday = new DateTimeOffset(2026, 11, 8, 9, 5, 0, TimeSpan.FromHours(-5));
        Assert.True(r.IsDue(sunday));
        Assert.False(r.IsDue(sunday.AddDays(1)));
        r.LastRun = sunday;
        Assert.False(r.IsDue(sunday.AddMinutes(30)));
        Assert.Equal(sunday.AddDays(7).AddMinutes(-5), r.NextRun(sunday));
        Assert.Equal("Sundays at 09:00", r.Describe());
    }

    [Fact]
    public async Task UnknownVersionTellsTheCoordinatorWhatExists()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot { Name = "Researcher", Title = "Research", Versions = [new BotVersion { Name = "strong" }] };
        foreach (var b in new[] { chief, researcher }) await bots.SaveAsync(b);
        _team.Script = (bot, messages, _) => bot != "Chief" ? new LlmChunk("x")
            : ToolResults(messages, "message_bot") == 0
                ? ScriptedTeam.Call("message_bot", new { bot = "Researcher", message = "hi", version = "turbo" })
                : new LlmChunk("seen: " + messages.Last(m => m.Role == "tool").Content);
        var conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief ask" });
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        Assert.Contains("no version 'turbo'. Versions: strong", conv.Messages.Last().Content);
        Assert.DoesNotContain(_team.Calls, c => c.bot == "Researcher");
    }

    [Fact]
    public async Task ClearSpecialistWorkSkipsALoneCoordinator()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot { Name = "Researcher", Title = "Research" };
        foreach (var b in new[] { chief, researcher }) await bots.SaveAsync(b);
        _team.Script = (bot, messages, _) =>
            messages[0].Content.Contains("Decide how to handle") ? new LlmChunk(messages[^1].Content.Contains("stock")
                ? "{\"mode\":\"one\",\"assign\":[{\"bot\":\"Researcher\",\"task\":\"Research Google's stock\"}],\"review\":false}"
                : "{\"mode\":\"self\",\"assign\":[]}")
            : new LlmChunk(bot + " answered");
        var chat = _sp.GetRequiredService<ChatService>();
        var opts = new SendOptions("main-model", false, false, true);

        var conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "research Google's stock" });
        await chat.GenerateAsync(conv, opts, () => Task.CompletedTask, CancellationToken.None);
        var replies = conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Notice is null && !m.Behind).ToList();
        // The Chief says "on it" at once (no model call), then the Researcher's answer goes straight to the user.
        Assert.Equal(["On it — I've asked Researcher to take this. Back shortly.", "Researcher answered"], replies.Select(r => r.Content));
        Assert.True(replies[0].Ack);
        Assert.Single(conv.Messages, m => m.Behind); // the hand-out, folded
        Assert.Contains(conv.Messages, m => m.Notice?.Contains("added") == true && m.Notice.Contains("Researcher"));

        // Chat and anything unclear stay with the coordinator.
        var other = new Conversation { BotIds = [chief.Id] };
        other.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "morning! what's on today?" });
        await chat.GenerateAsync(other, opts, () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("Chief answered", other.Messages.Last().Content);
        Assert.Equal([chief.Id], other.BotIds);
    }

    [Fact]
    public async Task TheTeamSplitsTheWorkReviewsItAndTheCoordinatorAnswers()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var researcher = new Bot { Name = "Researcher", Title = "Research" };
        var writer = new Bot { Name = "Writer", Title = "Writing" };
        var critic = new Bot { Name = "Critic", Title = "Reviewer & devil's advocate" };
        foreach (var b in new[] { chief, researcher, writer, critic }) await bots.SaveAsync(b);
        var reviews = 0;
        _team.Script = (bot, messages, _) =>
        {
            var system = messages[0].Content;
            if (system.Contains("Decide how to handle"))
                return new LlmChunk("Sure! ```json\n{\"mode\":\"team\",\"assign\":[{\"bot\":\"Researcher\",\"task\":\"Find Shield AI's latest funding\"}," +
                    "{\"bot\":\"@writer\",\"task\":\"Write a 3-line summary\"}],\"review\":true}\n```");
            Assert.Contains("RIGHT NOW:", system); // each step is told exactly what it's doing
            return bot switch
            {
                "Researcher" => new LlmChunk("Raised $240M in March 2025."),
                "Writer" => new LlmChunk(messages.Any(m => m.Content.Contains("please fix")) ? "Fixed summary: $240M, March 2025." : "Summary: raised $2.4B."),
                "Critic" => new LlmChunk(reviews++ == 0 ? "@Writer says $2.4B but Researcher found $240M." : "Looks good."),
                _ => new LlmChunk(messages.Any(m => m.Content.Contains("Fixed summary")) ? "Final: Shield AI raised $240M in March 2025." : "WRONG"),
            };
        };
        var conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "What's Shield AI's latest funding? Short summary please." });
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        var said = conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)
            .Select(m => $"{bots.GetAsync(m.BotId!).Result!.Name}: {m.Content.Split('\n')[0]}").ToList();
        Assert.Equal([
            "Chief: On it — I've got Researcher and Writer on this, and Critic will check the work. I'll come back with the answer.",
            "Chief: @Researcher: Find Shield AI's latest funding",
            "Researcher: Raised $240M in March 2025.",
            "Writer: Summary: raised $2.4B.",
            "Chief: @Critic, please check the work above against what the user asked.",
            "Critic: @Writer says $2.4B but Researcher found $240M.",
            "Chief: @Writer, please fix what Critic raised.",
            "Writer: Fixed summary: $240M, March 2025.",
            "Chief: Final: Shield AI raised $240M in March 2025.",
        ], said);
        // Only the quick reply and the answer are out front; everything between is the work behind it, in one team turn.
        var assistant = conv.Messages.Where(m => m.Role == ChatRole.Assistant && m.Notice is null).ToList();
        Assert.Equal([false, true, true, true, true, true, true, true, false], assistant.Select(m => m.Behind));
        Assert.Single(assistant.Select(m => m.TeamTurn).Distinct());
        // A later turn doesn't carry the folded work in its history.
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "thanks!" });
        string? seen = null;
        _team.Script = (bot, messages, _) => { if (!messages[0].Content.Contains("Decide how to handle")) seen = string.Join("\n", messages.Select(m => m.Content)); return new LlmChunk(messages[0].Content.Contains("Decide how to handle") ? "{\"mode\":\"self\"}" : "You're welcome"); };
        await _sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        Assert.Contains("Final: Shield AI raised $240M", seen);
        Assert.DoesNotContain("Summary: raised $2.4B", seen);
        Assert.Equal(4, conv.BotIds.Count); // everyone who worked on it is in the chat now

        Assert.True(ChatService.LooksGood("**Looks good.** Nothing to add."));
        // An answer check's rewrite that only says what it's about to do never replaces the answer.
        var answer = "Alphabet closed at $284.78 on Nov 5, down 30% from its high. " + new string('x', 400);
        Assert.False(ChatService.IsRealRewrite("Checking each claim against the tool output before rewriting.", answer));
        Assert.False(ChatService.IsRealRewrite("Alphabet closed at $284.78.", answer));
        Assert.True(ChatService.IsRealRewrite(answer.Replace("30%", "about 30%"), answer));
        Assert.False(ChatService.LooksGood("Looks good overall, but the date is wrong and the total doesn't add up, and also " + new string('x', 400)));
    }

    [Fact]
    public async Task NewChatsGoToTheDefaultBot()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        var writer = new Bot { Name = "Writer", Title = "Writing" };
        foreach (var b in new[] { chief, writer }) await bots.SaveAsync(b);
        _team.Script = (bot, messages, _) => new LlmChunk(messages[0].Content.Contains("Decide how to handle") ? "{\"mode\":\"self\"}" : (bot ?? "") + " here");
        var chat = _sp.GetRequiredService<ChatService>();
        var opts = new SendOptions("main-model", false, false, true);
        async Task<Conversation> AskAsync()
        {
            var c = new Conversation();
            c.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi" });
            await chat.GenerateAsync(c, opts, () => Task.CompletedTask, CancellationToken.None);
            return c;
        }

        Assert.Equal([chief.Id], (await AskAsync()).BotIds); // the coordinator, by default
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.DefaultBotId = writer.Id;
        await store.SaveAsync(s);
        Assert.Equal([writer.Id], (await AskAsync()).BotIds);
        s.DefaultBotId = "";
        await store.SaveAsync(s);
        Assert.Empty((await AskAsync()).BotIds); // plain Locus

        // A chat that already has plain replies stays plain.
        s.DefaultBotId = null;
        await store.SaveAsync(s);
        var old = new Conversation();
        old.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi" });
        old.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "hello" });
        old.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "again" });
        await chat.GenerateAsync(old, opts, () => Task.CompletedTask, CancellationToken.None);
        Assert.Empty(old.BotIds);
    }

    [Fact]
    public async Task CoordinatorOnItsOwnServerDelegatesToTwoBotsAndCombinesTheirWork()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true, ProviderId = _settings.Providers[1].Id };
        var researcher = new Bot { Name = "Researcher", Title = "Research" };
        var writer = new Bot { Name = "Writer", Title = "Writing" };
        foreach (var b in new[] { chief, researcher, writer }) await bots.SaveAsync(b);

        _team.Script = (bot, messages, _) => bot switch
        {
            "Chief" => ToolResults(messages, "message_bot") switch
            {
                0 => ScriptedTeam.Call("message_bot", new { bot = "Researcher", message = "Find three facts about the launch." }),
                1 => ScriptedTeam.Call("message_bot", new { bot = "Writer", message = "Write a launch note from: " + messages.Last(m => m.Role == "tool").Content }),
                _ => new LlmChunk("FINAL — " + messages.Last(m => m.Role == "tool").Content),
            },
            "Researcher" => new LlmChunk("FACTS: ships Friday; 3 regions; price unchanged"),
            "Writer" => new LlmChunk("NOTE: We launch Friday in 3 regions at the same price."),
            _ => new LlmChunk("ok"),
        };

        var conv = new Conversation { BotIds = [chief.Id, researcher.Id, writer.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Chief prepare the launch note" });
        var chat = _sp.GetRequiredService<ChatService>();
        await chat.GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        // Only the coordinator answered in the group; it combined both teammates' work.
        var replies = conv.Messages.Where(m => m.Role == ChatRole.Assistant).ToList();
        var final = Assert.Single(replies);
        Assert.Equal(chief.Id, final.BotId);
        Assert.Contains("We launch Friday in 3 regions", final.Content);
        Assert.Equal(["message_bot", "message_bot"], final.ToolCalls.Select(t => t.Name));
        Assert.All(final.ToolCalls, t => Assert.Equal(ToolCallStatus.Done, t.Status));

        // The writer got the researcher's facts passed along.
        var store = _sp.GetRequiredService<ConversationStore>();
        var writerChat = (await store.GetAsync(conv.Id + "-handoff-" + writer.Id))!; // behind the scenes, not a 1:1 chat
        Assert.Contains("ships Friday", writerChat.Messages.First(m => m.Role == ChatRole.User).Content);
        Assert.Equal(chief.Id, writerChat.Messages.First(m => m.Role == ChatRole.User).FromBotId);

        // Each bot ran on its own server with the right model.
        var calls = _team.Calls.ToList();
        Assert.All(calls.Where(c => c.bot == "Chief"), c => Assert.Equal(("cloud", "cloud-model"), (c.server, c.model)));
        Assert.All(calls.Where(c => c.bot is "Researcher" or "Writer"), c => Assert.Equal(("main", "main-model"), (c.server, c.model)));
        Assert.Contains(calls, c => c.bot == "Researcher");
        Assert.Contains(calls, c => c.bot == "Writer");
    }

    [Fact]
    public void BotServerFallsBackToTheMainServerThenBackups()
    {
        var backup = new ProviderProfile { Name = "backup" };
        var s = new AppSettings { Providers = [.. _settings.Providers, backup], ActiveProviderId = _settings.Providers[0].Id };
        s.FallbackProviderIds = [backup.Id];
        var bot = new Bot { Name = "B", ProviderId = _settings.Providers[1].Id };
        Assert.Equal("cloud", s.ProviderFor(bot).Name);
        Assert.Equal("main", s.ProviderFor(new Bot { ProviderId = "deleted-server" }).Name);
        var client = ((ILlmClientFactory)_team).CreateForBot(s, bot);
        var chain = Assert.IsType<FailoverLlmClient>(client);
        Assert.Equal(["cloud", "main", "backup"], chain.Servers);
    }

    [Fact]
    public void ParsesSkillProposals()
    {
        var create = SkillProposal.Parse("""
            ACTION: CREATE
            NAME: weekly-sales-report
            DESCRIPTION: Pull last week's sales and summarize them
            INSTRUCTIONS:
            1. Open the sales sheet with sheets_find "Sales 2026".
            2. Read last week's rows and total them by region.
            3. Reply with a table and a one-line trend.
            """);
        Assert.NotNull(create);
        Assert.Equal("weekly-sales-report", create!.Name);
        Assert.Null(create.Improve);
        Assert.StartsWith("1. Open the sales sheet", create.Instructions);

        var improve = SkillProposal.Parse("**ACTION:** IMPROVE weekly-sales-report\nNAME: weekly-sales-report\nDESCRIPTION: x\nINSTRUCTIONS:\n1. The sheet moved: search for \"Sales FY26\" instead.\n2. Total by region.");
        Assert.Equal("weekly-sales-report", improve!.Improve);
        Assert.Null(SkillProposal.Parse("ACTION: NONE"));
        Assert.Null(SkillProposal.Parse("ACTION: CREATE\nNAME: x\nINSTRUCTIONS:\ntoo short"));
    }

    [Fact]
    public async Task BotSavesAMethodAfterAMultiStepJobAndTheNextBotUsesIt()
    {
        _settings.SkillLearning.MinToolCalls = 3;
        await _sp.GetRequiredService<SettingsStore>().SaveAsync(_settings);
        var skills = _sp.GetRequiredService<SkillStore>();
        var bots = _sp.GetRequiredService<BotStore>();
        var analyst = new Bot { Name = "Analyst", Title = "Numbers" };
        await bots.SaveAsync(analyst);

        const string method = "1. Convert each price to USD with convert_units-style math.\n2. Add them with calculate.\n3. Report the total and the biggest item.";
        _team.Script = (bot, messages, request) =>
        {
            var reflecting = messages[0].Content.StartsWith("You maintain a library of reusable skills");
            if (reflecting) return new LlmChunk($"ACTION: CREATE\nNAME: total-prices\nDESCRIPTION: Add up a list of prices\nINSTRUCTIONS:\n{method}");
            return ToolResults(messages, "calculate") switch
            {
                < 3 => ScriptedTeam.Call("calculate", new { expression = "2+2" }),
                _ => new LlmChunk("The total is 12."),
            };
        };

        var conv = new Conversation { BotIds = [analyst.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Add up these prices: 4, 4, 4" });
        var chat = _sp.GetRequiredService<ChatService>();
        await chat.GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);

        Skill? saved = null;
        for (var i = 0; i < 50 && saved is null; i++) { await Task.Delay(100); saved = await skills.FindAsync("total-prices"); }
        Assert.NotNull(saved);
        Assert.Equal(analyst.Id, saved!.CreatedBy);
        Assert.False(saved.Pending);
        Assert.Equal(method, saved.Instructions);
        Assert.Contains("saved /total-prices", conv.Messages.Last().SkillNotes);

        // The next request sees the skill in its prompt and loads it.
        string? prompt = null;
        _team.Script = (bot, messages, _) =>
        {
            prompt ??= messages[0].Content;
            return ToolResults(messages, "use_skill") == 0 ? ScriptedTeam.Call("use_skill", new { name = "total-prices" }) : new LlmChunk("Done.");
        };
        var next = new Conversation { BotIds = [analyst.Id] };
        next.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Total: 1, 2" });
        await chat.GenerateAsync(next, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        Assert.Contains("- total-prices: Add up a list of prices", prompt);
        Assert.Contains("save_skill", prompt);
        Assert.Equal(1, (await skills.FindAsync("total-prices"))!.Uses);
    }

    [Fact]
    public async Task ReviewedSkillsStayHiddenUntilApprovedAndChangesCanBeUndone()
    {
        _settings.SkillLearning.NeedsReview = true;
        await _sp.GetRequiredService<SettingsStore>().SaveAsync(_settings);
        var skills = _sp.GetRequiredService<SkillStore>();
        var ctx = new ToolContext
        {
            Conversation = new Conversation(), Reply = new ChatMessage(), Settings = _settings, Client = null!, Model = "m",
            Bot = new Bot { Name = "Helper" },
        };
        var save = new SaveSkillTool(skills, _sp.GetRequiredService<SettingsStore>());
        var r = await save.ExecuteAsync(new JsonObject
        {
            ["name"] = "Check Flights", ["description"] = "Find the cheapest flight",
            ["instructions"] = "1. Open the airline site.\n2. Search the dates.\n3. Report the 3 cheapest options.",
        }, ctx, CancellationToken.None);
        Assert.False(r.IsError);
        var skill = (await skills.FindAsync("check-flights"))!;
        Assert.True(skill.Pending);
        Assert.Empty(await skills.ActiveAsync());

        skill.Pending = false;
        await skills.SaveAsync(skill);
        var improve = new ImproveSkillTool(skills, _sp.GetRequiredService<SettingsStore>());
        await improve.ExecuteAsync(new JsonObject
        {
            ["name"] = "check-flights", ["change"] = "Use the comparison site",
            ["instructions"] = "1. Open the comparison site instead — the airline site blocks bots.\n2. Search the dates.\n3. Report the 3 cheapest.",
        }, ctx, CancellationToken.None);
        skill = (await skills.FindAsync("check-flights"))!;
        Assert.True(skill.Pending);
        Assert.Contains("comparison site", skill.Instructions);
        Assert.Equal("Helper", skill.Revisions[0].By);

        await skills.UndoAsync(skill);
        Assert.StartsWith("1. Open the airline site.", (await skills.FindAsync("check-flights"))!.Instructions);
        Assert.Contains("saved /check-flights", ctx.Reply.SkillNotes);
    }

    [Fact]
    public async Task TheLeadPicksUpHowToBeWithYouAndWhatsGoingOn()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        await bots.SaveAsync(chief);
        var companion = _sp.GetRequiredService<Companion>();
        var store = _sp.GetRequiredService<SettingsStore>();
        _settings.MemoryEnabled = true;
        await store.SaveAsync(_settings);
        string? system = null;
        _team.Script = (bot, messages, _) =>
        {
            var first = messages[0].Content;
            if (first.Contains("You extract memories"))
                return new LlmChunk("vibe: dry humor; likes short, blunt answers\nthread: Job search | Final round at Shield AI on Friday\nuser: lives in Detroit");
            if (first.Contains("Decide how to handle")) return new LlmChunk("{\"mode\":\"self\"}");
            if (bot == "Chief") system = first;
            return new LlmChunk("Good luck Friday!");
        };
        var chat = _sp.GetRequiredService<ChatService>();
        var opts = new SendOptions("main-model", false, false, true);
        var conv = new Conversation { BotIds = [chief.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "ugh, final round at Shield AI is Friday. nervous." });
        await chat.GenerateAsync(conv, opts, () => Task.CompletedTask, CancellationToken.None);
        for (var i = 0; i < 100 && (await companion.ActiveAsync()).Count == 0; i++) await Task.Delay(20); // learning runs after the reply

        var thread = Assert.Single(await companion.ActiveAsync());
        Assert.Equal("Job search", thread.Title);
        Assert.Equal("dry humor; likes short, blunt answers", Assert.Single(await companion.NotesForAsync(chief.Id)).Text);

        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "morning" });
        await chat.GenerateAsync(conv, opts, () => Task.CompletedTask, CancellationToken.None);
        Assert.Contains("- dry humor; likes short, blunt answers", system);
        Assert.Contains("- Job search: Final round at Shield AI on Friday (today)", system);
        Assert.Contains(await _sp.GetRequiredService<ToolRegistry>().GetToolsAsync(_settings, default), t => t.Name == "life_threads");
    }

    [Fact]
    public async Task ThreadsGrowCloseAndDropOutWhenDone()
    {
        var companion = _sp.GetRequiredService<Companion>();
        await companion.UpdateAsync("Job search", "Applied to Shield AI");
        await companion.UpdateAsync("job search.", "Recruiter call Tuesday"); // the same thread, however it's written
        await companion.UpdateAsync("Kitchen remodel", "Contractor quotes due");
        var all = await companion.ThreadsListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(["Applied to Shield AI", "Recruiter call Tuesday"], all.First(t => t.Title == "Job search").Updates.Select(u => u.Text));
        Assert.NotNull(await companion.CloseAsync("kitchen", "Done, looks great"));
        Assert.Equal(["Job search"], (await companion.ActiveAsync()).Select(t => t.Title));
        Assert.DoesNotContain("Kitchen", await companion.PromptAsync(null));
        // Notes: no repeats, and only the latest are kept.
        Assert.True(await companion.AddNoteAsync(null, "Likes dad jokes"));
        Assert.False(await companion.AddNoteAsync(null, "likes dad jokes."));
        for (var i = 0; i < Companion.MaxNotesPerBot + 5; i++) await companion.AddNoteAsync(null, $"note number {i:000}");
        Assert.Equal(Companion.MaxNotesPerBot, (await companion.NotesForAsync(null)).Count);
    }

    [Fact]
    public async Task CheckInsAreKeptAsRoutinesOnTheLeadBot()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", Title = "Coordinator", IsCoordinator = true };
        await bots.SaveAsync(chief);
        var checkIns = _sp.GetRequiredService<CheckIns>();
        var c = new CheckInSettings { Morning = true, MorningTime = "07:45", Evening = true };
        await checkIns.ApplyAsync(c);
        var routines = (await bots.GetAsync(chief.Id))!.Routines;
        Assert.Equal([CheckIns.MorningName, CheckIns.EveningName], routines.Select(r => r.Name));
        Assert.All(routines, r => Assert.True(r.Conversational));
        Assert.Equal("07:45", routines[0].TimeOfDay);
        c.Morning = false;
        await checkIns.ApplyAsync(c);
        Assert.Equal([CheckIns.EveningName], (await bots.GetAsync(chief.Id))!.Routines.Select(r => r.Name));
        Assert.Null((await _sp.GetRequiredService<SettingsStore>().GetAsync()).CheckIns.MorningRoutineId);
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writers */ }
    }
}
