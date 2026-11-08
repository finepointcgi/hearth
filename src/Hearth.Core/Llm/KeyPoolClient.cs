using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Hearth.Core.Models;

namespace Hearth.Core.Llm;

/// <summary>
/// Several API keys for one server ("credential pool"): calls take turns across the keys, and when a key hits a rate
/// limit, runs out of quota or is rejected (before any reply has streamed), the same request moves on to the next
/// key while that one rests for a minute.
/// </summary>
public sealed class KeyPoolClient(IReadOnlyList<(string keyId, ILlmClient client)> keys) : ILlmClient
{
    public static readonly TimeSpan RestAfterLimit = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Resting = new();
    private static int _turn;

    /// <summary>A short stable id for a key (never the key itself).</summary>
    public static string KeyId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    /// <summary>Errors that mean "this key can't be used right now" rather than "the request is wrong".</summary>
    public static bool IsKeyProblem(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.PaymentRequired }) return true;
            var m = e.Message;
            if (m.Contains("429") || m.Contains("529") || m.Contains("401") || m.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || m.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) || m.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || m.Contains("overloaded", StringComparison.OrdinalIgnoreCase) || m.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || m.Contains("insufficient", StringComparison.OrdinalIgnoreCase) || m.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
                || m.Contains("invalid x-api-key", StringComparison.OrdinalIgnoreCase) || m.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Keys in the order to try this time: taking turns, resting keys last.</summary>
    private List<(string keyId, ILlmClient client)> Order()
    {
        var start = (int)((uint)Interlocked.Increment(ref _turn) % (uint)keys.Count);
        var rotated = keys.Skip(start).Concat(keys.Take(start)).ToList();
        var now = DateTimeOffset.UtcNow;
        return rotated.OrderBy(k => Resting.TryGetValue(k.keyId, out var until) && until > now).ToList();
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => TryEach(c => c.ListModelsAsync(ct));
    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => TryEach(c => c.EmbedAsync(model, inputs, ct));
    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => keys[0].client.GetCapabilitiesAsync(model, ct);

    private async Task<T> TryEach<T>(Func<ILlmClient, Task<T>> call)
    {
        Exception? last = null;
        foreach (var (id, client) in Order())
        {
            try { return await call(client); }
            catch (Exception ex) when (IsKeyProblem(ex)) { Resting[id] = DateTimeOffset.UtcNow + RestAfterLimit; last = ex; }
        }
        throw last ?? new LlmException("No API key worked.");
    }

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var order = Order();
        Exception? last = null;
        for (var i = 0; i < order.Count; i++)
        {
            var (id, client) = order[i];
            var enumerator = client.StreamChatAsync(request, ct).GetAsyncEnumerator(ct);
            bool hasFirst;
            try { hasFirst = await enumerator.MoveNextAsync(); }
            catch (Exception ex) when (IsKeyProblem(ex) && !ct.IsCancellationRequested)
            {
                Resting[id] = DateTimeOffset.UtcNow + RestAfterLimit;
                last = ex;
                await enumerator.DisposeAsync();
                continue;
            }
            try
            {
                if (hasFirst) yield return enumerator.Current;
                while (await enumerator.MoveNextAsync()) yield return enumerator.Current;
            }
            finally { await enumerator.DisposeAsync(); }
            yield break;
        }
        throw last ?? new LlmException("No API key worked.");
    }

    /// <summary>For tests: forget which keys are resting.</summary>
    public static void ResetForTests() => Resting.Clear();
}
