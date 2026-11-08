using System.Net.Http;
using System.Runtime.CompilerServices;
using Hearth.Core.Models;

namespace Hearth.Core.Llm;

/// <summary>
/// Tries the active model server first; if it can't be reached (before any reply has started), retries the same
/// request on the fallback servers in order, using each one's default model, and says which one answered.
/// </summary>
public sealed class FailoverLlmClient(IReadOnlyList<(ProviderProfile profile, ILlmClient client)> chain) : ILlmClient
{
    private ILlmClient Primary => chain[0].client;
    /// <summary>Server names in the order they're tried.</summary>
    public IReadOnlyList<string> Servers => chain.Select(c => c.profile.Name).ToList();

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Primary.ListModelsAsync(ct);
    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => Primary.EmbedAsync(model, inputs, ct);
    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => Primary.GetCapabilitiesAsync(model, ct);

    /// <summary>Errors that mean "that server isn't there", as opposed to "the request was bad".</summary>
    public static bool IsUnreachable(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: null or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout }) return true;
            if (e is TaskCanceledException or TimeoutException or System.Net.Sockets.SocketException) return true;
            if (e.Message.Contains("Could not reach", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("closed the connection", StringComparison.OrdinalIgnoreCase)
                || e.Message.StartsWith("502") || e.Message.StartsWith("503") || e.Message.StartsWith("504")) return true;
        }
        return false;
    }

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Exception? firstError = null;
        for (var i = 0; i < chain.Count; i++)
        {
            var (profile, client) = chain[i];
            var model = i == 0 ? request.Model : profile.DefaultModel;
            if (i > 0 && string.IsNullOrWhiteSpace(model))
            {
                // No default: keep the chosen model if this backup has it (e.g. the picker was filled from it),
                // otherwise use its first model.
                IReadOnlyList<ModelInfo> models;
                try { models = await client.ListModelsAsync(ct); }
                catch (Exception ex) when (IsUnreachable(ex, ct)) { continue; }
                model = models.FirstOrDefault(m => m.Id == request.Model)?.Id ?? models.FirstOrDefault()?.Id;
                if (model is null) continue;
            }

            var enumerator = client.StreamChatAsync(request with { Model = model! }, ct).GetAsyncEnumerator(ct);
            LlmChunk? first = null;
            bool hasFirst;
            try
            {
                hasFirst = await enumerator.MoveNextAsync();
                if (hasFirst) first = enumerator.Current;
            }
            catch (Exception ex) when (IsUnreachable(ex, ct) && i < chain.Count - 1)
            {
                firstError ??= ex;
                await enumerator.DisposeAsync();
                continue;
            }

            try
            {
                if (i > 0)
                    yield return new LlmChunk(Notice: $"{chain[0].profile.Name} couldn't be reached, so {profile.Name} ({model}) answered.");
                if (hasFirst) yield return first!;
                while (await enumerator.MoveNextAsync()) yield return enumerator.Current;
            }
            finally { await enumerator.DisposeAsync(); }
            yield break;
        }
        throw firstError ?? new LlmException("No model server could be reached.");
    }
}
