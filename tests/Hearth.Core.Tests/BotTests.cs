using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tests;

public class BotTests
{
    private static readonly List<Bot> Team =
    [
        new() { Name = "Chief of Staff" }, new() { Name = "Chief" }, new() { Name = "Researcher" }, new() { Name = "Writer" },
    ];

    [Fact]
    public void FindsMentionsInOrderAndPrefersLongestName()
    {
        var m = ChatService.Mentions("@Writer draft it after @chief of staff signs off", Team, includeEveryone: false);
        Assert.Equal(["Writer", "Chief of Staff"], m.Select(b => b.Name));
    }

    [Fact]
    public void EveryoneMeansAllButOnlyFromTheUser()
    {
        Assert.Equal(4, ChatService.Mentions("@everyone thoughts?", Team, includeEveryone: true).Count);
        Assert.Empty(ChatService.Mentions("@everyone thoughts?", Team, includeEveryone: false));
    }

    [Fact]
    public void NoMentions() => Assert.Empty(ChatService.Mentions("hello team", Team, true));

    [Fact]
    public void DailyRoutineRunsOncePerDayAfterItsTime()
    {
        var r = new Routine { Prompt = "x", Schedule = RoutineSchedule.Daily, TimeOfDay = "08:00" };
        var day = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.False(r.IsDue(day.AddHours(7.9)));
        Assert.True(r.IsDue(day.AddHours(8.1)));
        r.LastRun = day.AddHours(8.1);
        Assert.False(r.IsDue(day.AddHours(9)));
        Assert.True(r.IsDue(day.AddDays(1).AddHours(8.5)));
        Assert.False(r.IsDue(day.AddDays(1).AddHours(15))); // missed by > 6h: skip rather than run late
    }

    [Fact]
    public void WeekdayRoutineSkipsWeekends()
    {
        var r = new Routine { Prompt = "x", Schedule = RoutineSchedule.Weekdays, TimeOfDay = "09:00" };
        var saturday = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero);
        Assert.False(r.IsDue(saturday));
        Assert.True(r.IsDue(saturday.AddDays(2)));
    }

    [Fact]
    public void IntervalRoutine()
    {
        var now = DateTimeOffset.Now;
        var r = new Routine { Prompt = "x", Schedule = RoutineSchedule.EveryMinutes, IntervalMinutes = 30, LastRun = now.AddMinutes(-10) };
        Assert.False(r.IsDue(now));
        Assert.True(r.IsDue(now.AddMinutes(25)));
    }
}
