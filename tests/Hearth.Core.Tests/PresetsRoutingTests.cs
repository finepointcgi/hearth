using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

public sealed class PresetsRoutingTests
{
    private sealed class Screenshot : ITool
    {
        public string Name => "fake_screenshot";
        public string DisplayName => "Screenshot";
        public string Icon => "📸";
        public string Description => "Takes a screenshot.";
        public JsonObject Parameters => Schema.Object();
        public bool RequiresApproval => false;
        public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(new ToolResult("Took a screenshot.", ModelImage: new ImageAttachment("screen.png", "image/png", "iVBORw0KGgo=")));
    }

    [Fact]
    public async Task AReplySwitchesToTheVisionModelOnceAScreenshotComesBack()
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
        var cloud = new ProviderProfile { Name = "cloud", BaseUrl = "http://127.0.0.1:9", DefaultModel = "big" };
        s.Providers = [local, cloud];
        s.ActiveProviderId = local.Id;
        s.AutoTitle = false;
        s.MemoryAutoLearn = false;
        s.Tools.PickToolsPerMessage = false;
        s.ModelRouting = new ModelRoutingSettings
        {
            Enabled = true, FastProviderId = local.Id, FastModel = "small", StrongProviderId = local.Id, StrongModel = "medium",
            VisionProviderId = cloud.Id, VisionModel = "eyes",
        };
        await store.SaveAsync(s);
        team.Script = (_, msgs, _) => msgs.Last().Role == "user" && msgs.Last().Images.Count > 0 ? new LlmChunk("I can see it.")
            : msgs.Any(m => m.Role == "tool") ? new LlmChunk("done") : ScriptedTeam.Call("fake_screenshot", new { });

        var conv = new Conversation();
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi, look at my screen" });
        await sp.GetRequiredService<ConversationStore>().SaveAsync(conv);
        var reply = await sp.GetRequiredService<ChatService>().RunAgentAsync(conv, null, new SendOptions("small", false, false, true),
            () => Task.CompletedTask, default, autoTitle: false, extraTools: [new Screenshot()], learn: false);

