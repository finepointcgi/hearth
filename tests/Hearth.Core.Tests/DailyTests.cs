using Hearth.Core;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hearth.Core.Tests;

public sealed class DailyTests : IAsyncDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-daily-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly ScriptedTeam _team = new();
    private readonly ServiceProvider _sp;

    public DailyTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddHearthCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "main-model" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.AutoTitle = false;
        s.MemoryEnabled = false;
        s.SkillLearning.Enabled = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task TheBriefIsOneRoutineThatFollowsTheSettings()
    {
        var bots = _sp.GetRequiredService<BotStore>();
        var chief = new Bot { Name = "Chief", IsCoordinator = true };
        var scout = new Bot { Name = "Scout" };
        await bots.SaveAsync(scout);
        await bots.SaveAsync(chief);
        var briefing = _sp.GetRequiredService<Briefing>();

        var b = new BriefingSettings { Enabled = true, Time = "6:45", Jobs = true, JobCriteria = "Director of Software, defense tech, $230k+", Email = false };
        await briefing.ApplyAsync(b);
        var routine = Assert.Single((await bots.GetAsync(chief.Id))!.Routines); // the coordinator by default
        Assert.Equal((Briefing.RoutineName, RoutineSchedule.Weekdays, "06:45"), (routine.Name, routine.Schedule, routine.TimeOfDay));
        Assert.Contains("matching: Director of Software, defense tech, $230k+", routine.Prompt);
        Assert.Contains("Today's calendar", routine.Prompt);
        Assert.DoesNotContain("Email:", routine.Prompt);

        // Saving again updates it; picking another bot moves it; turning it off removes it.
        b.WeekdaysOnly = false;
        await briefing.ApplyAsync(b);
        Assert.Equal(RoutineSchedule.Daily, Assert.Single((await bots.GetAsync(chief.Id))!.Routines).Schedule);
        b.BotId = scout.Id;
        await briefing.ApplyAsync(b);
        Assert.Empty((await bots.GetAsync(chief.Id))!.Routines);
        Assert.Single((await bots.GetAsync(scout.Id))!.Routines);
        b.Enabled = false;
        await briefing.ApplyAsync(b);
        Assert.Empty((await bots.GetAsync(scout.Id))!.Routines);
        Assert.False((await _sp.GetRequiredService<SettingsStore>().GetAsync()).Briefing.Enabled);
    }

    [Fact]
    public async Task SelfCheckRunsQuietlyAndAlertsWhenResultsDrop()
    {
        var good = true;
        _team.Script = (_, messages, req) =>
        {
            var ask = messages.Last(m => m.Role == "user").Content;
            var hadTool = messages.Any(m => m.Role == "tool");
            if (!good) return new LlmChunk("I don't know.");
            if (ask.Contains("1847")) return hadTool ? new LlmChunk("It's 723,174.") : ScriptedTeam.Call("calculate", new { expression = "1847*392-850" });
            if (ask.Contains("today's date")) return hadTool ? new LlmChunk($"Today is {DateTime.Now:MMMM d}.") : ScriptedTeam.Call("get_current_datetime", new { });
            if (ask.Contains("poem")) return new LlmChunk("Dark roast at dawn,\nthe day begins to hum.");
            if (ask.Contains("miles")) return hadTool ? new LlmChunk("About 42.16 km.") : ScriptedTeam.Call("calculate", new { expression = "26.2*1.609344" });
            if (ask.Contains("double that")) return new LlmChunk("72 + 7 = 79.");
            // Hearth's own features: each asks for its tool, then answers from the result.
            var afterTool = messages[^1].Role == "tool";
            if (ask.Contains("note_about")) return afterTool ? new LlmChunk("Noted.") : ScriptedTeam.Call("note_about", new { name = SelfCheck.TestPerson, note = "Recruiter at Example Corp" });
            if (ask.Contains("who_is")) return afterTool ? new LlmChunk($"{SelfCheck.TestPerson} is a recruiter at Example Corp.") : ScriptedTeam.Call("who_is", new { name = SelfCheck.TestPerson });
            if (ask.Contains("repainting")) return afterTool ? new LlmChunk("Ready.")
                : ScriptedTeam.Call("update_plan", new { steps = new[] { new { step = "Buy paint", status = "in_progress" }, new { step = "Prep walls", status = "pending" },
                    new { step = "Paint", status = "pending" }, new { step = "Clean up", status = "pending" } } });
            if (ask.Contains("meal-plan")) return afterTool ? new LlmChunk("Two dinners: tacos and pasta.") : ScriptedTeam.Call("use_skill", new { name = "meal-plan" });
            if (ask.Contains("Undo that")) return !afterTool ? ScriptedTeam.Call("undo_change", new { })
                : messages[^1].Content is var listed && System.Text.RegularExpressions.Regex.Match(listed, @"id (\w+):") is { Success: true } id
                    ? ScriptedTeam.Call("undo_change", new { id = id.Groups[1].Value }) : new LlmChunk("Undone.");
            if (ask.Contains(SelfCheck.UndoFile)) return afterTool ? new LlmChunk("Saved.") : ScriptedTeam.Call("write_file", new { path = SelfCheck.UndoFile, content = "hello" });
            return new LlmChunk("36.");
        };
        var check = _sp.GetRequiredService<SelfCheck>();
        var first = (await check.RunAsync())!;
        Assert.True(first.Total >= 8, $"only {first.Total} checks ran");
        Assert.True(first.Passed == first.Total, string.Join("; ", first.Failures));
        Assert.Empty(await _sp.GetRequiredService<PeopleStore>().ListAsync()); // the test person is removed again
        Assert.Empty(await _sp.GetRequiredService<ConversationStore>().ListAsync()); // nothing left in the sidebar

        good = false;
        var second = (await check.RunAsync())!;
        Assert.Equal(0, second.Passed);
        Assert.Equal(2, (await check.HistoryAsync()).Count);
        var feed = await _sp.GetRequiredService<ActivityLog>().SinceAsync(DateTimeOffset.Now.AddMinutes(-5));
        Assert.Contains(feed, a => a is { Kind: ActivityKind.Error, Ok: false } && a.Title.Contains($"0/{first.Total} passed, down from {first.Total}/{first.Total}"));
        Assert.Contains(feed, a => a.Ok && a.Title.Contains($"{first.Total}/{first.Total} passed"));
    }

    [Fact]
    public void SlowerRepliesCountAsWorse()
    {
        var at = DateTimeOffset.Now;
        var history = Enumerable.Range(0, 5).Select(i => new SelfCheckRun(at, "m", 5, 5, 10, [])).ToList();
        Assert.Null(SelfCheck.Worse(history, new SelfCheckRun(at, "m", 5, 5, 13, [])));
        Assert.Contains("slower", SelfCheck.Worse(history, new SelfCheckRun(at, "m", 5, 5, 20, [])));
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { }
    }
}
