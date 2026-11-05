using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

/// <summary>Chat replies run in the background, survive the page closing, and resume after a restart.</summary>
public sealed class ChatRunsTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-runs-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly List<ServiceProvider> _sps = [];

    /// <summary>A fresh app over the same data folder (a second call = after a restart).</summary>
    private ServiceProvider StartApp()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
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
    public async Task RestartPicksUpWhereItLeftOff()
    {
        var first = StartApp();
        _team.DelayFor = _ => TimeSpan.FromSeconds(30);
        var runs = first.GetRequiredService<ChatRuns>();
        var conv = await NewChatAsync(first, "find me jobs");
        _ = runs.ReplyAsync(conv, Opts with { Think = true });
        await WaitUntil(() => File.ReadAllText(Path.Combine(_paths.DataDirectory, "conversations", conv.Id + ".json")).Contains("\"Running\""));
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