        Assert.Equal("I can see it.", reply!.Content);
        var calls = team.Calls.ToList();
        Assert.Equal(("local", "small"), (calls[0].server, calls[0].model)); // an everyday message starts on the fast model
        Assert.Equal(("cloud", "eyes"), (calls[1].server, calls[1].model)); // the screenshot moves it to the vision model
        Assert.Contains(reply.Trace, t => t.Kind == "route" && t.Name.Contains("screenshot"));
        Assert.Equal("eyes", reply.Model);
    }

    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-routing-").FullName;
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void PresetsSetTheAbilitiesAndAreRecognised()
    {
        var a = new BotAbilities { ProjectFolder = "/src/app", BrowserProfile = BrowserProfile.Shared };
        AbilityPresets.Find("safe-home")!.Apply(a);
        Assert.False(a.Gmail || a.Code || a.ComputerUse || a.Files || a.Browser || a.Contacts);
        Assert.True(a.WebSearch && a.Calendar && a.Documents);
        Assert.Equal("safe-home", AbilityPresets.Matching(a)?.Id);
        Assert.Equal("/src/app", a.ProjectFolder); // the bot's own setup is left alone
        Assert.Equal(BrowserProfile.Shared, a.BrowserProfile);

        AbilityPresets.Find("careful-work")!.Apply(a);
        Assert.True(a.Gmail && a.Browser && a.Code && a.Microsoft365);
        Assert.False(a.ComputerUse);
        Assert.Equal(CodePermissionMode.Ask, a.CodeMode);
        Assert.Equal("careful-work", AbilityPresets.Matching(a)?.Id);

        AbilityPresets.Find("full-power")!.Apply(a);
        Assert.True(a.ComputerUse);
        Assert.Equal(CodePermissionMode.AcceptEdits, a.CodeMode);
        Assert.Equal("full-power", AbilityPresets.Matching(a)?.Id);

        a.Images = false; // tuned by hand
        Assert.Null(AbilityPresets.Matching(a));
    }

    [Fact]
    public void WhatABotCanDoIsSpelledOut()
    {
        var a = new BotAbilities();
        AbilityPresets.Find("safe-home")!.Apply(a);
        var lines = AbilityPresets.Describe(a);
        Assert.Contains("search the web", lines[0]);
        Assert.Contains("see and change your calendar", lines[0]);
        Assert.Contains("email", lines[1]);
        Assert.Contains("your screen", lines[1]);

        AbilityPresets.Find("full-power")!.Apply(a);
        Assert.Contains("use your computer's screen and keyboard", AbilityPresets.Describe(a)[0]);
        Assert.DoesNotContain(AbilityPresets.Describe(a), l => l.Contains("email") && l.StartsWith("No access"));
    }

    // ------------------------------------------------------------------ model routing

    private static AppSettings Routed(bool vision = true)
    {
        var fast = new ProviderProfile { Name = "Local" };
        var strong = new ProviderProfile { Name = "Claude", Kind = ProviderKind.ClaudeCode };
        var s = new AppSettings { Providers = [fast, strong] };
        s.ModelRouting = new ModelRoutingSettings
        {
            Enabled = true, FastProviderId = fast.Id, FastModel = "llama3.2", StrongProviderId = strong.Id, StrongModel = "sonnet",
            VisionProviderId = vision ? strong.Id : null, VisionModel = vision ? "sonnet-vision" : "",
        };
        return s;
    }

    [Fact]
    public void BackgroundWorkIsSortedByWhatItNeeds()
    {
        var s = Routed();
        // A heartbeat is a check, however long its prompt.
        Assert.Equal("llama3.2", ModelRouter.Pick(s, null, new string('x', 2000), 0, false, "Heartbeat", null, 0)?.model);
        // A task's own hint wins over the message.
        var plan = ModelRouter.Pick(s, null, "hi", 0, false, "Task", new RouteHint(true, "planning the task"), 0);
        Assert.Equal("sonnet", plan?.model);
        Assert.Equal("strong model: planning the task", plan?.reason);
        Assert.Equal("llama3.2", ModelRouter.Pick(s, null, new string('x', 2000), 0, false, "Task", new RouteHint(false, "wake check"), 0)?.model);
        // A picture goes to the vision model; without one set, the usual choice (attachments → strong).
        Assert.Equal("sonnet-vision", ModelRouter.Pick(s, null, "what's this?", 1, false, null, null, 1)?.model);
        Assert.Equal("sonnet", ModelRouter.Pick(Routed(vision: false), null, "what's this?", 1, false, null, null, 1)?.model);
        // Pinned bots keep their own model, pictures included.
        Assert.Null(ModelRouter.Pick(s, new Bot { Model = "mine" }, "what's this?", 1, false, null, null, 1));
        Assert.Null(ModelRouter.VisionFor(s, new Bot { Model = "mine" }));
        Assert.Equal("sonnet-vision", ModelRouter.VisionFor(s, new Bot())?.model);
        s.ModelRouting.Enabled = false;
        Assert.Null(ModelRouter.VisionFor(s, null));
    }

    [Fact]
    public void TaskCyclesAskForTheRightModel()
    {
        var task = new AgentTask { Goal = "g" };
        Assert.True(TaskRunner.RouteFor(task)!.Strong); // no plan yet
        task.Plan = [new TaskStep { Text = "one" }];
        Assert.Null(TaskRunner.RouteFor(task)); // ordinary work: the message decides
        task.JustWoke = true;
        Assert.False(TaskRunner.RouteFor(task)!.Strong);
        task.LastError = "timeout";
        Assert.Equal("getting past a problem", TaskRunner.RouteFor(task)!.Reason);
        task.LastError = null;
        task.Nudge = "redo from step 1";
        Assert.Equal("redoing steps you asked to retry", TaskRunner.RouteFor(task)!.Reason);
        task.Nudge = null;
        task.StepCycles = 3;
        Assert.True(TaskRunner.RouteFor(task)!.Strong);
    }

    // ------------------------------------------------------------------ free time

    private static readonly TimeSpan Off = TimeSpan.FromHours(-5);
    private static DateTimeOffset At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, Off); // Nov 16 2026 is a Monday

    [Fact]
    public void FreeTimeFillsTheGapsInWorkingHours()
    {
        var busy = new[] { (At(16, 9), At(16, 10)), (At(16, 10, 30), At(16, 12)), (At(16, 13), At(16, 17)) };
        var slots = FreeTime.Find(busy, At(16, 8), At(16, 23), 30, new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal([(At(16, 10), At(16, 10, 30)), (At(16, 12), At(16, 13))], slots.Select(s => (s.Start, s.End)));

        // Too short for an hour: only the lunch gap fits.
        Assert.Single(FreeTime.Find(busy, At(16, 8), At(16, 23), 60, new TimeOnly(9, 0), new TimeOnly(17, 0)));
    }

    [Fact]
    public void FreeTimeStartsNowSkipsWeekendsAndRoundsUp()
    {
        // Friday 14:07 to Monday 10:00, nothing booked.
        var slots = FreeTime.Find([], At(20, 14, 7), At(23, 10), 30, new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal(At(20, 14, 15), slots[0].Start); // rounded to the quarter hour
        Assert.Equal(At(20, 17), slots[0].End);
        Assert.Equal((At(23, 9), At(23, 10)), (slots[1].Start, slots[1].End)); // Saturday and Sunday skipped
        Assert.Equal(2, slots.Count);

        Assert.Equal(4, FreeTime.Find([], At(20, 14, 7), At(23, 10), 30, new TimeOnly(9, 0), new TimeOnly(17, 0), weekends: true).Count);
    }

    [Fact]
    public void OverlappingAndAllDayEventsBlockTime()
    {
        var busy = new[] { (At(17, 9), At(17, 11)), (At(17, 10), At(17, 12, 10)), (At(18, 0), At(19, 0)) };
        var slots = FreeTime.Find(busy, At(17, 0), At(18, 23), 30, new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.Equal([(At(17, 12, 15), At(17, 17))], slots.Select(s => (s.Start, s.End)));
        Assert.Contains("No free", FreeTime.Describe([], 30, At(18, 0), At(18, 23)));
    }
}
