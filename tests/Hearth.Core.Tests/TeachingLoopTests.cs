using System.Text.RegularExpressions;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>What a reply drew on, 👎 corrections, contradiction checks, "you keep asking this" offers and the weekly recap.</summary>
public sealed class TeachingLoopTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-teach-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;
    private readonly AppSettings _settings;

    public TeachingLoopTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        _settings = store.GetAsync().GetAwaiter().GetResult();
        _settings.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9" }];
        _settings.ActiveProviderId = _settings.Providers[0].Id;
        _settings.AutoTitle = false;
        _settings.MemoryEnabled = true;
        _settings.MemoryAutoLearn = false;
        _settings.SkillLearning.Enabled = false;
        store.SaveAsync(_settings).GetAwaiter().GetResult();
        _team.ModelsByServer["main"] = ["main-model"];
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writers */ }
    }

    private static string System(IReadOnlyList<LlmMessage> m) => m[0].Content;

    private async Task<ChatMessage> AskAsync(Conversation conv, string text)
    {
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text });
        var chat = _sp.GetRequiredService<ChatService>();
        await chat.GenerateAsync(conv, new SendOptions("main-model", false, false, true), () => Task.CompletedTask, CancellationToken.None);
        var reply = conv.Messages.Last(m => m.Role == ChatRole.Assistant);
        await chat.AfterReplyAsync(reply.Id).WaitAsync(TimeSpan.FromSeconds(10));
        return reply;
    }

    [Theory]
    [InlineData("User's dog is named Biscuit", "Take Biscuit out before 7.", true)]
    [InlineData("Working on the kitchen remodel project", "For the kitchen remodel, get three quotes.", true)]
    [InlineData("User likes short, blunt answers", "Here's a long answer about taxes.", false)]
    [InlineData("Gym on Monday", "See you Monday!", false)]
    public void AFactShowsInAReplyByNameOrMostOfItsWords(string fact, string reply, bool shows) =>
        Assert.Equal(shows, LearningLoop.ShowsIn(fact, reply));

    [Fact]
    public async Task UnderAReplyItShowsTheSkillsAndMemoriesItDrewOn()
    {
        var memories = _sp.GetRequiredService<MemoryStore>();
        await memories.AddAsync("User's dog is named Biscuit");
        await memories.AddAsync("User works in aerospace");
        await _sp.GetRequiredService<SkillStore>().SaveAsync(new Skill
            { Name = "walk-plan", Description = "Plan dog walks", Instructions = "1. Check the weather.\n2. Pick a time before work.\n3. Say it in one line." });
        _team.Script = (_, messages, _) => messages.Any(m => m.Role == "tool")
            ? new LlmChunk("Walk Biscuit at 7am, before the rain.")
            : ScriptedTeam.Call("use_skill", new { name = "walk-plan" });

        var reply = await AskAsync(new Conversation(), "when should I walk the dog tomorrow?");

        Assert.Contains(reply.Used, u => u is { Kind: LearnedItem.UsedSkill, Text: "/walk-plan" });
        Assert.Contains(reply.Used, u => u is { Kind: LearnedItem.Memory, Text: "User's dog is named Biscuit" });
        Assert.DoesNotContain(reply.Used, u => u.Text.Contains("aerospace"));
    }

    [Fact]
    public async Task ThumbsDownFixesTheMemoryOrSkillBehindAWrongReplyAndEachFixCanBeUndone()
    {
        var memories = _sp.GetRequiredService<MemoryStore>();
        var skills = _sp.GetRequiredService<SkillStore>();
        var sam = await memories.AddAsync("Weekly report goes to Sam");
        var skill = new Skill { Name = "weekly-report", Description = "Send the weekly report", Instructions = "1. Gather the numbers.\n2. Write the summary.\n3. Email it to Sam." };
        await skills.SaveAsync(skill);
        _team.Script = (_, messages, _) =>
        {
            var system = System(messages);
            if (system.StartsWith("An AI assistant gave a reply the user says is wrong"))
            {
                Assert.Contains(sam.Id, messages[1].Content); // the memory it drew on is offered
                Assert.Contains("Email it to Sam", messages[1].Content);
                return new LlmChunk($"FIX {sam.Id} | Weekly report goes to Sarah\nSKILL weekly-report | email it to Sarah, not Sam\nREMEMBER | Always cc Dana on reports");
            }
            if (system.StartsWith("You edit a skill")) return new LlmChunk("1. Gather the numbers.\n2. Write the summary.\n3. Email it to Sarah.");
            return new LlmChunk("NONE");
        };
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "send the weekly report" });
        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant, Model = "main-model", Content = "Done: I emailed the weekly report to Sam.",
            Used = [new LearnedItem { Kind = LearnedItem.Memory, Text = sam.Text, Ref = sam.Id }, new LearnedItem { Kind = LearnedItem.UsedSkill, Text = "/weekly-report", Ref = skill.Id }],
        };
        conv.Messages.Add(reply);

        var result = await _sp.GetRequiredService<LearningLoop>().CorrectAsync(conv, reply, "It goes to Sarah now", () => Task.CompletedTask);

        Assert.StartsWith("Fixed:", result);
        Assert.Equal("It goes to Sarah now", reply.Feedback);
        Assert.Equal([LearnedItem.FixedMemory, LearnedItem.ImprovedSkill, LearnedItem.Memory], reply.LearnedItems.Select(i => i.Kind));
        Assert.Contains(await memories.ListAsync(), m => m.Text == "Weekly report goes to Sarah");
        Assert.Contains(await memories.ListAsync(), m => m.Text == "Always cc Dana on reports");
        Assert.Contains("Sarah", (await skills.FindAsync("weekly-report"))!.Instructions);

        var chat = _sp.GetRequiredService<ChatService>();
        foreach (var item in reply.LearnedItems.ToArray()) await chat.UndoLearnedAsync(conv, item);
        Assert.Contains(await memories.ListAsync(), m => m.Text == "Weekly report goes to Sam");
        Assert.DoesNotContain(await memories.ListAsync(), m => m.Text.Contains("Dana"));
        Assert.Contains("Email it to Sam", (await skills.FindAsync("weekly-report"))!.Instructions);
    }

    [Fact]
    public async Task ANewMemoryThatContradictsAnOldOneAsksWhichIsRight()
    {
        _settings.MemoryAutoLearn = true;
        await _sp.GetRequiredService<SettingsStore>().SaveAsync(_settings);
        var memories = _sp.GetRequiredService<MemoryStore>();
        var old = await memories.AddAsync("User likes short, blunt answers");
        _team.Script = (_, messages, _) =>
        {
            var system = System(messages);
            if (system.Contains("You extract memories")) return new LlmChunk("user: User prefers long, detailed answers");
            if (system.StartsWith("You keep an AI assistant's memory consistent"))
                return new LlmChunk(Regex.Match(messages[1].Content, @"- (\w+): User likes short") is { Success: true } m ? $"N1: {m.Groups[1].Value}" : "NONE");
            return new LlmChunk("Got it, I'll go into more detail from now on.");
        };

        var reply = await AskAsync(new Conversation(), "actually I'd like longer, more detailed answers from now on");

        var item = Assert.Single(reply.LearnedItems);
        Assert.True(item.HasOpenConflict);
        Assert.Equal(old.Id, item.ConflictRef);
        Assert.Equal("User likes short, blunt answers", item.ConflictText);

        var conv = new Conversation(); // resolving works on the item as stored in its chat
        Assert.Equal("Forgot \"User likes short, blunt answers\".", await _sp.GetRequiredService<LearningLoop>().ResolveConflictAsync(conv, item, "new"));
        Assert.False(item.HasOpenConflict);
        Assert.DoesNotContain(await memories.ListAsync(), m => m.Id == old.Id);
        Assert.Contains(await memories.ListAsync(), m => m.Text == "User prefers long, detailed answers");
    }

    [Fact]
    public async Task AskingForTheSameJobAgainOffersToMakeItASkillUntilTurnedDown()
    {
        var store = _sp.GetRequiredService<ConversationStore>();
        foreach (var day in new[] { 14, 7 })
        {
            var earlier = new Conversation { Title = "Status" };
            earlier.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "write my weekly status update for the team from these notes", CreatedAt = DateTimeOffset.Now.AddDays(-day) });
            earlier.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Here's your update.", CreatedAt = DateTimeOffset.Now.AddDays(-day) });
            await store.SaveAsync(earlier);
        }
        var nudgeAsked = 0;
        _team.Script = (_, messages, _) =>
        {
            if (System(messages).StartsWith("You notice when a user keeps asking"))
            {
                nudgeAsked++;
                Assert.Contains("weekly status update", messages[1].Content);
                return new LlmChunk("SKILL | 3 | weekly-status-update");
            }
            return new LlmChunk("Here's your weekly status update: shipped the parser, fixed two bugs.");
        };

        var reply = await AskAsync(new Conversation(), "write my weekly status update for the team from my notes");

        Assert.Equal(1, nudgeAsked);
        Assert.Equal(Nudge.Skill, reply.Nudge?.Kind);
        Assert.Equal(3, reply.Nudge!.Times);

        var conv = new Conversation();
        conv.Messages.Add(reply);
        await _sp.GetRequiredService<LearningLoop>().DismissNudgeAsync(conv, reply);
        Assert.Contains("skill:weekly-status-update", (await _sp.GetRequiredService<SettingsStore>().GetAsync()).Teaching.DismissedNudges);

        var again = await AskAsync(new Conversation(), "write my weekly status update for the team please, notes below");
        Assert.Null(again.Nudge); // turned down: not offered again
    }

    [Fact]
    public async Task TheWeeklyRecapListsWhatWasLearnedAcrossChatsAndFindsEachAgain()
    {
        var store = _sp.GetRequiredService<ConversationStore>();
        var conv = new Conversation { Title = "Dogs" };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "my dog is Biscuit" });
        conv.Messages.Add(new ChatMessage
        {
            Role = ChatRole.Assistant, Content = "Cute!",
            LearnedItems = [new LearnedItem { Kind = LearnedItem.Memory, Text = "Dog is named Biscuit" }, new LearnedItem { Kind = LearnedItem.Vibe, Text = "Dry humor", Undone = true }],
        });
        conv.Messages.Add(new ChatMessage
            { Role = ChatRole.Assistant, Content = "Old", CreatedAt = DateTimeOffset.Now.AddDays(-20), LearnedItems = [new LearnedItem { Kind = LearnedItem.Memory, Text = "Old fact" }] });
        await store.SaveAsync(conv);
        var recap = _sp.GetRequiredService<LearningRecap>();

        var week = await recap.SinceAsync(DateTimeOffset.Now.AddDays(-7));
        Assert.Equal(["Dog is named Biscuit"], week.Select(e => e.Item.Text));
        Assert.Contains("1. 🧠 Dog is named Biscuit", LearningRecap.Text(week));
        Assert.Equal("Dog is named Biscuit", (await recap.FindAsync(week[0].ConversationId, week[0].MessageId, week[0].Index))!.Value.item.Text);

        string? sent = null;
        recap.Ready += (text, _) => sent = text;
        Assert.Equal(1, await recap.SendAsync());
        Assert.StartsWith("🧠 This week Hearth learned 1 thing:", sent);
        Assert.NotNull((await _sp.GetRequiredService<SettingsStore>().GetAsync()).Teaching.LastRecap);

        var t = new TeachingSettings { RecapDay = DayOfWeek.Sunday, RecapTime = "18:00" };
        var wednesday = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero), LearningRecap.DueThisWeek(t, wednesday));
    }
}
