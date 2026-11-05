using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

/// <summary>Long chats are summarised to fit the model instead of overflowing its context window.</summary>
public sealed class ContextWindowTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-ctx-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly List<LlmRequest> _requests = [];
    private ServiceProvider? _sp;

    private ServiceProvider Services(int window = 4000)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.OpenAICompatible, BaseUrl = "http://127.0.0.1:9/v1", DefaultModel = "m", ContextTokens = window }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        s.MaxTokens = 512;
        store.SaveAsync(s).GetAwaiter().GetResult();
        _team.Script = (_, msgs, r) =>
        {
            lock (_requests) _requests.Add(r);
            return msgs[0].Content.StartsWith("You compress") ? new LlmChunk("- User wants a job in Detroit, floor $230k.") : new LlmChunk("ok");
        };
        return _sp;
    }

    private static Conversation LongChat(int turns, int chars)
    {
        var conv = new Conversation();
        for (var i = 0; i < turns; i++)
        {
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = $"Question {i}: " + new string('q', chars) });
            conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"Answer {i}: " + new string('a', chars) });
        }
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Latest question" });
        return conv;
    }

    private async Task<ChatMessage?> ReplyAsync(IServiceProvider sp, Conversation conv) =>
        await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, false), () => Task.CompletedTask, default);

    [Fact]
    public async Task LongChatIsSummarisedAndFits()
    {
        var sp = Services();
        var conv = LongChat(turns: 30, chars: 600); // ~12k tokens of history in a 4k window
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);

        var reply = await ReplyAsync(sp, conv);
        Assert.Equal("ok", reply!.Content);

        var summaryCalls = _requests.Count(r => r.Messages[0].Content.StartsWith("You compress"));
        Assert.Equal(1, summaryCalls);
        var answer = _requests.Last();
        var budget = ContextWindow.BudgetFor(new ProviderProfile { ContextTokens = 4000 }, new AppSettings { MaxTokens = 512 });
        Assert.True(ContextWindow.Tokens(answer.Messages) <= budget, $"{ContextWindow.Tokens(answer.Messages)} > {budget}");
        Assert.Contains(answer.Messages, m => m.Content.Contains("job in Detroit")); // the summary is sent
        Assert.DoesNotContain(answer.Messages, m => m.Content.StartsWith("Question 0:")); // the oldest messages aren't
        Assert.Equal("Latest question", answer.Messages.Last().Content);

        var saved = await sp.GetRequiredService<ConversationStore>().GetAsync(conv.Id);
        Assert.Contains("job in Detroit", saved!.Summary);
        Assert.NotNull(saved.SummaryThroughId);
        Assert.Contains(saved.Messages, m => m.Notice?.Contains("summarised") == true);

        // Next turn: the saved summary is reused, no new summary needed while it fits.
        _requests.Clear();
        saved.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "And another" });
        await ReplyAsync(sp, saved);
        Assert.Equal(0, _requests.Count(r => r.Messages[0].Content.StartsWith("You compress")));
        Assert.Contains(_requests.Last().Messages, m => m.Content.Contains("job in Detroit"));
    }

    [Fact]
    public async Task ShortChatsAreSentWhole()
    {
        var sp = Services(window: 32_000);
        var conv = LongChat(turns: 3, chars: 100);
        await ReplyAsync(sp, conv);
        Assert.Single(_requests);
        Assert.Contains(_requests[0].Messages, m => m.Content.StartsWith("Question 0:"));
        Assert.Null(conv.Summary);
    }

    [Fact]
    public async Task OlderRepliesDropTheirToolStepsAndBigResultsAreShortened()
    {
        var sp = Services(window: 32_000);
        var conv = new Conversation();
        for (var i = 0; i < 4; i++)
        {
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = $"q{i}" });
            var a = new ChatMessage { Role = ChatRole.Assistant, Content = $"a{i}" };
            a.ToolCalls.Add(new ToolCall { Name = "fetch_url", ArgumentsJson = "{}", Result = $"page {i} " + new string('x', 200), Status = ToolCallStatus.Done });
            conv.Messages.Add(a);
        }
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "now" });
        await ReplyAsync(sp, conv);
        var tools = _requests[0].Messages.Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Equal(2, tools.Count); // only the last two replies keep tool steps
        Assert.StartsWith("page 2", tools[0]);
        Assert.Contains(_requests[0].Messages, m => m.Content == "a0"); // older answers stay

        var big = Enumerable.Range(0, 6).Select(i => new LlmMessage { Role = "tool", Content = new string('r', 9000), ToolCallId = $"t{i}" }).ToList();
        var fitted = ContextWindow.FitToolResults(big, 9000);
        Assert.True(ContextWindow.Tokens(fitted) <= 9000);
        Assert.Contains("shortened", fitted[0].Content);
    }

    [Fact]
    public async Task IfSummarisingFailsTheOldestPartIsLeftOut()
    {
        var sp = Services();
        _team.Script = (_, msgs, r) =>
        {
            lock (_requests) _requests.Add(r);
            if (msgs[0].Content.StartsWith("You compress")) throw new HttpRequestException("server busy");
            return new LlmChunk("ok");
        };
        var conv = LongChat(turns: 30, chars: 600);
        var reply = await ReplyAsync(sp, conv);
        Assert.Equal("ok", reply!.Content);
        var answer = _requests.Last();
        Assert.Contains(answer.Messages, m => m.Content.Contains("earlier messages aren't shown"));
        Assert.Null(conv.Summary);
    }

    [Theory]
    [InlineData(ProviderKind.Ollama, 8192)]
    [InlineData(ProviderKind.Anthropic, 200_000)]
    [InlineData(ProviderKind.OpenAICompatible, 32_768)]
    public void DefaultWindows(ProviderKind kind, int expected) =>
        Assert.Equal(expected, ContextWindow.WindowFor(new ProviderProfile { Kind = kind }, new AppSettings { ContextLength = 8192 }));

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
