using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

/// <summary>Long-running tasks recover from temporary problems, notice when they're stuck, and say why they stopped.</summary>
public sealed class TaskHardeningTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-taskfix-").FullName;
    }

    /// <summary>A model whose task turns follow <see cref="Script"/> (turn number → reply; it may throw).</summary>
    private sealed class ScriptedModel : ILlmClient, ILlmClientFactory
    {
        public int TaskTurns;
        public Func<int, LlmChunk> Script = _ => new LlmChunk("ok");
        public readonly List<string> Prompts = [];
        public ILlmClient Create(ProviderProfile profile) => this;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("fake:1b")]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);

        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (request.Tools?.Any(t => t.Name == "task_plan") != true) { yield return new LlmChunk("ok"); yield break; }
            var last = request.Messages[^1];
            if (last.Role == "tool") { yield return new LlmChunk("Noted."); yield break; }
            lock (Prompts) Prompts.Add(last.Content ?? "");
            yield return Script(Interlocked.Increment(ref TaskTurns));
        }
    }

    private static LlmChunk Call(string name, string args) => new(ToolCalls: [new LlmToolCall(Guid.NewGuid().ToString("N"), name, args)]);
    private static LlmChunk Plan => Call("task_plan", """{"steps":["Look up prices","Write the summary"]}""");

    private readonly TempPaths _paths = new();
    private readonly ServiceProvider _sp;
    private readonly ScriptedModel _model = new();
    private readonly List<string> _milestones = [];

    public TaskHardeningTests()
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
        Runner.Milestone += (_, m) => { lock (_milestones) _milestones.Add(m); };
    }

    private TaskRunner Runner => _sp.GetRequiredService<TaskRunner>();
    private TaskStore Tasks => _sp.GetRequiredService<TaskStore>();

    private async Task<AgentTask> WaitFor(string id, Func<AgentTask, bool> done)
    {
        AgentTask? t = null;
        for (var i = 0; i < 200 && (t is null || !done(t)); i++)
        {
            await Task.Delay(50);
            t = await Tasks.GetAsync(id);
        }
        Assert.True(t is not null && done(t), $"timed out; state {t?.State}, log: {string.Join(" | ", t?.Log.Select(l => l.Text) ?? [])}");
        return t!;
    }

    [Fact]
    public async Task ATemporaryProblemIsRetriedQuietlyInsteadOfFailing()
    {
        _model.Script = n => n switch
        {
            1 => throw new HttpRequestException("Connection refused (127.0.0.1:11434)"),
            2 => Plan,
            _ => Call("task_done", """{"report":"Prices summarised."}"""),
        };
        var task = await Runner.StartAsync("Compare e-bike prices", null);
        var parked = await WaitFor(task.Id, t => t.State == TaskState.Scheduled);
        Assert.True(parked.Retrying);
        Assert.Equal(1, parked.Retries);
        Assert.Contains("Connection refused", parked.LastError);
        Assert.InRange(parked.WakeAt!.Value, DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(2));

        await Runner.WakeDueAsync(DateTimeOffset.Now.AddMinutes(2));
        var done = await WaitFor(task.Id, t => t.State == TaskState.Done);
        Assert.Equal(0, done.Retries);
        lock (_milestones) Assert.DoesNotContain(_milestones, m => m.StartsWith("❌"));
        lock (_model.Prompts) Assert.DoesNotContain(_model.Prompts, p => p.Contains("You were waiting for")); // a retry isn't a wake-up check
    }

    [Fact]
    public async Task APersistentTemporaryProblemIsPutToTheUserWithWhatWentWrong()
    {
        _model.Script = _ => throw new HttpRequestException("503 Service Unavailable");
        var task = await Runner.StartAsync("Compare e-bike prices", null);
        for (var i = 0; i < AgentTask.MaxRetries; i++)
        {
            await WaitFor(task.Id, t => t.State == TaskState.Scheduled && t.Retries == i + 1);
            await Runner.WakeDueAsync(DateTimeOffset.Now.AddHours(1));
        }
        var asked = await WaitFor(task.Id, t => t.State == TaskState.WaitingForYou);
        Assert.Contains("Last error: ", asked.Question);
        Assert.Contains("503", asked.Question);
        Assert.Contains("Next: Check the model server", asked.Question);
        Assert.NotEqual(TaskState.Failed, asked.State);
    }

    [Fact]
    public async Task TheSameHardErrorThreeTimesStopsTheTaskAndSaysWhy()
    {
        _model.Script = _ => throw new InvalidOperationException("The prompt is longer than the model's context window.");
        var task = await Runner.StartAsync("Summarise my whole inbox", null);
        var failed = await WaitFor(task.Id, t => t.State == TaskState.Failed);
        Assert.Equal(AgentTask.MaxRetries, failed.Retries);
        lock (_milestones)
        {
            var said = Assert.Single(_milestones, m => m.StartsWith("❌"));
            Assert.Contains("context window", said);
            Assert.Contains("Next: ", said);
        }
    }

    [Fact]
    public async Task ADeletedWorkChatStopsTheTaskAtOnce()
    {
        _model.Script = _ => Plan;
        var task = new AgentTask { Goal = "Anything", ConversationId = "deleted-chat" };
        await Tasks.SaveAsync(task);
        Runner.EnsureStarted();
        var failed = await WaitFor(task.Id, t => t.State == TaskState.Failed);
        Assert.Equal(0, _model.TaskTurns);
        Assert.Contains("deleted", failed.LastError);
    }

    [Fact]
    public async Task ReadingWithoutFinishingAStepRunsIntoTheStepBudget()
    {
        _model.Script = n => n == 1 ? Plan : Call("calculate", """{"expression":"1+1"}""");
        var task = await Runner.StartAsync("Compare e-bike prices", null);
        var asked = await WaitFor(task.Id, t => t.State == TaskState.WaitingForYou);
        Assert.StartsWith($"Step 1 has taken {AgentTask.MaxStepCycles} work cycles", asked.Question);
        Assert.Contains("Step 1 of 2: Look up prices", asked.Question);
        Assert.Equal(1 + AgentTask.MaxStepCycles, asked.Cycles);
    }

    [Fact]
    public async Task AStuckStepCanBeSkippedInsteadOfAsking()
    {
        _model.Script = n => n == 1 ? Plan : n <= 1 + AgentTask.MaxStepCycles ? Call("calculate", """{"expression":"1+1"}""")
            : Call("task_done", """{"report":"Summary written without step 1."}""");
        var task = await Runner.StartAsync("Compare e-bike prices", null);
        await Runner.SetStateAsync(task.Id, TaskState.Paused);
        var t = (await Tasks.GetAsync(task.Id))!;
        t.OnStuckStep = "skip";
        await Tasks.SaveAsync(t);
        await Runner.SetStateAsync(task.Id, TaskState.Running);
        var done = await WaitFor(task.Id, x => x.State == TaskState.Done);
        Assert.StartsWith("Gave up after", done.Plan[0].Note);
        Assert.Contains(done.Log, e => e.Text.StartsWith("Step 1 skipped after"));
    }

    [Fact]
    public async Task OnlyTalkingIsIdleAndGetsABetterQuestion()
    {
        _model.Script = n => n == 1 ? Plan : new LlmChunk("I'm thinking about how best to approach the prices.");
        var task = await Runner.StartAsync("Compare e-bike prices", null);
        var asked = await WaitFor(task.Id, t => t.State == TaskState.WaitingForYou);
        Assert.StartsWith("I'm not making progress.", asked.Question);
        Assert.Contains("Next: Tell me how to get past step 1", asked.Question);
        lock (_model.Prompts) Assert.Contains(_model.Prompts, p => p.Contains("Which part of step 1 are you blocked on?"));
    }

    [Fact]
    public async Task WakingUpChecksTheConditionFirst()
    {
        _model.Script = n => n switch
        {
            1 => Plan,
            2 => Call("task_wait", """{"hours":24,"reason":"the price drop on Monday","done_when":"the shop lists it under $1,800"}"""),
            _ => Call("task_done", """{"report":"It dropped."}"""),
        };
        var task = await Runner.StartAsync("Tell me when the e-bike gets cheaper", null);
        var waiting = await WaitFor(task.Id, t => t.State == TaskState.Scheduled);
        Assert.Equal("the shop lists it under $1,800", waiting.WaitCondition);
        await Runner.WakeDueAsync(DateTimeOffset.Now.AddDays(2));
        await WaitFor(task.Id, t => t.State == TaskState.Done);
        lock (_model.Prompts)
            Assert.Contains(_model.Prompts, p => p.Contains("You were waiting for: the price drop on Monday")
                && p.Contains("It counts as happened when: the shop lists it under $1,800")
                && p.Contains("Do not invent progress"));
    }

    [Fact]
    public void ReplanningKeepsFinishedStepsAndIsCapped()
    {
        var task = new AgentTask { Goal = "g" };
        var tools = TaskTools.For(task).ToDictionary(t => t.Name);
        string Run(string name, JsonObject args) => tools[name].ExecuteAsync(args, null!, default).GetAwaiter().GetResult().Text;

        Run("task_plan", new() { ["steps"] = new JsonArray("A", "B", "C") });
        Run("task_step", new() { ["step"] = 1, ["status"] = "done", ["note"] = "found it" });
        // A new plan that leaves out the finished step: it stays, at the front.
        Assert.Contains("1 finished steps are kept", Run("task_plan", new() { ["steps"] = new JsonArray("X", "Y") }));
        Assert.Equal(["A", "X", "Y"], task.Plan.Select(p => p.Text));
        Assert.Equal("found it", task.Plan[0].Note);

        for (var i = 1; i < AgentTask.MaxPlansInARow; i++) Run("task_plan", new() { ["steps"] = new JsonArray("Z") }); // the re-plan above was the first
        Assert.Contains("re-planned", Run("task_plan", new() { ["steps"] = new JsonArray("Q") }));
        Assert.DoesNotContain(task.Plan, p => p.Text == "Q");
    }

    [Fact]
    public void ScratchpadKeepsTheLatestNotes()
    {
        var task = new AgentTask { Goal = "g" };
        var note = TaskTools.For(task).Single(t => t.Name == "task_note");
        for (var i = 0; i < AgentTask.MaxScratchpad + 3; i++)
            note.ExecuteAsync(new JsonObject { ["note"] = $"n{i}" }, null!, default).GetAwaiter().GetResult();
        Assert.Equal(AgentTask.MaxScratchpad, task.Scratchpad.Count);
        Assert.Equal("n3", task.Scratchpad[0]);
    }

    [Theory]
    [InlineData("Connection refused (localhost:11434)", true)]
    [InlineData("Response status code does not indicate success: 503 (Service Unavailable).", true)]
    [InlineData("Rate limit reached, try again in 20s", true)]
    [InlineData("The prompt is longer than the model's context window.", false)]
    [InlineData("Unknown tool 'gmail_send'.", false)]
    public void TemporaryProblemsAreRecognised(string message, bool temporary) =>
        Assert.Equal(temporary, TaskRunner.IsTemporary(message));

    public void Dispose()
    {
        _sp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background writer may still hold a file */ }
    }
}
