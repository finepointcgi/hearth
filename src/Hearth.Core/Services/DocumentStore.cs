using System.Runtime.InteropServices;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <param name="EmbeddingModel">"" = indexed for keyword search only (no embedding model was available).</param>
/// <param name="Source">For a file from a linked folder: its full path (the folder sync keeps it current).</param>
public sealed record DocumentInfo(string Id, string Name, int Characters, int Chunks, string EmbeddingModel, DateTimeOffset AddedAt,
    string? Source = null, string? FolderId = null, DateTime? SourceModifiedUtc = null, long? SourceSize = null)
{
    public bool KeywordOnly => EmbeddingModel.Length == 0;
}

/// <summary>Where a document came from (a file in a linked folder).</summary>
public sealed record DocumentSource(string Path, string FolderId, DateTime ModifiedUtc, long Size);

public sealed record DocumentHit(string DocumentName, string Text, double Score);

/// <summary>
/// Local document library for retrieval-augmented answers. Text is chunked, embedded with a local embedding
/// model and kept as JSON on the device; search is brute-force cosine similarity (fine for personal libraries).
/// Without an embedding model, documents are still indexed and found by keyword search.
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

    /// <summary>
    /// Indexes a document. With <paramref name="source"/>, replaces any earlier version of that file. When the
    /// embedding model can't be reached (or <paramref name="embed"/> is false) it's indexed for keyword search.
    /// </summary>
    public async Task<DocumentInfo> AddAsync(string name, string text, IProgress<string>? progress = null, CancellationToken ct = default,
        DocumentSource? source = null, bool embed = true, bool notify = true)
    {
        var settings = await settingsStore.GetAsync();
        var model = settings.Documents.EmbeddingModel;
        var pieces = Chunker.Split(text, settings.Documents.ChunkSize, settings.Documents.ChunkSize / 6);
        if (pieces.Count == 0) throw new InvalidOperationException("No text could be extracted from this file.");

        var chunks = pieces.Select(p => new Chunk { Text = p }).ToList();
        if (embed && !string.IsNullOrWhiteSpace(model))
        {
            try
            {
                var client = factory.Create(EmbeddingProvider(settings));
                for (var i = 0; i < pieces.Count; i += 16)
                {
                    progress?.Report($"Embedding {Math.Min(i + 16, pieces.Count)}/{pieces.Count}…");
                    var batch = pieces.Skip(i).Take(16).ToList();
                    var vectors = await client.EmbedAsync(model, batch.Select(b => $"{name}\n{b}").ToList(), ct);
                    for (var j = 0; j < batch.Count; j++)
                        chunks[i + j].Vector = Convert.ToBase64String(MemoryMarshal.AsBytes(Normalize(vectors[j]).AsSpan()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                foreach (var c in chunks) c.Vector = ""; // no embedding model: keyword search still works
                embed = false;
            }
        }
        else embed = false;

        var info = new DocumentInfo(Guid.NewGuid().ToString("N"), name, text.Length, chunks.Count, embed ? model : "", DateTimeOffset.Now,
            source?.Path, source?.FolderId, source?.ModifiedUtc, source?.Size);
        await _lock.WaitAsync();
        try
        {
            await Json.WriteAtomicAsync(Path.Combine(Dir, info.Id + ".json"), new DocFile { Chunks = chunks });
            var index = await IndexAsync();
            if (source is not null)
                foreach (var old in index.Where(d => d.Source == source.Path).ToList()) RemoveLocked(old.Id);
            index.Add(info);
            await Json.WriteAtomicAsync(IndexPath, _index);
        }
        finally { _lock.Release(); }
        if (notify) Changed?.Invoke();
        return info;
    }

    /// <summary>Removes every document matching <paramref name="match"/> (e.g. a linked folder's files).</summary>
    public async Task<int> RemoveWhereAsync(Func<DocumentInfo, bool> match, bool notify = true)
    {
        int removed;
        await _lock.WaitAsync();
        try
        {
            var gone = (await IndexAsync()).Where(match).ToList();
            foreach (var d in gone) RemoveLocked(d.Id);
            removed = gone.Count;
            if (removed > 0) await Json.WriteAtomicAsync(IndexPath, _index);
        }
        finally { _lock.Release(); }
        if (removed > 0 && notify) Changed?.Invoke();
        return removed;
    }

    public void NotifyChanged() => Changed?.Invoke();

    private void RemoveLocked(string id)
    {
        _index?.RemoveAll(d => d.Id == id);
        _vectors.Remove(id);
        var f = Path.Combine(Dir, Path.GetFileName(id) + ".json");
        if (File.Exists(f)) File.Delete(f);
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            await IndexAsync();
            RemoveLocked(id);
            await Json.WriteAtomicAsync(IndexPath, _index);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task<List<DocumentHit>> SearchAsync(string query, int topK, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync();
        var docs = await ListAsync();
        if (docs.Count == 0) return [];
        float[]? q = null;
        if (docs.Any(d => !d.KeywordOnly && d.EmbeddingModel == settings.Documents.EmbeddingModel))
        {
            try
            {
                q = Normalize((await factory.Create(EmbeddingProvider(settings))
                    .EmbedAsync(settings.Documents.EmbeddingModel, [query], ct))[0]);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* model unavailable: keyword search for everything */ }
        }

        var terms = Keywords.Terms(query);
        var hits = new List<DocumentHit>();
        foreach (var doc in docs)
        {
            var semantic = q is not null && doc.EmbeddingModel == settings.Documents.EmbeddingModel;
            var nameScore = Keywords.Score(terms, doc.Name);
            foreach (var (text, vec) in await VectorsAsync(doc.Id))
            {
                if (semantic && vec.Length == q!.Length) hits.Add(new DocumentHit(Label(doc), text, Dot(q, vec)));
                else if (Keywords.Score(terms, text) is var s && s > 0)
                    hits.Add(new DocumentHit(Label(doc), text, 0.8 * Math.Min(1, s + 0.25 * nameScore)));
            }
        }
        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    /// <summary>Linked-folder files are labelled with their path so bots can open them.</summary>
    private static string Label(DocumentInfo d) => d.Source ?? d.Name;

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
        v = file.Chunks.Select(c => (c.Text, c.Vector.Length == 0 ? [] : MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(c.Vector)).ToArray())).ToArray();
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

/// <summary>Plain keyword matching, used when no embedding model is available.</summary>
public static class Keywords
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "be", "it", "this", "that",
        "with", "as", "at", "by", "from", "what", "which", "who", "how", "do", "does", "my", "me", "i", "about", "any",
    };

    public static string[] Terms(string query) =>
        System.Text.RegularExpressions.Regex.Split(query.ToLowerInvariant(), @"[^\p{L}\p{N}_]+")
            .Where(t => t.Length >= 2 && !Stop.Contains(t)).Distinct().ToArray();

    /// <summary>0–1: the share of query terms found, with a little extra for repeats.</summary>
    public static double Score(string[] terms, string text)
    {
        if (terms.Length == 0 || text.Length == 0) return 0;
        var lower = text.ToLowerInvariant();
        double score = 0;
        foreach (var t in terms)
        {
            var count = 0;
            for (var i = lower.IndexOf(t, StringComparison.Ordinal); i >= 0 && count < 20; i = lower.IndexOf(t, i + t.Length, StringComparison.Ordinal)) count++;
            if (count > 0) score += 1 + Math.Log(count) * 0.2;
        }
        return Math.Min(1, score / (terms.Length * 1.3));
    }
}
