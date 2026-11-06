using Locus.Core;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

public sealed class LongTermMemoryTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-ltm-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;

    public LongTermMemoryTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryEnabled = true;
        s.MemoryAutoLearn = false;
        s.SkillLearning.Enabled = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
        _team.ModelsByServer["main"] = ["main-model"];
    }

    private MemoryStore Memories => _sp.GetRequiredService<MemoryStore>();

    [Fact]
    public async Task LongTermMemoriesComeAlongWhenRelevantWithoutChangingTheSystemPrompt()
    {
        var chicken = await Memories.AddAsync("Doesn't eat chicken; cooks dinner for 2 people");
        await Memories.AddAsync("Prefers answers short and direct");
        await Memories.SetArchivedAsync([chicken.Id], true);

        var requests = new List<LlmRequest>();
        _team.Script = (_, _, req) => { requests.Add(req); return new LlmChunk("ok"); };
        var chat = _sp.GetRequiredService<ChatService>();
        async Task Ask(string text)
        {
            var conv = new Conversation();
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text });
            await chat.GenerateAsync(conv, new SendOptions("main-model", false, false, false), () => Task.CompletedTask, CancellationToken.None);
        }
        await Ask("plan my dinners this week: what about chicken?");
        await Ask("what's 2+2");

        var system = requests[0].Messages[0].Content;
        Assert.Contains("Prefers answers short and direct", system);      // recent: listed every time
        Assert.DoesNotContain("Doesn't eat chicken", system);              // long-term: not in the system prompt
        Assert.Contains("1 more memories are in long-term storage", system);
        var ask = requests[0].Messages.Last(m => m.Role == "user").Content;
        Assert.Contains("[From long-term memory, possibly relevant:\n- Doesn't eat chicken", ask);
        Assert.Equal(system, requests[1].Messages[0].Content);             // same start for every message
        Assert.DoesNotContain("long-term memory, possibly", requests[1].Messages.Last(m => m.Role == "user").Content);
    }

    [Fact]
    public async Task NewMemoriesFoldIntoTheProfileAndMoveToLongTermStorage()
    {
        var a = await Memories.AddAsync("Director of Advanced Software at Detroit Defense");
        var b = await Memories.AddAsync("No chicken in meal plans");
        var c = await Memories.AddAsync("Asked about the weather in Detroit once");
        var curator = _sp.GetRequiredService<MemoryCurator>();
        await curator.SaveProfileAsync("Mitchell is job hunting with a $230k floor.");
        string? sent = null;
        _team.Script = (_, messages, _) =>
        {
            sent = messages[^1].Content;
            return new LlmChunk($"PROFILE:\nMitchell is Director of Advanced Software at Detroit Defense, job hunting with a $230k floor.\nFood: no chicken.\nFOLDED: {a.Id}, {b.Id}, nonsense");
        };

        var (folded, updated) = await curator.ConsolidateAsync();

        Assert.Contains(c.Id, sent);
        Assert.Equal((2, true), (folded, updated));
        var profile = await curator.ProfileAsync();
        Assert.Contains("Food: no chicken.", profile.Text);
        Assert.Equal("Mitchell is job hunting with a $230k floor.", profile.PreviousText);
        Assert.NotNull(profile.ConsolidatedAt);
        var all = await Memories.ListAsync();
        Assert.Equal(3, all.Count); // nothing deleted
        Assert.Equal([a.Id, b.Id], all.Where(m => m.Archived).Select(m => m.Id).ToList());

        await curator.UndoProfileAsync();
        Assert.Equal("Mitchell is job hunting with a $230k floor.", (await curator.ProfileAsync()).Text);
    }

    [Fact]
    public void FoldsOvernightOrWhenTheListGetsLong()
    {
        var night = new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.FromHours(-5));
        var two = Enumerable.Range(0, 2).Select(i => new Memory($"m{i}", "x", night)).ToList();
        var yesterday = new UserProfile { ConsolidatedAt = night.AddDays(-1) };
        Assert.True(MemoryCurator.ConsolidationDue(yesterday, two, night));
        Assert.False(MemoryCurator.ConsolidationDue(yesterday, two, night.AddHours(9)));        // noon: wait for tonight
        Assert.False(MemoryCurator.ConsolidationDue(yesterday, [new Memory("a", "x", night, Archived: true)], night)); // nothing new
        var many = Enumerable.Range(0, MemoryCurator.FoldWhenRecentReach).Select(i => new Memory($"m{i}", "x", night)).ToList();
        Assert.True(MemoryCurator.ConsolidationDue(yesterday, many, night.AddHours(9)));         // too many to wait
    }

    [Fact]
    public void ReadsTheFoldedIds()
    {
        var (profile, folded) = MemoryCurator.ParseConsolidation("PROFILE:\nLikes tea and long walks.\nFOLDED: `ab12`, cd34,zz99", new HashSet<string> { "ab12", "cd34" });
        Assert.Equal("Likes tea and long walks.", profile);
        Assert.Equal(["ab12", "cd34"], folded);
        Assert.Null(MemoryCurator.ParseConsolidation("I can't do that.", new HashSet<string>()).profile);
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
