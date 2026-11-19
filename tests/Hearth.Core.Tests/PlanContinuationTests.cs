using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

public sealed class PlanContinuationTests
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-plan-").FullName;
    }

    private static object[] Steps(params (string step, string status)[] steps) => steps.Select(s => (object)new { s.step, s.status }).ToArray();

    private static LlmChunk SetPlan(params (string step, string status)[] steps) => ScriptedTeam.Call("update_plan", new { steps = Steps(steps) });

    private static bool Nudged(IReadOnlyList<LlmMessage> msgs) => msgs.Any(m => m.Role == "user" && m.Content.Contains("The plan isn't finished"));

    /// <summary>Runs one chat turn against a fake model and returns the chat afterwards.</summary>
    private static async Task<(Conversation conv, ScriptedTeam team)> RunAsync(Func<IReadOnlyList<LlmMessage>, LlmChunk> script)
    {
        var team = new ScriptedTeam();
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new TempPaths());
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(team));
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        var local = new ProviderProfile { Name = "local", BaseUrl = "http://127.0.0.1:9", DefaultModel = "small" };
        s.Providers = [local];
        s.ActiveProviderId = local.Id;
        s.DefaultBotId = "";
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        s.Tools.PickToolsPerMessage = false;
        await store.SaveAsync(s);
        team.Script = (_, msgs, _) => script(msgs);

        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Research my three repos and write it up." });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
        await sp.GetRequiredService<ChatService>().GenerateAsync(conv, new SendOptions("small", false, false, true), () => Task.CompletedTask, default);
        return (conv, team);
    }

    [Fact]
    public async Task AReplyThatStopsWithStepsLeftIsSentBackToFinish()
    {
        var (conv, _) = await RunAsync(msgs =>
        {
            var nudged = Nudged(msgs);
            if (msgs.Last().Role == "tool") return new LlmChunk(nudged ? "Everything is done." : "Step one is done.");
            return nudged ? SetPlan(("Look", "completed"), ("Write", "completed"))
                : SetPlan(("Look", "completed"), ("Write", "in_progress"));
        });

        Assert.All(conv.Plan, t => Assert.Equal(CodeTodoStatus.Completed, t.Status));
        var nudge = Assert.Single(conv.Messages, m => m.RoutineName == ChatService.PlanRoutine);
        Assert.Contains("\"Write\"", nudge.Content); // it names the step to do next
        Assert.Equal("Everything is done.", conv.Messages.Last(m => m.Role == ChatRole.Assistant).Content);
    }

    [Fact]
    public async Task ARealQuestionIsLeftForTheUser()
    {
        var (conv, _) = await RunAsync(msgs => msgs.Last().Role == "tool"
            ? new LlmChunk("Which of the three repos matters most to you?")
            : SetPlan(("Look", "completed"), ("Write", "pending")));

        Assert.DoesNotContain(conv.Messages, m => m.RoutineName == ChatService.PlanRoutine);
        Assert.Equal(CodeTodoStatus.Pending, conv.Plan[1].Status);
    }

    [Fact]
    public async Task ShallIContinueIsNotAQuestionToWaitFor()
    {
        Assert.False(ChatService.AsksSomethingReal("Step 2 is done. Shall I move on to step 3?"));
        Assert.False(ChatService.AsksSomethingReal("All set. Want me to continue?"));
        Assert.True(ChatService.AsksSomethingReal("Done. Which budget should I assume?"));
        Assert.False(ChatService.AsksSomethingReal("Done with step 2."));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ItStopsWhenANudgeChangesNothing()
    {
        var (conv, team) = await RunAsync(msgs => msgs.Last().Role == "tool" ? new LlmChunk("Working on it.")
            : Nudged(msgs) ? new LlmChunk("Still working on it.")
            : SetPlan(("Look", "completed"), ("Write", "in_progress")));

        Assert.Single(conv.Messages, m => m.RoutineName == ChatService.PlanRoutine); // one try, then it leaves it to you
        Assert.Equal(CodeTodoStatus.InProgress, conv.Plan[1].Status);
        Assert.True(team.Calls.Count <= 4);
    }

    [Fact]
    public async Task APlanFromAnEarlierMessageIsNotPushedOnAnUnrelatedOne()
    {
        var (conv, _) = await RunAsync(_ => new LlmChunk("Hello!")); // never touches the plan
        Assert.DoesNotContain(conv.Messages, m => m.RoutineName == ChatService.PlanRoutine);

        // A leftover unfinished plan from before, and a reply that doesn't change it.
        var team = new ScriptedTeam();
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new TempPaths());
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(team));
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<SettingsStore>();
        var s = await store.GetAsync();
        var local = new ProviderProfile { Name = "local", BaseUrl = "http://127.0.0.1:9", DefaultModel = "small" };
        s.Providers = [local];
        s.ActiveProviderId = local.Id;
        s.DefaultBotId = "";
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        await store.SaveAsync(s);
        team.Script = (_, _, _) => new LlmChunk("Sure, it's sunny.");
        var old = new Conversation { Plan = [new CodeTodo("Old step", CodeTodoStatus.InProgress)] };
        old.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "What's the weather?" });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(old);
        await sp.GetRequiredService<ChatService>().GenerateAsync(old, new SendOptions("small", false, false, true), () => Task.CompletedTask, default);
        Assert.DoesNotContain(old.Messages, m => m.RoutineName == ChatService.PlanRoutine);
    }
}
