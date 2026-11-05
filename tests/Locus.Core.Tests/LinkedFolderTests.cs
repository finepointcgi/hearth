using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Locus.Core.Tests;

/// <summary>Linked folders stay indexed and searchable (keyword search without an embedding model); bots can read
/// files in them but nothing hidden, linked or outside. Also: chats get real names.</summary>
public sealed class LinkedFolderTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-folders-").FullName;
    }

    private readonly TempPaths _paths = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("locus-linked-").FullName;
    private readonly ScriptedTeam _team = new(); // can't embed: everything falls back to keyword search
    private ServiceProvider? _sp;

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(_paths);
        services.AddLocusCore(new PlatformInfo("test", false, CanRunProcesses: false));
        services.Replace(ServiceDescriptor.Singleton<ILlmClientFactory>(_team));
        _sp = services.BuildServiceProvider();
        var store = _sp.GetRequiredService<SettingsStore>();
        var s = store.GetAsync().GetAwaiter().GetResult();
        s.Providers = [new ProviderProfile { Name = "main", Kind = ProviderKind.Ollama, BaseUrl = "http://127.0.0.1:9", DefaultModel = "m" }];
        s.ActiveProviderId = s.Providers[0].Id;
        s.MemoryAutoLearn = false;
        store.SaveAsync(s).GetAwaiter().GetResult();
        return _sp;
    }

    private string Write(string rel, string text)
    {
        var path = Path.Combine(_folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static ToolContext Ctx(IServiceProvider sp) => new()
    {
        Conversation = new Conversation(), Reply = new ChatMessage { Role = ChatRole.Assistant },
        Settings = sp.GetRequiredService<SettingsStore>().GetAsync().GetAwaiter().GetResult(), Client = null!, Model = "m",
    };

    [Fact]
    public async Task LinkedFolderIsIndexedKeptInSyncAndSearchable()
    {
        var sp = Services();
        var plans = Write("plans/garden.md", "# Garden\n\nPlant the tomatoes after the last frost in May.");
        Write("notes.txt", "Quarterly budget review is on Friday.");
        Write(".env", "API_KEY=secret-tomatoes");
        Write("node_modules/lib/readme.md", "tomatoes everywhere");
        Write("photo.png", "not text");
        var folders = sp.GetRequiredService<LinkedFolders>();
        var docs = sp.GetRequiredService<DocumentStore>();

        Assert.Null(await folders.AddAsync(_folder));
        var id = (await folders.ListAsync()).Single().Id;
        await folders.SyncAsync(id);

        var indexed = await docs.ListAsync();
        Assert.Equal(2, indexed.Count); // hidden files, dependencies and images are skipped
        Assert.All(indexed, d => Assert.True(d.KeywordOnly));
        var folder = (await folders.ListAsync()).Single();
        Assert.Equal((2, true, null), (folder.Files, folder.KeywordOnly, folder.Error));

        var hit = (await docs.SearchAsync("when to plant tomatoes", 3, default)).First();
        Assert.Equal(plans, hit.DocumentName); // labelled with the path, so a bot can open it
        Assert.Contains("last frost", hit.Text);

        // Changed, new and deleted files are picked up.
        File.WriteAllText(plans, "# Garden\n\nPlant peppers in June.");
        File.SetLastWriteTimeUtc(plans, DateTime.UtcNow.AddMinutes(1));
        Write("new.md", "Dentist appointment Tuesday.");
        File.Delete(Path.Combine(_folder, "notes.txt"));
        await folders.SyncAsync(id);
        indexed = await docs.ListAsync();
        Assert.Equal(2, indexed.Count);
        Assert.DoesNotContain(indexed, d => d.Name == "notes.txt");
        Assert.Contains("peppers", (await docs.SearchAsync("peppers", 3, default)).First().Text);
        Assert.Empty(await docs.SearchAsync("frost", 3, default));

        // Unlinking removes its files from the library.
        await folders.RemoveAsync(id);
        Assert.Empty(await docs.ListAsync());
    }

    [Fact]
    public async Task RejectsWholeDrivesHomeAndOverlaps()
    {
        var sp = Services();
        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.NotNull(await folders.AddAsync("/"));
        Assert.NotNull(await folders.AddAsync("~"));
        Assert.NotNull(await folders.AddAsync(Path.Combine(_folder, "missing")));
        Assert.NotNull(await folders.AddAsync(_paths.DataDirectory));
        Assert.Null(await folders.AddAsync(_folder));
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        Assert.NotNull(await folders.AddAsync(Path.Combine(_folder, "sub")));
    }

    [Fact]
    public async Task BotsReadOnlyWhatWasShared()
    {
        var sp = Services();
        var notes = Write("notes.md", "line one\nline two\nline three");
        Write(".env", "API_KEY=secret");
        Write(".git/config", "[remote]");
        var outside = Path.Combine(Directory.CreateTempSubdirectory("locus-outside-").FullName, "private.txt");
        File.WriteAllText(outside, "private");
        File.CreateSymbolicLink(Path.Combine(_folder, "sneaky.txt"), outside);
        Directory.CreateSymbolicLink(Path.Combine(_folder, "linkdir"), Path.GetDirectoryName(outside)!);

        var folders = sp.GetRequiredService<LinkedFolders>();
        Assert.Null(await folders.AddAsync(_folder));
        var read = new ReadLinkedFileTool(folders);
        var ctx = Ctx(sp);

        var ok = await read.ExecuteAsync(new JsonObject { ["path"] = notes, ["start_line"] = 2 }, ctx, default);
        Assert.False(ok.IsError, ok.Text);
        Assert.Contains("line two", ok.Text);
        Assert.DoesNotContain("line one", ok.Text);

        foreach (var bad in new[]
        {
            Path.Combine(_folder, ".env"), Path.Combine(_folder, ".git", "config"), outside,
            Path.Combine(_folder, "sneaky.txt"), Path.Combine(_folder, "linkdir", "private.txt"),
            Path.Combine(_folder, "..", Path.GetFileName(Path.GetDirectoryName(outside)!), "private.txt"),
        })
            Assert.True((await read.ExecuteAsync(new JsonObject { ["path"] = bad }, ctx, default)).IsError, bad);

        var list = await new ListLinkedFilesTool(folders).ExecuteAsync([], ctx, default);
        Assert.Contains("notes.md", list.Text);
        Assert.DoesNotContain(".env", list.Text);
        Assert.DoesNotContain("private", list.Text);

        // What the bot reads from a file is treated as information, not instructions.
        Assert.Equal("a file", ContentTrust.UntrustedSource(read));
        Assert.Equal(("documents", false), PermissionCatalog.Classify(read));
    }

    [Fact]
    public async Task ManualUploadsStillWorkWithoutAnEmbeddingModel()
    {
        var sp = Services();
        var docs = sp.GetRequiredService<DocumentStore>();
        var info = await docs.AddAsync("recipes.md", "Pancakes need flour, eggs and milk.");
        Assert.True(info.KeywordOnly);
        Assert.Contains("flour", (await docs.SearchAsync("pancake flour", 3, default)).Single().Text);
    }

    // ------------------------------------------------------------ chat names

    [Theory]
    [InlineData("Director-Level Job Search", "Director-Level Job Search")]
    [InlineData("<think>The user wants jobs…</think>\n\n\"Detroit Job Hunt.\"", "Detroit Job Hunt")]
    [InlineData("Title: **Garden Planning**", "Garden Planning")]
    [InlineData("<think>still thinking and ran out of tokens", null)]
    [InlineData("New chat", null)]
    [InlineData("", null)]
    public void CleansTitles(string raw, string? expected) => Assert.Equal(expected, ChatService.Title(raw));

    [Fact]
    public async Task ChatsGetNamedIncludingGroupsAndOldUntitledOnes()
    {
        var sp = Services();
        _team.Script = (_, msgs, r) => msgs[0].Content.StartsWith("You name chats")
            ? new LlmChunk("<think>hmm</think>Detroit Job Search")
            : new LlmChunk("Here are some roles.");
        var chat = sp.GetRequiredService<ChatService>();
        var store = sp.GetRequiredService<ConversationStore>();

        // A group chat (several bots) gets named too.
        var bots = sp.GetRequiredService<BotStore>();
        var a = new Bot { Name = "Ann" }; var b = new Bot { Name = "Bo" };
        await bots.SaveAsync(a); await bots.SaveAsync(b);
        var group = new Conversation { BotIds = [a.Id, b.Id] };
        group.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "@Ann find me director jobs" });
        await store.SaveAsync(group);
        await chat.GenerateAsync(group, new SendOptions("m", false, false, true), () => Task.CompletedTask, default);
        for (var i = 0; i < 100 && (await store.GetAsync(group.Id))!.Title == "New chat"; i++) await Task.Delay(20);
        Assert.Equal("Detroit Job Search", (await store.GetAsync(group.Id))!.Title);

        // Old chats left as "New chat" are named at startup.
        var old = new Conversation();
        old.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "jobs?" });
        old.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Sure.", Model = "m" });
        await store.SaveAsync(old);
        Assert.Equal(1, await chat.TitleUntitledAsync());
        Assert.Equal("Detroit Job Search", (await store.GetAsync(old.Id))!.Title);

        // Bots can rename a chat themselves.
        var conv = (await store.GetAsync(old.Id))!;
        var renamed = await new RenameChatTool(store).ExecuteAsync(new JsonObject { ["title"] = "Defense Tech Leadership Roles" },
            new ToolContext { Conversation = conv, Reply = new ChatMessage(), Settings = new AppSettings(), Client = null!, Model = "m" }, default);
        Assert.False(renamed.IsError);
        Assert.Equal("Defense Tech Leadership Roles", (await store.GetAsync(old.Id))!.Title);
    }

    public void Dispose()
    {
        _sp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var d in new[] { _paths.DataDirectory, _folder })
            try { Directory.Delete(d, true); } catch { }
    }
}
