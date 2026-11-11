using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Chat replies run in the background, survive the page closing, and resume after a restart.</summary>
public sealed class ChatRunsTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-runs-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly List<ServiceProvider> _sps = [];

    /// <summary>A fresh app over the same data folder (a second call = after a restart).</summary>
    private ServiceProvider StartApp()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        var sp = services.BuildServiceProvider();
        _sps.Add(sp);
        var store = sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        if (s.Providers.All(p => p.Name != "main"))
        {
            s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
            s.ActiveProviderId = s.Providers[0].Id;
            s.AutoTitle = false;
            s.MemoryAutoLearn = false;
            store.SaveAsync(s).GetAwaiter().GetResult();
        }
        return sp;
    }

    private static async Task<Conversation> NewChatAsync(IServiceProvider sp, string question)
    {
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = question });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
        return conv;
    }

    private static readonly SendOptions Opts = new("m", false, false, true);

    private static async Task WaitUntil(Func<bool> done, int seconds = 10)
    {
        for (var i = 0; i < seconds * 50 && !done(); i++) await Task.Delay(20);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task ReplyFinishesWithNobodyWatchingAndClearsItsMark()
    {
        var sp = StartApp();
        _team.DelayFor = _ => TimeSpan.FromMilliseconds(300);
        _team.Script = (_, _, _) => new LlmChunk("Found 3 jobs.");
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "find me jobs");

        var run = runs.ReplyAsync(conv, Opts); // the page that started it may close; nothing cancels the run
        Assert.True(runs.IsRunning(conv.Id));
        Assert.Same(conv, runs.Live(conv.Id));
        await WaitUntil(() => File.ReadAllText(Path.Combine(_paths.DataDirectory, "conversations", conv.Id + ".json")).Contains("\"Running\""));

        await run;
        Assert.False(runs.IsRunning(conv.Id));
        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Equal("Found 3 jobs.", saved!.Messages.Last().Content);
        Assert.Null(saved.Running);
    }

    [Fact]
    public async Task SlowModelSaysItIsWaitingThenClearsTheNote()
    {
        var sp = StartApp();
        var before = ChatService.WaitingNoticeAfter;
        ChatService.WaitingNoticeAfter = TimeSpan.FromMilliseconds(150);
        try
        {
            _team.DelayFor = _ => TimeSpan.FromMilliseconds(1500);
            _team.Script = (_, _, _) => new LlmChunk("Here you go.");
            var runs = sp.GetRequiredService<ChatRuns>();
            var conv = await NewChatAsync(sp, "research google");
            var run = runs.ReplyAsync(conv, Opts);
            await WaitUntil(() => conv.Messages.LastOrDefault()?.Notice?.Contains("Waiting for the model") == true);
            await run;
            Assert.Equal("Here you go.", conv.Messages.Last().Content);
            Assert.Null(conv.Messages.Last().Notice);
        }
        finally { ChatService.WaitingNoticeAfter = before; }
    }

    [Fact]
    public async Task ToolCallsWrittenAsTextStillRun()
    {
        var sp = StartApp();
        var round = 0;
        _team.Script = (_, msgs, req) => Interlocked.Increment(ref round) == 1
            ? new LlmChunk("Let me work it out.\n<function_calls>\n[{\"name\": \"calculate\", \"arguments\": {\"expression\": \"6*7\"}}]\n</function_calls>")
            : new LlmChunk("It's " + msgs.Last(m => m.Role == "tool").Content);
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "what's 6 times 7");
        await runs.ReplyAsync(conv, Opts);
        var reply = conv.Messages.Last();
        Assert.Equal("calculate", Assert.Single(reply.ToolCalls).Name);
        Assert.DoesNotContain("function_calls", reply.Content);
        Assert.Contains("42", reply.Content);
    }

    [Fact]
    public async Task DraftsBeforeToolCallsGoWithTheStepsNotTheAnswer()
    {
        var sp = StartApp();
        var round = 0;
        string? replayed = null;
        _team.Script = (_, msgs, _) =>
        {
            if (Interlocked.Increment(ref round) == 1)
                return new LlmChunk("Based on what I know it's about 40. Let me check.",
                    ToolCalls: [new LlmToolCall("c1", "calculate", "{\"expression\": \"6*7\"}")]);
            replayed = msgs.LastOrDefault(m => m.Role == "assistant")?.Content;
            return new LlmChunk("It's 42.");
        };
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "what's 6 times 7");
        await runs.ReplyAsync(conv, Opts);
        var reply = conv.Messages.Last();
        Assert.Equal("It's 42.", reply.Content);
        Assert.Equal("Based on what I know it's about 40. Let me check.", reply.ToolCalls[0].Preface);
        Assert.Equal("Based on what I know it's about 40. Let me check.", replayed); // the model knows what it said
    }

    [Fact]
    public async Task LastRoundAsksForAnAnswerAndHidesLateCalls()
    {
        var sp = StartApp();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.Tools.MaxRounds = 2;
        await store.SaveAsync(s);
        LlmRequest? last = null;
        _team.Script = (_, _, req) =>
        {
            last = req;
            return req.Tools is { Count: > 0 }
                ? ScriptedTeam.Call("calculate", new { expression = "1+1" })
                : new LlmChunk("<tool_call>\n<function=calculate>\n<parameter=expression>\n2+2\n</parameter>\n</function>\n</tool_call>");
        };
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "keep calculating");
        await runs.ReplyAsync(conv, Opts);
        Assert.Contains("all the tool steps", last!.Messages[^1].Content);
        var reply = conv.Messages.Last();
        Assert.DoesNotContain("<tool_call>", reply.Content);
        Assert.Contains("used all 2 tool steps", reply.Notice);
    }

    [Fact]
    public async Task RunningOutOfStepsIsAlwaysExplained()
    {
        // The model obediently answers on the last step (no late calls): the user still learns it ran out, rather than
        // seeing a bot that seems to have stopped halfway on its own.
        var sp = StartApp();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.Tools.MaxRounds = 2;
        await store.SaveAsync(s);
        _team.Script = (_, _, req) => req.Tools is { Count: > 0 }
            ? ScriptedTeam.Call("calculate", new { expression = "1+1" })
            : new LlmChunk("Partway there.");
        var conv = await NewChatAsync(sp, "keep calculating");
        await sp.GetRequiredService<ChatRuns>().ReplyAsync(conv, Opts);
        Assert.Equal("Partway there.", conv.Messages.Last().Content);
        Assert.Contains("used all 2 tool steps", conv.Messages.Last().Notice);

        // Finishing with steps to spare says nothing.
        s.Tools.MaxRounds = 5;
        await store.SaveAsync(s);
        var calls = 0;
        _team.Script = (_, _, _) => calls++ == 0 ? ScriptedTeam.Call("calculate", new { expression = "1+1" }) : new LlmChunk("It's 2.");
        var done = await NewChatAsync(sp, "add one and one");
        await sp.GetRequiredService<ChatRuns>().ReplyAsync(done, Opts);
        Assert.Null(done.Messages.Last().Notice);
    }

    [Fact]
    public void ComputerAndBrowserUseGetTheLongStepBudget()
    {
        Assert.True(ChatService.NeedsManySteps("desktop_click"));
        Assert.True(ChatService.NeedsManySteps("browser_open"));
        Assert.False(ChatService.NeedsManySteps("web_search"));
        var s = new AppSettings();
        s.Tools.MaxRounds = 12;
        s.Tools.CodeMaxRounds = 60;
        Assert.Equal(60, ChatService.LongTaskRounds(s, null));
        Assert.Equal(8, ChatService.LongTaskRounds(s, 8)); // a team member's tighter limit still wins
    }

    [Fact]
    public async Task LastStepKeepsToolsListedAndFallsBackWhenTheServerIgnoresIt()
    {
        var sp = StartApp();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        s.Tools.MaxRounds = 2;
        s.Providers[0].Kind = ProviderKind.OpenAICompatible;
        await store.SaveAsync(s);
        var requests = new List<LlmRequest>();
        _team.Script = (_, _, req) =>
        {
            requests.Add(req);
            // Like oMLX: tool_choice "none" is ignored and it calls a tool anyway.
            return req.Tools is { Count: > 0 } ? ScriptedTeam.Call("calculate", new { expression = "1+1" }) : new LlmChunk("FINAL answer.");
        };
        var conv = await NewChatAsync(sp, "keep calculating");
        await sp.GetRequiredService<ChatRuns>().ReplyAsync(conv, Opts);

        Assert.Equal(3, requests.Count);
        Assert.True(requests[1].NoToolCalls); // the last step first keeps the tools listed (the server's cache still fits)
        Assert.NotNull(requests[1].Tools);
        Assert.Null(requests[2].Tools);       // ignored: asked again without them
        Assert.Equal("FINAL answer.", conv.Messages.Last().Content);
        Assert.Single(conv.Messages.Last().ToolCalls);
    }

    [Fact]
    public async Task RestartPicksUpWhereItLeftOff()
    {
        var first = StartApp();
        _team.DelayFor = _ => TimeSpan.FromSeconds(30);
        var runs = first.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(first, "find me jobs");
        _ = runs.ReplyAsync(conv, Opts with { Think = true });
        var store = first.GetRequiredService<ConversationStore>();
        await WaitUntil(() => { try { return store.GetAsync(conv.Id).GetAwaiter().GetResult()?.Running is not null; } catch (IOException) { return false; } });
        runs.PrepareForShutdown(); // the app stops mid-reply
        runs.Stop(conv.Id);
        await Task.Delay(300);

        var second = StartApp();
        _team.DelayFor = null;
        _team.Script = (_, _, r) => new LlmChunk($"Resumed with {r.Model}.");
        var again = second.GetRequiredService<ChatRuns>();
        Assert.Equal(1, await again.ResumeInterruptedAsync());
        await WaitUntil(() => !again.IsRunning(conv.Id));

        var saved = await second.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Contains(saved!.Messages, m => m.Notice?.Contains("picks up where it left off") == true);
        Assert.Equal("Resumed with m.", saved.Messages.Last().Content);
        Assert.Null(saved.Running);
        Assert.True(saved.Unread);
        Assert.Equal(0, await again.ResumeInterruptedAsync()); // once only
    }

    [Fact]
    public async Task StaleOrRepeatedlyInterruptedRunsAreNotResumed()
    {
        var sp = StartApp();
        var store = sp.GetRequiredService<ConversationStore>();
        var old = await NewChatAsync(sp, "old");
        old.Running = new PendingRun("m", false, false, true, DateTimeOffset.Now - ChatRuns.ResumeWithin - TimeSpan.FromMinutes(1));
        await store.SaveAsync(old);
        var crashy = await NewChatAsync(sp, "crashy");
        crashy.Running = new PendingRun("m", false, false, true, DateTimeOffset.Now, Resumes: ChatRuns.MaxResumes);
        await store.SaveAsync(crashy);

        Assert.Equal(0, await sp.GetRequiredService<ChatRuns>().ResumeInterruptedAsync());
        foreach (var id in new[] { old.Id, crashy.Id })
        {
            var c = await store.GetAsync(id);
            Assert.Null(c!.Running);
            Assert.Contains("Send a message to carry on", c.Messages.Last().Notice);
        }
    }

    [Fact]
    public async Task StopEndsTheReplyAndItIsNotResumed()
    {
        var sp = StartApp();
        _team.DelayFor = _ => TimeSpan.FromSeconds(30);
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "long job");
        var run = runs.ReplyAsync(conv, Opts);
        await Task.Delay(200);
        runs.Stop(conv.Id);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(runs.IsRunning(conv.Id));
        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Null(saved!.Running);
        Assert.Equal(0, await StartApp().GetRequiredService<ChatRuns>().ResumeInterruptedAsync());
    }

    [Fact]
    public async Task OneReplyAtATimePerChat()
    {
        var sp = StartApp();
        _team.DelayFor = _ => TimeSpan.FromSeconds(30);
        var runs = sp.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(sp, "hi");
        _ = runs.ReplyAsync(conv, Opts);
        Assert.Throws<InvalidOperationException>(() => { _ = runs.ReplyAsync(conv, Opts); });
        runs.Stop(conv.Id);
    }

    public void Dispose()
    {
        foreach (var sp in _sps) sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
