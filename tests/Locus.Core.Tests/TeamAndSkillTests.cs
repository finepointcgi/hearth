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
        var writerChat = await store.GetOrCreateDirectAsync(writer);
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

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writers */ }
    }
}
