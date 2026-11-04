using System.Runtime.InteropServices;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

public sealed record DocumentInfo(string Id, string Name, int Characters, int Chunks, string EmbeddingModel, DateTimeOffset AddedAt);

public sealed record DocumentHit(string DocumentName, string Text, double Score);

/// <summary>
/// Local document library for retrieval-augmented answers. Text is chunked, embedded with a local embedding
/// model and kept as JSON on the device; search is brute-force cosine similarity (fine for personal libraries).
/// </summary>
public sealed class DocumentStore(IAppPaths paths, SettingsStore settingsStore, ILlmClientFactory factory)
{
    private sealed class Chunk { public string Text { get; set; } = ""; public string Vector { get; set; } = ""; }
    private sealed class DocFile { public List<Chunk> Chunks { get; set; } = []; }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<DocumentInfo>? _index;
    private readonly Dictionary<string, (string text, float[] vec)[]> _vectors = [];

    private string Dir => Path.Combine(paths.DataDirectory, "documents");
    private string IndexPath => Path.Combine(Dir, "index.json");

    public event Action? Changed;

    public async Task<List<DocumentInfo>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return (await IndexAsync()).ToList(); }
        finally { _lock.Release(); }
    }

    private async Task<List<DocumentInfo>> IndexAsync() =>
        _index ??= await Json.ReadAsync<List<DocumentInfo>>(IndexPath) ?? [];

    public async Task<DocumentInfo> AddAsync(string name, string text, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync();
        var model = settings.Documents.EmbeddingModel;
        var client = factory.Create(EmbeddingProvider(settings));
        var pieces = Chunker.Split(text, settings.Documents.ChunkSize, settings.Documents.ChunkSize / 6);
        if (pieces.Count == 0) throw new InvalidOperationException("No text could be extracted from this file.");

        var chunks = new List<Chunk>();
        for (var i = 0; i < pieces.Count; i += 16)
        {
            progress?.Report($"Embedding {Math.Min(i + 16, pieces.Count)}/{pieces.Count}…");
            var batch = pieces.Skip(i).Take(16).ToList();
            var vectors = await client.EmbedAsync(model, batch.Select(b => $"{name}\n{b}").ToList(), ct);
            for (var j = 0; j < batch.Count; j++)
                chunks.Add(new Chunk { Text = batch[j], Vector = Convert.ToBase64String(MemoryMarshal.AsBytes(Normalize(vectors[j]).AsSpan())) });
        }

        var info = new DocumentInfo(Guid.NewGuid().ToString("N"), name, text.Length, chunks.Count, model, DateTimeOffset.Now);
        await _lock.WaitAsync();
        try
        {
            await Json.WriteAtomicAsync(Path.Combine(Dir, info.Id + ".json"), new DocFile { Chunks = chunks });
            (await IndexAsync()).Add(info);
            await Json.WriteAtomicAsync(IndexPath, _index);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        return info;
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            (await IndexAsync()).RemoveAll(d => d.Id == id);
            await Json.WriteAtomicAsync(IndexPath, _index);
            _vectors.Remove(id);
            var f = Path.Combine(Dir, Path.GetFileName(id) + ".json");
            if (File.Exists(f)) File.Delete(f);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task<List<DocumentHit>> SearchAsync(string query, int topK, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync();
        var docs = await ListAsync();
        if (docs.Count == 0) return [];
        var q = Normalize((await factory.Create(EmbeddingProvider(settings))
            .EmbedAsync(settings.Documents.EmbeddingModel, [query], ct))[0]);

        var hits = new List<DocumentHit>();
        foreach (var doc in docs)
        {
            if (doc.EmbeddingModel != settings.Documents.EmbeddingModel) continue; // vectors not comparable
            foreach (var (text, vec) in await VectorsAsync(doc.Id))
                if (vec.Length == q.Length)
                    hits.Add(new DocumentHit(doc.Name, text, Dot(q, vec)));
        }
        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    /// <summary>
    /// Claude (API or Claude Code) can't make embeddings, so when it's the active provider the library uses the first
    /// local provider (Ollama / OpenAI-compatible) instead.
    /// </summary>
    private static ProviderProfile EmbeddingProvider(AppSettings s) =>
        s.ActiveProvider.Kind is ProviderKind.Ollama or ProviderKind.OpenAICompatible
            ? s.ActiveProvider
            : s.Providers.FirstOrDefault(p => p.Kind is ProviderKind.Ollama or ProviderKind.OpenAICompatible) ?? s.ActiveProvider;

    private async Task<(string text, float[] vec)[]> VectorsAsync(string id)
    {
        if (_vectors.TryGetValue(id, out var v)) return v;
        var file = await Json.ReadAsync<DocFile>(Path.Combine(Dir, id + ".json")) ?? new DocFile();
        v = file.Chunks.Select(c => (c.Text, MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(c.Vector)).ToArray())).ToArray();
        _vectors[id] = v;
        return v;
    }

    private static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

public static class Chunker
{
    /// <summary>Splits on paragraph boundaries into ~size-char chunks with overlap.</summary>
    public static List<string> Split(string text, int size, int overlap)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var para in paragraphs.SelectMany(p => p.Length > size ? HardSplit(p, size) : [p]))
        {
            if (current.Length > 0 && current.Length + para.Length > size)
            {
                chunks.Add(current.ToString());
                var tail = current.ToString();
                current.Clear();
                if (overlap > 0) current.Append(tail[Math.Max(0, tail.Length - overlap)..]).Append("\n\n");
            }
            current.Append(para).Append("\n\n");
        }
        if (current.ToString().Trim().Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private static IEnumerable<string> HardSplit(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}
