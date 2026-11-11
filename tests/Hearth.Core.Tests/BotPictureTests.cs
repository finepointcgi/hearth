using System.Xml.Linq;
using Hearth.Core.Models;

namespace Hearth.Core.Tests;

public sealed class BotPictureTests
{
    [Theory]
    [InlineData("Chef", "", "job-chef")]
    [InlineData("Chief of Staff", "Coordinator", "job-chief")]
    [InlineData("Researcher", "Research & fact-checking", "job-researcher")]
    [InlineData("Engineer", "Software engineer", "job-engineer")]
    [InlineData("Writer", "Writing & editing", "job-writer")]
    [InlineData("Critic", "Reviewer & devil's advocate", "job-critic")]
    [InlineData("Program Manager", "Workbench: cost, schedule, staffing", "job-manager")]
    [InlineData("Systems Architect", "Meridian: SysML model and traceability", "job-architect")]
    [InlineData("Configuration Engineer", "Keel: parts, BOMs, changes", "job-configuration")]
    [InlineData("Remy", "Home cook and meal planner", "job-chef")] // the name says nothing: the title decides
    public void BotsGetTheSymbolForTheirJob(string name, string title, string expected)
    {
        Assert.Equal(expected, BotPictures.For(new Bot { Name = name, Title = title })!.Id);
    }

    [Fact]
    public void OtherBotsShowTheirInitialInTheirColour()
    {
        var pic = BotPictures.For(new Bot { Name = "locus", Title = "", Color = "#1e3a8a" })!;
        Assert.Equal(BotPictures.Initial, pic.Id);
        var svg = XElement.Parse(pic.Svg);
        Assert.Equal("L", svg.Descendants().Single(e => e.Name.LocalName == "text").Value);
        Assert.Contains("#1e3a8a", pic.Svg);
        Assert.Contains("#ffffff", pic.Svg); // white on a dark colour
        Assert.Contains("#1f2430", BotPictures.For(new Bot { Name = "Locus", Color = "#f5b942" })!.Svg); // dark on a light one
        Assert.NotEqual("job-entertainer", BotPictures.For(new Bot { Name = "Defunct" })!.Id); // words match at their start only
    }

    [Fact]
    public void NothingFromTheBotCanBreakOutOfThePicture()
    {
        var pic = BotPictures.For(new Bot { Name = "<script>", Color = "red\"/><script>" })!;
        Assert.DoesNotContain("<script", pic.Svg);
        Assert.Contains("#64748b", pic.Svg); // not a hex colour: neutral instead
        XElement.Parse(pic.Svg);
    }

    [Fact]
    public void PickedPicturesAreKept()
    {
        Assert.Equal("fox", BotPictures.For(new Bot { Name = "Chef", AvatarImage = BotPictures.Prefix + "fox" })!.Id);
        Assert.Equal("ghost-chef", BotPictures.For(new Bot { Name = "Chef", AvatarImage = BotPictures.Prefix + "ghost-chef" })!.Id);
        Assert.Null(BotPictures.For(new Bot { Name = "Chef", AvatarImage = "" })); // picked the emoji
        var picked = BotPictures.For(new Bot { Name = "Chef", Color = "#0f766e", AvatarImage = BotPictures.Prefix + "job-writer" })!;
        Assert.Equal("job-writer", picked.Id);
        Assert.Contains("#0f766e", picked.Svg); // a picked symbol still takes the bot's colour
    }

    [Fact]
    public void EveryPictureIsValidSvg()
    {
        Assert.Equal(BotPictures.All.Count, BotPictures.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in BotPictures.All)
        {
            var svg = XElement.Parse(p.Svg);
            // The characters have eyes for the blink and look-around animations.
            if (!BotPictures.IsBadge(p.Id)) Assert.Contains(svg.Descendants(), e => (string?)e.Attribute("class") == "eyes");
        }
    }
}
