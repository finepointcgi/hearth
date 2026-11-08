using System.IO.Compression;
using System.Text;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Services.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Buttons that survive restarts, "what I learned" in the morning brief, skill health, and importing from ChatGPT or Claude.</summary>
public sealed class TeachingExtrasTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-extras-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;

    public TeachingExtrasTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var settings = store.GetAsync().GetAwaiter().GetResult();
        settings.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9" }];
        settings.ActiveProviderId = settings.Providers[0].Id;
        settings.MemoryEnabled = true;
        store.SaveAsync(settings).GetAwaiter().GetResult();
        _team.ModelsByServer["main"] = ["main-model"];
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writers */ }
    }

    [Fact]
    public async Task ButtonKeysAndTheRecapSurviveARestart()
    {
        var file = Path.Combine(_paths.DataDirectory, "button-keys.json");
        var before = new ButtonKeys(file);
        var key = await before.NewAsync("conv1", "msg1");
        await before.SetRecapAsync([new ButtonKeys.RecapRef("conv2", "msg2", 3)]);

        var after = new ButtonKeys(file); // as after a restart
        var target = await after.FindAsync(key);
        Assert.Equal("conv1", target!.Conv);
        Assert.Equal("msg1", target.Msg);
        Assert.Equal(new ButtonKeys.RecapRef("conv2", "msg2", 3), await after.RecapAsync(0));
        Assert.Null(await after.RecapAsync(1));
        Assert.Null(await after.FindAsync("nope"));
    }

    [Fact]
    public async Task TheMorningBriefListsWhatWasLearnedSinceTheLastOne()
    {
        Assert.Contains(LearningRecap.LearnedToken, Briefing.Prompt(new BriefingSettings()));
        Assert.DoesNotContain(LearningRecap.LearnedToken, Briefing.Prompt(new BriefingSettings { Learned = false }));

        var recap = _sp.GetRequiredService<LearningRecap>();
        var prompt = "Brief:\n- " + LearningRecap.LearnedToken;
        Assert.Contains("nothing new was learned", await recap.ExpandAsync(prompt, DateTimeOffset.Now.AddDays(-1)));

        var conv = new Conversation { Title = "Dogs" };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Cute!", LearnedItems = [new LearnedItem { Kind = LearnedItem.Memory, Text = "Dog is named Biscuit" }] });
        conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Old", CreatedAt = DateTimeOffset.Now.AddDays(-3),
            LearnedItems = [new LearnedItem { Kind = LearnedItem.Memory, Text = "Older fact" }] });
        await _sp.GetRequiredService<ConversationStore>().SaveAsync(conv);

        var expanded = await recap.ExpandAsync(prompt, DateTimeOffset.Now.AddDays(-1));
        Assert.Contains("1. 🧠 Dog is named Biscuit", expanded);
        Assert.DoesNotContain("Older fact", expanded); // before the last brief
        Assert.DoesNotContain(LearningRecap.LearnedToken, expanded);
        Assert.Equal("No token here", await recap.ExpandAsync("No token here", null));
    }

    [Fact]
    public async Task ASkillThatKeepsGettingThumbsDownIsFlaggedAndFixedFromWhatWasSaid()
    {
        var skills = _sp.GetRequiredService<SkillStore>();
        var skill = new Skill { Name = "dinner-ideas", Description = "Suggest dinners", Instructions = "1. Look at what's in season.\n2. Suggest five dinners with recipes." };
        await skills.SaveAsync(skill);
        _team.Script = (_, messages, _) =>
        {
            var system = messages[0].Content;
            if (system.StartsWith("An AI assistant gave a reply")) return new LlmChunk("REMEMBER | Keep dinner ideas short");
            if (system.StartsWith("You edit a skill"))
            {
                Assert.Contains("too long", messages[1].Content);
                return new LlmChunk("1. Look at what's in season.\n2. Suggest three dinners, one line each, no recipes.");
            }
            return new LlmChunk("NONE");
        };
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "dinner ideas?" });
        var reply = new ChatMessage { Role = ChatRole.Assistant, Model = "main-model", Content = "Here are five dinners with full recipes…",
            Used = [new LearnedItem { Kind = LearnedItem.UsedSkill, Text = "/dinner-ideas", Ref = skill.Id }] };
        conv.Messages.Add(reply);

        await _sp.GetRequiredService<LearningLoop>().CorrectAsync(conv, reply, "way too long", () => Task.CompletedTask);

        var flagged = (await skills.FindAsync("dinner-ideas"))!;
        Assert.True(flagged.NeedsFixing);
        Assert.Equal(1, flagged.Wrong);
        Assert.Equal(["way too long"], flagged.WrongNotes);

        var result = await _sp.GetRequiredService<LearningLoop>().FixSkillAsync(flagged);
        Assert.StartsWith("Rewrote /dinner-ideas", result);
        var fixedSkill = (await skills.FindAsync("dinner-ideas"))!;
        Assert.Contains("three dinners", fixedSkill.Instructions);
        Assert.False(fixedSkill.NeedsFixing); // the new version answers the complaint
        Assert.Equal(1, fixedSkill.Wrong);    // the all-time count stays
    }

    [Fact]
    public void ASkillUnusedForTwoMonthsIsIdleUntilKept()
    {
        var now = DateTimeOffset.Now;
        Assert.False(new Skill { CreatedAt = now.AddDays(-10) }.IsIdle(now));
        Assert.True(new Skill { CreatedAt = now.AddDays(-90) }.IsIdle(now));
        Assert.False(new Skill { CreatedAt = now.AddDays(-90), LastUsed = now.AddDays(-5) }.IsIdle(now));
        Assert.False(new Skill { CreatedAt = now.AddDays(-90), KeptAt = now.AddDays(-5) }.IsIdle(now));
        Assert.False(new Skill { CreatedAt = now.AddDays(-90), Pending = true }.IsIdle(now));
    }

    private const string ChatGptJson = """
        [{"title":"Kitchen remodel","create_time":1730000000.5,"update_time":1730100000.5,"mapping":{
          "a":{"message":null},
          "b":{"message":{"author":{"role":"user"},"create_time":1730000001,"content":{"content_type":"text","parts":["I'm redoing my kitchen in Detroit"]}}},
          "c":{"message":{"author":{"role":"assistant"},"create_time":1730000002,"content":{"content_type":"text","parts":["Nice!"]}}},
          "d":{"message":{"author":{"role":"user"},"create_time":1730000003,"content":{"content_type":"multimodal_text","parts":[{"asset":"x"},"budget is 20k"]}}}}}]
        """;

    private const string ClaudeJson = """
        [{"uuid":"1","name":"Blender help","created_at":"2026-10-01T10:00:00Z","updated_at":"2026-10-02T10:00:00Z","chat_messages":[
          {"sender":"human","text":"I teach Blender on YouTube","content":[]},
          {"sender":"assistant","text":"Cool"},
          {"sender":"human","text":"","content":[{"type":"text","text":"keep answers short"}]}]}]
        """;

    [Fact]
    public async Task ItReadsTheUsersOwnMessagesFromChatGptAndClaudeExports()
    {
        var (source, chats) = await AssistantImport.ReadExportAsync(new MemoryStream(Encoding.UTF8.GetBytes(ChatGptJson)), "conversations.json");
        Assert.Equal("ChatGPT", source);
        Assert.Equal(["I'm redoing my kitchen in Detroit", "budget is 20k"], chats.Single().UserMessages);
        Assert.Equal("Kitchen remodel", chats[0].Title);

        // Claude's export, zipped, read from a stream that can't seek (as browser uploads are)
        var zip = new MemoryStream();
        using (var a = new ZipArchive(zip, ZipArchiveMode.Create, true))
        await using (var w = new StreamWriter(a.CreateEntry("data-2026/conversations.json").Open()))
            await w.WriteAsync(ClaudeJson);
        (source, chats) = await AssistantImport.ReadExportAsync(new ForwardOnly(zip.ToArray()), "export.zip");
        Assert.Equal("Claude", source);
        Assert.Equal(["I teach Blender on YouTube", "keep answers short"], chats.Single().UserMessages);
    }

    [Fact]
    public async Task ImportedFactsArePreviewedWithoutWhatHearthKnowsAndSavedWhenPicked()
    {
        var memories = _sp.GetRequiredService<MemoryStore>();
        await memories.AddAsync("Teaches Blender on YouTube.");
        var import = _sp.GetRequiredService<AssistantImport>();
        _team.Script = (_, messages, _) =>
        {
            Assert.Contains("I teach Blender on YouTube", messages[1].Content);
            Assert.DoesNotContain("Cool", messages[1].Content); // only what the user wrote
            return new LlmChunk("- Teaches Blender on YouTube.\n2. Prefers short answers.\nFacts:");
        };

        var found = await import.FromExportAsync(new MemoryStream(Encoding.UTF8.GetBytes(ClaudeJson)), "conversations.json");
        Assert.Equal("Claude", found.Source);
        Assert.Equal(["Prefers short answers."], found.Facts);
        Assert.Equal(1, await import.SaveAsync(found.Facts));
        Assert.Contains(await memories.ListAsync(), m => m.Text == "Prefers short answers.");

        var pasted = await import.FromTextAsync("- Prefers short answers.\n- Has a dog named Biscuit\n\n");
        Assert.Equal(["Has a dog named Biscuit"], pasted.Facts);
    }

    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
