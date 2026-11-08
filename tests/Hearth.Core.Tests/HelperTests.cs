using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Helper agents: parallel up to the configured limit, results come back, nothing left behind.</summary>
public sealed class HelperTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-helpers-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private ServiceProvider? _sp;

    private ServiceProvider Services(int maxHelpers)
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
        s.Tools.MaxHelpers = maxHelpers;
        store.SaveAsync(s).GetAwaiter().GetResult();

        _team.DelayFor = _ => TimeSpan.FromMilliseconds(150);
        _team.Script = (_, msgs, r) =>
        {
            var last = msgs.Last();
            if (msgs.Any(m => m.Content.StartsWith("You're a helper")))
            {
                Assert.DoesNotContain(r.Tools ?? [], t => t.Name == "delegate_task"); // helpers can't start helpers
                var part = msgs.First(m => m.Content.StartsWith("You're a helper")).Content.Split("Your part:\n")[1];
                return new LlmChunk($"result for {part}");
            }
            if (last.Role == "tool") return new LlmChunk("All five checked.");
            return ScriptedTeam.Call("delegate_task", new { goals = new[] { "company A", "company B", "company C", "company D", "company E" }, shared_context = "Director roles" });
        };
        return _sp;
    }

    private async Task<(ChatMessage reply, ChatService chat)> RunAsync(int maxHelpers)
    {
        var sp = Services(maxHelpers);
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Check these five companies" });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
        var chat = sp.GetRequiredService<ChatService>();
        var reply = await chat.RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask, default);
        return (reply!, chat);
    }

    [Fact]
    public async Task HelpersWorkInParallelUpToTheLimitAndReportBack()
    {
        var (reply, chat) = await RunAsync(maxHelpers: 3);
        Assert.Equal("All five checked.", reply.Content);
        var call = Assert.Single(reply.ToolCalls, t => t.Name == "delegate_task");
        foreach (var c in "ABCDE") Assert.Contains($"result for company {c}", call.Result);
        Assert.Equal(3, chat.PeakHelpers); // at most three at a time
        Assert.Single((await _sp!.GetRequiredService<ConversationStore>().ListAsync())); // helper chats are deleted
    }

    [Fact]
    public async Task LimitOfOneRunsThemOneByOne()
    {
        var (reply, chat) = await RunAsync(maxHelpers: 1);
        Assert.Contains("result for company E", reply.ToolCalls.Single(t => t.Name == "delegate_task").Result);
        Assert.Equal(1, chat.PeakHelpers);
    }

    [Fact]
    public async Task ZeroTurnsHelpersOff()
    {
        var sp = Services(maxHelpers: 0);
        LlmRequest? seen = null;
        _team.Script = (_, _, r) => { seen = r; return new LlmChunk("ok"); };
        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi" });
        await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("m", false, false, true), () => Task.CompletedTask, default);
        Assert.DoesNotContain(seen!.Tools ?? [], t => t.Name == "delegate_task");
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
