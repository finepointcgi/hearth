using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tests;

public sealed class MemoryProviderTests : IDisposable
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-sqlite-").FullName;
    }

    /// <summary>Concept vectors: words about cars point one way, family birthdays another, servers a third.</summary>
    private sealed class ConceptEmbedder : IEmbedder
    {
        private static readonly string[][] Concepts =
        [
            ["car", "subaru", "loan", "payment", "vehicle", "financing"],
            ["sister", "sibling", "birthday", "present", "gift", "ana"],
            ["server", "plex", "ubuntu", "media", "homelab"],
        ];
        public int Calls;
        public string Description => "test concepts";
        public Task<string?> ModelIdAsync(CancellationToken ct = default) => Task.FromResult<string?>("test:concepts");
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Interlocked.Add(ref Calls, texts.Count);
            return Task.FromResult(texts.Select(t =>
            {
                var words = t.ToLowerInvariant().Split(' ', ',', '.', '?', '\'');
                var v = Concepts.Select(c => (float)words.Count(w => c.Any(w.StartsWith))).Append(0.05f).ToArray();
                return Vectors.Normalize(v);
            }).ToArray());
        }
    }

    private readonly TempPaths _paths = new();

    private static Task WriteJson<T>(string path, T value) =>
        File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(value));

    [Fact]
    public async Task StoresFactsPerScopeAndTheProfile()
    {
        using var p = new SqliteMemoryProvider(_paths, NoEmbedder.Instance);
        var a = await p.AddAsync("Lives in Denver", null);
        await p.AddAsync("Draft newsletters on Fridays", "writer");
        Assert.Equal(["Lives in Denver"], (await p.ListAsync(null)).Select(m => m.Text));
        Assert.Equal(["Draft newsletters on Fridays"], (await p.ListAsync("writer")).Select(m => m.Text));

        Assert.True(await p.UpdateAsync(a.Id, "Lives in Boulder", null));
        Assert.False(await p.UpdateAsync(a.Id, "x", "writer")); // wrong scope
        Assert.Equal("Lives in Boulder", Assert.Single(await p.SearchAsync("boulder", null, 5)).Text);
        Assert.Empty(await p.SearchAsync("denver", null, 5)); // search index follows edits

        Assert.True(await p.DeleteAsync(a.Id, null));
        Assert.Empty(await p.ListAsync(null));

        await p.SaveProfileAsync(new UserProfile { Text = "A developer.", FactsAtLastCleanup = 3, UpdatedAt = DateTimeOffset.Now });
        var profile = await p.GetProfileAsync();
        Assert.Equal(("A developer.", 3), (profile.Text, profile.FactsAtLastCleanup));

        // Survives a restart.
        using var again = new SqliteMemoryProvider(_paths, NoEmbedder.Instance);
        Assert.Single(await again.ListAsync("writer"));
    }

    [Fact]
    public async Task ImportsTheOldJsonFilesOnce()
    {
        var created = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        await WriteJson(Path.Combine(_paths.DataDirectory, "memories.json"), new List<Memory> { new("abc123", "Prefers C#", created) });
        Directory.CreateDirectory(Path.Combine(_paths.DataDirectory, "bots", "bot7"));
        await WriteJson(Path.Combine(_paths.DataDirectory, "bots", "bot7", "memories.json"), new List<Memory> { new("def456", "Uses AP style", created) });
        await WriteJson(Path.Combine(_paths.DataDirectory, "profile.json"), new UserProfile { Text = "Builds Locus." });

        using var p = new SqliteMemoryProvider(_paths, NoEmbedder.Instance);
        var shared = Assert.Single(await p.ListAsync(null));
        Assert.Equal(("abc123", "Prefers C#", created), (shared.Id, shared.Text, shared.CreatedAt));
        Assert.Equal("Uses AP style", Assert.Single(await p.ListAsync("bot7")).Text);
        Assert.Equal("Builds Locus.", (await p.GetProfileAsync()).Text);
        Assert.False(File.Exists(Path.Combine(_paths.DataDirectory, "memories.json")));
        Assert.True(File.Exists(Path.Combine(_paths.DataDirectory, "memories.json.before-sqlite.bak")));

        // A file appearing later isn't imported again.
        await WriteJson(Path.Combine(_paths.DataDirectory, "memories.json"), new List<Memory> { new("zzz", "Should not appear", created) });
        using var again = new SqliteMemoryProvider(_paths, NoEmbedder.Instance);
        Assert.Single(await again.ListAsync(null));
    }

    [Fact]
    public async Task FindsFactsByMeaningWhenNoWordsMatch()
    {
        var embedder = new ConceptEmbedder();
        using var p = new SqliteMemoryProvider(_paths, embedder);
        await p.AddAsync("Financing a Subaru Outback at 6%", null);
        await p.AddAsync("Sister Ana's birthday is May 4", null);
        await p.AddAsync("Runs Plex on Ubuntu", null);
        await p.EmbedBacklogAsync();
        Assert.Equal(3, (await p.StatsAsync()).FactsEmbedded);

        Assert.Equal("Financing a Subaru Outback at 6%", (await p.SearchAsync("how are my vehicle payments going", null, 3))[0].Text);
        Assert.Equal("Sister Ana's birthday is May 4", (await p.SearchAsync("gift for my sibling", null, 3))[0].Text);
        // Keyword hits still count.
        Assert.Equal("Runs Plex on Ubuntu", (await p.SearchAsync("plex", null, 3))[0].Text);
        Assert.Equal(0, await p.EmbedBacklogAsync()); // nothing left to embed
    }

    [Fact]
    public async Task ChatIndexSyncsWithTheFilesAndKeepsVectors()
    {
        var embedder = new ConceptEmbedder();
        using var p = new SqliteMemoryProvider(_paths, embedder);
        var store = new ConversationStore(_paths);
        var index = new ChatHistoryIndex(store, p);
        var car = new Conversation { Title = "Car", Messages = { new ChatMessage { Role = ChatRole.User, Content = "Compare the Subaru loan offers" } } };
        var bday = new Conversation { Title = "Ana", BotIds = ["helper"], Messages = { new ChatMessage { Role = ChatRole.User, Content = "Ideas for Ana's birthday present" } } };
        await store.SaveAsync(car);
        await store.SaveAsync(bday);
        await index.SyncAsync();
        await p.EmbedBacklogAsync();

        // Meaning: no shared words with "vehicle financing".
        Assert.Equal("Car", (await index.SearchAsync("vehicle financing", null))[0].Title);
        // Bots only see their own chats.
        Assert.Equal("Ana", Assert.Single(await index.SearchAsync("sibling gift", "helper")).Title);

        // Adding a message re-indexes the chat without re-embedding the old one.
        var before = embedder.Calls;
        car.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "The credit union loan is cheapest." });
        await p.IndexConversationAsync(car, store.FileTime(car.Id));
        await p.EmbedBacklogAsync();
        Assert.Equal(before + 1, embedder.Calls);

        // A deleted chat file disappears from search after the next sync.
        await store.DeleteAsync(bday.Id);
        Assert.Empty(await index.SearchAsync("birthday", null));
    }

    [Theory]
    [InlineData("newsletter pricing", "\"newsletter\" OR \"pricing\"")]
    [InlineData("\"quoted\" OR NEAR(x) *", "\"quoted\" OR \"near\"")]
    [InlineData("the a of", null)]
    public void BuildsSafeFullTextQueries(string input, string? expected) => Assert.Equal(expected, SqliteMemoryProvider.FtsQuery(input));

    [Fact]
    public void FusionAndRelevance()
    {
        Assert.Equal(["b", "a", "c"], SqliteMemoryProvider.Fuse(["a", "b"], ["b", "c"]));
        Assert.Equal(["x"], SqliteMemoryProvider.Relevant([("x", 0.34f), ("y", 0.07f), ("z", 0.04f)], 10));
        Assert.Equal(["x", "y"], SqliteMemoryProvider.Relevant([("x", 0.7f), ("y", 0.5f), ("z", 0.35f)], 10));
        Assert.Empty(SqliteMemoryProvider.Relevant([("x", 0.1f)], 10));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_paths.DataDirectory, true); } catch { /* background embedder */ }
    }
}
