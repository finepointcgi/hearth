using System.Runtime.CompilerServices;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Tasks that span days: they set themselves aside with task_wait and wake up on their own.</summary>
public sealed class LongTaskTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-longtask-").FullName;
    }

    /// <summary>Plans, does a quiet step, a step worth telling, waits two days for replies, then finishes.</summary>
    private sealed class ScriptedModel : ILlmClient, ILlmClientFactory
    {
        public int TaskTurns;
        public readonly List<string> Prompts = [];
        public ILlmClient Create(ProviderProfile profile) => this;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("fake:1b")]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);

        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (request.Tools?.Any(t => t.Name == "task_wait") != true) { yield return new LlmChunk("ok"); yield break; }
            var last = request.Messages[^1];
            if (last.Role == "tool") { yield return new LlmChunk("Noted."); yield break; }
            lock (Prompts) Prompts.Add(last.Content ?? "");
            var n = Interlocked.Increment(ref TaskTurns);
            yield return n switch
            {
                1 => Call("task_plan", """{"steps":["Email three landlords","Wait for replies","Compare the offers"]}"""),
                2 => Call("task_step", """{"step":1,"status":"done","note":"Sent three emails"}"""),
                3 => Call("task_step", """{"step":2,"status":"working","note":"One landlord can show it Saturday","tell_user":true}"""),
                4 => Call("task_wait", """{"hours":48,"reason":"replies from the other two landlords"}"""),
                _ => Call("task_done", """{"report":"Two replies came in; the Elm St flat is the best value."}"""),
            };
        }

        private static LlmChunk Call(string name, string args) => new(ToolCalls: [new LlmToolCall(Guid.NewGuid().ToString("N"), name, args)]);
    }

    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;
    private readonly ScriptedModel _model = new();

    public LongTaskTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_model));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "fake", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "fake:1b" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    private async Task<AgentTask> WaitFor(string id, TaskState state)
    {
        var tasks = _sp.GetRequiredService<TaskStore>();
        AgentTask? t = null;
        for (var i = 0; i < 150 && t?.State != state; i++)
        {
            await Task.Delay(100);
            t = await tasks.GetAsync(id);
        }
        Assert.Equal(state, t!.State);
        return t;
    }

    [Fact]
    public async Task WaitsForDaysThenWakesAndFinishes()
    {
        var runner = _sp.GetRequiredService<TaskRunner>();
        var milestones = new List<string>();
        runner.Milestone += (_, m) => { lock (milestones) milestones.Add(m); };

        var task = await runner.StartAsync("Find me a flat to rent", null);
        var waiting = await WaitFor(task.Id, TaskState.Scheduled);
        Assert.Equal("replies from the other two landlords", waiting.WaitReason);
        Assert.InRange(waiting.WakeAt!.Value, DateTimeOffset.Now.AddHours(47), DateTimeOffset.Now.AddHours(49));

        lock (milestones)
        {
            Assert.Contains(milestones, m => m.Contains("Plan for"));
            Assert.Contains(milestones, m => m.Contains("can show it Saturday")); // flagged for the user
            Assert.DoesNotContain(milestones, m => m.Contains("Sent three emails")); // routine progress stays quiet
            Assert.DoesNotContain(milestones, m => m.Contains("Waiting until"));
        }

        // Not due yet: nothing happens.
        Assert.Equal(0, await runner.WakeDueAsync(DateTimeOffset.Now.AddHours(1)));
        Assert.Equal(TaskState.Scheduled, (await _sp.GetRequiredService<TaskStore>().GetAsync(task.Id))!.State);

        // Two days later it picks itself up, knows what it was waiting for, and finishes.
        Assert.Equal(1, await runner.WakeDueAsync(DateTimeOffset.Now.AddDays(3)));
        var done = await WaitFor(task.Id, TaskState.Done);
        Assert.Contains("Elm St", done.Result);
        Assert.Equal(1, done.Wakes);
        Assert.Null(done.WakeAt);
        lock (_model.Prompts) Assert.Contains(_model.Prompts, p => p.Contains("waiting for: replies from the other two landlords"));
        lock (milestones) Assert.Contains(milestones, m => m.StartsWith("🏁"));
    }

    [Fact]
    public void WaitsAreKeptBetweenSixMinutesAndAMonth()
    {
        var task = new AgentTask { Goal = "x" };
        var wait = Tools.TaskTools.For(task).Single(t => t.Name == "task_wait");
        wait.ExecuteAsync(new() { ["hours"] = 0.001, ["reason"] = "r" }, null!, default).GetAwaiter().GetResult();
        Assert.True(task.WakeAt >= DateTimeOffset.Now.AddMinutes(5));
        wait.ExecuteAsync(new() { ["hours"] = 5000, ["reason"] = "r" }, null!, default).GetAwaiter().GetResult();
        Assert.True(task.WakeAt <= DateTimeOffset.Now.AddDays(30).AddMinutes(1));
        Assert.Contains("how long", wait.ExecuteAsync(new() { ["reason"] = "r" }, null!, default).GetAwaiter().GetResult().Text);
        Assert.Equal(TaskState.Scheduled, task.State);
    }

    [Fact]
    public async Task WakingAddsASmallBudgetButNeverPastTheHardLimit()
    {
        // The runner's loop isn't started here, so the tasks just wake and wait to be worked on.
        var store = _sp.GetRequiredService<TaskStore>();
        var runner = _sp.GetRequiredService<TaskRunner>();
        var fresh = new AgentTask { Goal = "a", State = TaskState.Scheduled, WakeAt = DateTimeOffset.Now.AddMinutes(-1), Cycles = 28, MaxCycles = 30, WaitReason = "x" };
        var old = new AgentTask { Goal = "b", State = TaskState.Scheduled, WakeAt = DateTimeOffset.Now.AddMinutes(-1), Cycles = 195, MaxCycles = 195, WaitReason = "y" };
        await store.SaveAsync(fresh);
        await store.SaveAsync(old);

        Assert.Equal(2, await runner.WakeDueAsync(DateTimeOffset.Now));
        Assert.Equal(38, (await store.GetAsync(fresh.Id))!.MaxCycles);
        Assert.Equal(AgentTask.HardCycleLimit, (await store.GetAsync(old.Id))!.MaxCycles);
        Assert.All(await store.ListAsync(), t => Assert.Equal(TaskState.Running, t.State));
    }

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writer may still hold a file */ }
    }
}
