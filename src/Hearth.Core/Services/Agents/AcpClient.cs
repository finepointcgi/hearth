using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearth.Core.Services.Agents;

public sealed class AcpException(string message) : Exception(message);

/// <summary>
/// The client side of the Agent Client Protocol: JSON-RPC 2.0, one message per line, over the agent's stdin/stdout.
/// Hearth sends initialize, session/new and session/prompt; the agent streams session/update notifications and may
/// ask Hearth things (session/request_permission), which <see cref="OnRequest"/> answers.
/// </summary>
public sealed class AcpClient(Stream toAgent, Stream fromAgent) : IAsyncDisposable
{
    private readonly StreamWriter _writer = new(toAgent, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;
    private Task? _reader;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>A request from the agent (method, params) → the result to send back. Throw <see cref="AcpException"/> to answer with an error.</summary>
    public Func<string, JsonNode?, Task<JsonNode?>>? OnRequest { get; set; }
    /// <summary>A notification from the agent (method, params), e.g. session/update.</summary>
    public Action<string, JsonNode?>? OnNotification { get; set; }
    /// <summary>The agent's output ended (it quit or crashed).</summary>
    public event Action? Closed;

    public bool IsClosed { get; private set; }

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        if (IsClosed) throw new AcpException("The agent has stopped.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private async Task SendAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { throw new AcpException("The agent has stopped."); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        using var reader = new StreamReader(fromAgent, Encoding.UTF8);
        try
        {
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue; // agents sometimes print stray log lines
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (msg is null) continue;
                var method = msg["method"]?.GetValue<string>();
                var idNode = msg["id"];
                if (method is null && idNode is not null)
                {
                    if (TryId(idNode, out var id) && _pending.TryGetValue(id, out var tcs))
                    {
                        if (msg["error"] is JsonObject err) tcs.TrySetException(new AcpException(err["message"]?.GetValue<string>() ?? "The agent reported an error."));
                        else tcs.TrySetResult(msg["result"]?.DeepClone());
                    }
                }
                else if (method is not null && idNode is not null)
                    _ = AnswerAsync(idNode.DeepClone(), method, msg["params"]?.DeepClone());
                else if (method is not null)
                {
                    try { OnNotification?.Invoke(method, msg["params"]); }
                    catch { /* a bad update never stops the run */ }
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
        finally
        {
            IsClosed = true;
            foreach (var p in _pending.Values) p.TrySetException(new AcpException("The agent stopped before answering."));
            Closed?.Invoke();
        }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        JsonObject reply;
        try
        {
            if (OnRequest is null) throw new AcpException($"Hearth doesn't support {method}.");
            var result = await OnRequest(method, parameters);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (Exception ex)
        {
            var code = ex is AcpException { Message: var m } && m.StartsWith("Hearth doesn't support") ? -32601 : -32603;
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message } };
        }
        try { await SendAsync(reply); } catch (AcpException) { }
    }

    private static bool TryId(JsonNode node, out long id)
    {
        id = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out long l)) { id = l; return true; }
            if (v.TryGetValue(out int i)) { id = i; return true; }
            if (v.TryGetValue(out string? s) && long.TryParse(s, out l)) { id = l; return true; }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _writer.DisposeAsync(); } catch { }
        if (_reader is not null) try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}
