using System.Xml.Linq;
using Hearth.Core.Models;

namespace Hearth.Core.Tests;

public sealed class BotPictureTests
{
    [Theory]
    [InlineData("Chef", "", "ghost-chef")]
    [InlineData("Chief of Staff", "Coordinator", "ghost-chief")]
    [InlineData("Researcher", "Research & fact-checking", "ghost-researcher")]
    [InlineData("Engineer", "Software engineer", "ghost-engineer")]
    [InlineData("Writer", "Writing & editing", "ghost-writer")]
    [InlineData("Critic", "Reviewer & devil's advocate", "ghost-critic")]
    [InlineData("Remy", "Home cook and meal planner", "ghost-chef")] // name says nothing: the title decides
    [InlineData("Planner", "Plans, schedules & checklists", "ghost-planner")]
    public void BotsGetTheGhostForTheirJob(string name, string title, string expected)
    {
        Assert.Equal(expected, BotPictures.For(new Bot { Name = name, Title = title })!.Id);
    }

    [Fact]
    public void OtherBotsGetAPlainGhostAndChoicesAreKept()
    {
        var plain = BotPictures.For(new Bot { Name = "Locus", Title = "" })!;
        Assert.StartsWith("ghost", plain.Id);
        Assert.DoesNotContain(plain.Id, new[] { "ghost-chef", "ghost-chief", "ghost-researcher", "ghost-engineer", "ghost-writer", "ghost-critic", "ghost-planner", "ghost-comedian" });
        Assert.NotEqual("ghost-comedian", BotPictures.For(new Bot { Name = "Defunct" })!.Id); // words match at their start only

        Assert.Equal("fox", BotPictures.For(new Bot { Name = "Chef", AvatarImage = BotPictures.Prefix + "fox" })!.Id); // picked an animal
        Assert.Null(BotPictures.For(new Bot { Name = "Chef", AvatarImage = "" }));                                     // picked the emoji
    }

    [Fact]
    public void EveryPictureIsValidSvgWithEyesToAnimate()
    {
        Assert.Equal(BotPictures.All.Count, BotPictures.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in BotPictures.All)
        {
            var svg = XElement.Parse(p.Svg);
            Assert.Contains(svg.Descendants(), e => (string?)e.Attribute("class") == "eyes");
        }
        Assert.All(BotPictures.Ghosts, g => Assert.StartsWith("ghost", g.Id));
    }
}
