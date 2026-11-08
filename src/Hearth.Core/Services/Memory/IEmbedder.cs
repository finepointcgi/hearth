using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Turns text into unit-length vectors for recall by meaning.</summary>
public interface IEmbedder
{
    /// <summary>Identifies the vector space (vectors from different models can't be compared); null when unavailable.</summary>
    Task<string?> ModelIdAsync(CancellationToken ct = default);
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
    /// <summary>For the Memory page, e.g. "built-in all-MiniLM-L6-v2".</summary>
    string Description { get; }
}

public static class Vectors
{
    public static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }

    public static float Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        float s = 0;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static byte[] ToBytes(float[] v) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    public static float[] FromBytes(byte[] b) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b).ToArray();
}

/// <summary>Uses the embedding model on your model server (Settings → Documents), when it has one.</summary>
public sealed class ServerEmbedder(SettingsStore settingsStore, ILlmClientFactory factory) : IEmbedder
{
    private DateTimeOffset _retryAfter;
    private string? _lastModel;

    public string Description => _lastModel is null ? "your model server (no embedding model found)" : $"{_lastModel} on your model server";

    private static ProviderProfile Provider(AppSettings s) =>
        s.ActiveProvider.Kind is ProviderKind.Ollama or ProviderKind.OpenAICompatible
            ? s.ActiveProvider
            : s.Providers.FirstOrDefault(p => p.Kind is ProviderKind.Ollama or ProviderKind.OpenAICompatible) ?? s.ActiveProvider;

    public async Task<string?> ModelIdAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.Now < _retryAfter) return null;
        var s = await settingsStore.GetAsync();
        var p = Provider(s);
        try
        {
            await factory.Create(p).EmbedAsync(s.Documents.EmbeddingModel, ["ok"], ct);
            _lastModel = s.Documents.EmbeddingModel;
            return $"server:{p.Id}:{s.Documents.EmbeddingModel}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastModel = null;
            _retryAfter = DateTimeOffset.Now.AddMinutes(10);
            return null;
        }
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var s = await settingsStore.GetAsync();
        var v = await factory.Create(Provider(s)).EmbedAsync(s.Documents.EmbeddingModel, texts, ct);
        return v.Select(Vectors.Normalize).ToArray();
    }
}

/// <summary>No recall by meaning (keywords only).</summary>
public sealed class NoEmbedder : IEmbedder
{
    public static readonly NoEmbedder Instance = new();
    public string Description => "off";
    public Task<string?> ModelIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) => throw new NotSupportedException();
}
