using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tests;

public class SkillTests
{
    private static readonly List<Skill> Skills =
    [
        new() { Name = "summarize", Instructions = "Five bullets." },
        new() { Name = "action-items", Instructions = "Find tasks." },
    ];

    [Fact]
    public void MatchesSlashCommandWithInput()
    {
        var m = Skill.Match("/summarize the meeting went long", Skills);
        Assert.NotNull(m);
        Assert.Equal("summarize", m.Value.skill.Name);
        Assert.Equal("the meeting went long", m.Value.rest);
        Assert.Contains("Five bullets.", m.Value.skill.Expand(m.Value.rest));
    }

    [Fact]
    public void MatchesMultilineInput() =>
        Assert.Equal("line one\nline two", Skill.Match("/action-items\nline one\nline two", Skills)!.Value.rest);

    [Fact]
    public void IgnoresUnknownAndNonCommands()
    {
        Assert.Null(Skill.Match("/unknown hi", Skills));
        Assert.Null(Skill.Match("please /summarize", Skills));
        Assert.Null(Skill.Match("and/or summarize", Skills));
    }

    [Theory]
    [InlineData("Weekly Report!", "weekly-report")]
    [InlineData("  /Fix_It  ", "fixit")]
    public void Slugs(string input, string expected) => Assert.Equal(expected, Skill.Slug(input));
}

public class FeedTests
{
    [Fact]
    public void ParsesRss()
    {
        var items = FeedParser.Parse("""
            <rss version="2.0"><channel><title>Blog</title>
              <item><title>Post A</title><link>https://x.test/a</link><guid>a</guid><description>&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</description><pubDate>Tue, 03 Nov 2026 10:00:00 GMT</pubDate></item>
              <item><title>Post B</title><link>https://x.test/b</link></item>
            </channel></rss>
            """);
        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].Id);
        Assert.Contains("Hello", items[0].Summary);
        Assert.DoesNotContain("<b>", items[0].Summary);
        Assert.Equal("https://x.test/b", items[1].Id); // falls back to link
    }

    [Fact]
    public void ParsesAtom()
    {
        var items = FeedParser.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Releases</title>
              <entry><id>tag:github.com,2008:Repository/1/v1.0</id><title>v1.0</title>
                <link rel="alternate" href="https://github.com/o/r/releases/tag/v1.0"/>
                <updated>2026-11-01T00:00:00Z</updated><content type="html">&lt;p&gt;Notes&lt;/p&gt;</content></entry>
            </feed>
            """);
        Assert.Single(items);
        Assert.Equal("v1.0", items[0].Title);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.0", items[0].Link);
        Assert.Equal("Notes", items[0].Summary);
    }
}

public class DiffTests
{
    [Fact]
    public void FindsAddedAndRemovedLines()
    {
        var (added, removed) = TextDiff.Lines("Price: $10\nIn stock", "Price: $8\nIn stock\nSale!");
        Assert.Equal(["Price: $8", "Sale!"], added);
        Assert.Equal(["Price: $10"], removed);
    }

    [Fact]
    public void TriggerKindsAreNotClockDue()
    {
        var r = new Routine { Prompt = "x", Trigger = TriggerKind.Webhook, Schedule = RoutineSchedule.EveryMinutes };
        Assert.False(r.IsDue(DateTimeOffset.Now));
        var feed = new Routine { Prompt = "x", Trigger = TriggerKind.Feed, Url = "https://x.test/feed", PollMinutes = 15 };
        Assert.True(feed.IsPollDue(DateTimeOffset.Now, null));
        Assert.False(feed.IsPollDue(DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(-5)));
    }
}
