using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Core.Tests;

public class BotAbilityTests
{
    [Theory]
    [InlineData("calculate", true)]
    [InlineData("web_search", true)]
    [InlineData("browser_click", false)]
    [InlineData("desktop_click", false)]
    [InlineData("run_python", false)]
    [InlineData("save_memory", true)]
    [InlineData("github_create_issue", true)]
    public void MapsToolsToAbilities(string tool, bool allowed)
    {
        var a = new BotAbilities { Browser = false, ComputerUse = false, Code = false };
        Assert.Equal(allowed, a.Allows(tool, new BotMemory()));
    }

    [Fact]
    public void MemoryOffRemovesMemoryTools() =>
        Assert.False(new BotAbilities().Allows("recall_memory", new BotMemory { Enabled = false }));

    [Fact]
    public void BrowserProfileKeyFollowsSetting()
    {
        var bot = new Bot { Id = "abc" };
        Assert.Equal("abc", bot.BrowserProfileKey);
        bot.Abilities.BrowserProfile = BrowserProfile.Shared;
        Assert.Equal("shared", bot.BrowserProfileKey);
    }

    [Fact]
    public void OldBotJsonGetsDefaults()
    {
        var bot = System.Text.Json.JsonSerializer.Deserialize<Bot>("""{"Id":"x","Name":"Old","ToolMode":0}""")!;
        Assert.True(bot.Abilities.WebSearch);
        Assert.True(bot.Memory.AutoLearn);
    }
}

public class MemorySearchTests
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("hearth-mem-").FullName;
    }

    [Fact]
    public async Task SearchesAndDedupesPerScope()
    {
        var paths = new TempPaths();
        var store = MemoryStore.Local(paths);
        await store.AddAsync("Sister Ana's birthday is May 4", "bot1");
        await store.AddAsync("Prefers metric units", "bot1");
        await store.AddAsync("Lives in Denver");

        var hits = await store.SearchAsync("when is my sister birthday", "bot1", 5);
        Assert.Equal("Sister Ana's birthday is May 4", Assert.Single(hits).Text);
        Assert.Empty(await store.SearchAsync("birthday", null, 5)); // scopes are separate

        Assert.True(await store.KnowsAsync("prefers metric units.", "bot1"));
        Assert.False(await store.KnowsAsync("prefers metric units", null));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // Windows keeps pooled files locked
        Directory.Delete(paths.DataDirectory, true);
    }
}
