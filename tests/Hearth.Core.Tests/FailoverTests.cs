using System.Runtime.CompilerServices;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Tests;

public class FailoverTests
{
    private sealed class FakeClient(Func<string, IEnumerable<LlmChunk>>? reply, Exception? fail = null) : ILlmClient
    {
        public List<string> ModelsAsked { get; } = [];
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("backup-default")]);
        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Task.FromResult<ModelCapabilities?>(null);
        public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ModelsAsked.Add(request.Model);
            await Task.Yield();
            if (fail is not null) throw fail;
            foreach (var c in reply!(request.Model)) yield return c;
        }
    }

    private static LlmRequest Req => new("primary-model", [LlmMessage.User("hi")], 0.5, 100, 0, false);

    [Fact]
    public async Task FallsBackWhenPrimaryIsUnreachable()
    {
        var primary = new FakeClient(null, new HttpRequestException("No route to host"));
        var backup = new FakeClient(m => [new LlmChunk($"hello from {m}")]);
        var client = new FailoverLlmClient([(new ProviderProfile { Name = "oMLX" }, primary), (new ProviderProfile { Name = "Claude Code", DefaultModel = "haiku" }, backup)]);
        var chunks = new List<LlmChunk>();
        await foreach (var c in client.StreamChatAsync(Req)) chunks.Add(c);
        Assert.Contains("oMLX couldn't be reached", chunks[0].Notice);
        Assert.Equal("hello from haiku", chunks[1].Content);
        Assert.Equal(["haiku"], backup.ModelsAsked);
    }

    [Fact]
    public async Task BackupWithoutDefaultModelKeepsTheChosenModelIfItHasIt()
    {
        // Hearth started while the main server was down, so the picker was filled from the backup.
        var primary = new FakeClient(null, new HttpRequestException("Connection refused"));
        var backup = new FakeClient(m => [new LlmChunk($"hello from {m}")]);
        var client = new FailoverLlmClient([(new ProviderProfile { Name = "oMLX" }, primary), (new ProviderProfile { Name = "Ollama" }, backup)]);
        var chunks = new List<LlmChunk>();
        await foreach (var c in client.StreamChatAsync(Req with { Model = "backup-default" })) chunks.Add(c);
        Assert.Equal("hello from backup-default", chunks[^1].Content);
    }

    [Fact]
    public async Task DoesNotFallBackOnRealErrors()
    {
        var primary = new FakeClient(null, new LlmException("400: context length exceeded"));
        var backup = new FakeClient(m => [new LlmChunk("nope")]);
        var client = new FailoverLlmClient([(new ProviderProfile { Name = "A" }, primary), (new ProviderProfile { Name = "B" }, backup)]);
        await Assert.ThrowsAsync<LlmException>(async () => { await foreach (var _ in client.StreamChatAsync(Req)) { } });
        Assert.Empty(backup.ModelsAsked);
    }

    [Fact]
    public async Task UsesBackupsFirstListedModelWhenNoDefault()
    {
        var primary = new FakeClient(null, new TaskCanceledException("timeout"));
        var backup = new FakeClient(m => [new LlmChunk(m)]);
        var client = new FailoverLlmClient([(new ProviderProfile { Name = "A" }, primary), (new ProviderProfile { Name = "B" }, backup)]);
        var text = "";
        await foreach (var c in client.StreamChatAsync(Req)) text += c.Content;
        Assert.Equal("backup-default", text);
    }
}
