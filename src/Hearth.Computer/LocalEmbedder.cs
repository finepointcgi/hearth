using Hearth.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Hearth.Computer;

/// <summary>
/// Built-in embeddings for recall by meaning: all-MiniLM-L6-v2 (quantized ONNX, ~23 MB, Apache-2.0) running on the CPU,
/// downloaded once into the data folder. Falls back to the model server's embedding model if it can't be set up.
/// </summary>
public sealed class LocalEmbedder(IAppPaths paths, IHttpClientFactory http, ServerEmbedder fallback) : IEmbedder, IDisposable
{
    public const string ModelName = "all-MiniLM-L6-v2";
    private const string Repo = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/";
    private const int MaxTokens = 256;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private DateTimeOffset _retryAfter;
    private bool _usingFallback;

    private string Dir => Path.Combine(paths.DataDirectory, "models", ModelName);

    public string Description => _usingFallback ? fallback.Description : $"built-in {ModelName}";

    private async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (_session is not null) return true;
        if (DateTimeOffset.Now < _retryAfter) return false;
        await _lock.WaitAsync(ct);
        try
        {
            if (_session is not null) return true;
            Directory.CreateDirectory(Dir);
            var model = Path.Combine(Dir, "model_quantized.onnx");
            var vocab = Path.Combine(Dir, "vocab.txt");
            var client = http.CreateClient(Hearth.Core.Llm.LlmClientFactory.HttpClientName);
            foreach (var (url, file) in new[] { (Repo + "onnx/model_quantized.onnx", model), (Repo + "vocab.txt", vocab) })
            {
                if (File.Exists(file) && new FileInfo(file).Length > 1000) continue;
                var tmp = file + ".download";
                await using (var src = await client.GetStreamAsync(url, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                File.Move(tmp, file, overwrite: true);
            }
            await using (var v = File.OpenRead(vocab))
                _tokenizer = BertTokenizer.Create(v, new BertOptions { LowerCaseBeforeTokenization = true });
            _session = new InferenceSession(model, new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) });
            _usingFallback = false;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _retryAfter = DateTimeOffset.Now.AddMinutes(10);
            return false;
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> ModelIdAsync(CancellationToken ct = default)
    {
        if (await EnsureAsync(ct)) return "local:" + ModelName;
        _usingFallback = true;
        return await fallback.ModelIdAsync(ct);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (!await EnsureAsync(ct)) return await fallback.EmbedAsync(texts, ct);
        return await Task.Run(() => texts.Select(Embed).ToArray(), ct);
    }

    private float[] Embed(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _).Select(i => (long)i).ToArray();
        var n = ids.Length;
        var shape = new[] { 1, n };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[n], shape)),
        };
        using var results = _session!.Run(inputs);
        var hidden = results.First().AsTensor<float>(); // [1, n, 384]
        var dim = hidden.Dimensions[2];
        var pooled = new float[dim];
        for (var t = 0; t < n; t++)
            for (var d = 0; d < dim; d++) pooled[d] += hidden[0, t, d];
        for (var d = 0; d < dim; d++) pooled[d] /= n;
        return Vectors.Normalize(pooled);
    }

    public void Dispose() => _session?.Dispose();
}
