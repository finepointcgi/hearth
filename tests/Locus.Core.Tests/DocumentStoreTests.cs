using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tests;

public class DocumentStoreTests
{
    private sealed class TempPaths : IAppPaths
    {
        public string DataDirectory { get; } = Directory.CreateTempSubdirectory("locus-test-").FullName;
    }

    /// <summary>Embeds text as keyword counts, so similarity is predictable.</summary>
    private sealed class KeywordEmbedder : ILlmClient, ILlmClientFactory
    {
        private static readonly string[] Words = ["dragon", "invoice", "moon", "recipe"];
        public ILlmClient Create(ProviderProfile profile) => this;
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(inputs.Select(t => Words.Select(w => (float)CountOf(t.ToLowerInvariant(), w) + 0.01f).ToArray()).ToArray());
        private static int CountOf(string s, string w) => (s.Length - s.Replace(w, "").Length) / w.Length;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);
    }

    [Fact]
    public async Task AddsSearchesAndDeletes()
    {
        var paths = new TempPaths();
        var embedder = new KeywordEmbedder();
        var store = new DocumentStore(paths, new SettingsStore(paths), embedder);

        await store.AddAsync("bestiary.md", "The red dragon sleeps on gold.\n\nDragons hate the cold.");
        var finance = await store.AddAsync("finance.txt", "Invoice 42 is overdue.\n\nPay the invoice by Friday.");

        var hits = await store.SearchAsync("where does the dragon sleep", 1, CancellationToken.None);
        Assert.Equal("bestiary.md", hits[0].DocumentName);

        hits = await store.SearchAsync("unpaid invoice", 1, CancellationToken.None);
        Assert.Equal("finance.txt", hits[0].DocumentName);

        // Survives a restart (fresh store reading from disk).
        var reopened = new DocumentStore(paths, new SettingsStore(paths), embedder);
        Assert.Equal(2, (await reopened.ListAsync()).Count);

        await reopened.DeleteAsync(finance.Id);
        Assert.Single(await reopened.ListAsync());
        Directory.Delete(paths.DataDirectory, recursive: true);
    }
}
