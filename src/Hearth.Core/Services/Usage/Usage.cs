using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Microsoft.Data.Sqlite;

namespace Hearth.Core.Services;

/// <summary>One model call: who made it, on which server and model, how big and how long, and what it cost.</summary>
public sealed record UsageRecord(DateTimeOffset Time, string Provider, string ProviderKind, string Model, string? BotId,
    string? ConversationId, string Source, int InputTokens, int OutputTokens, long Milliseconds, decimal? Cost, bool Subscription);

/// <summary>One finished reply: how long the user waited (to the first words and to the end), how much work it took,
/// and what the answer check did (Ran = the check looked at it; Fixed = it rewrote the answer).</summary>
public sealed record ReplyRecord(DateTimeOffset Time, string Provider, string Model, string? BotId, string? ConversationId, string Source,
    long TotalMs, long? FirstTokenMs, int Rounds, int ToolCalls, bool CheckRan, bool CheckFixed, bool Failed);

/// <summary>Per-million-token prices (USD). Local servers are free unless you set a price on the server.</summary>
public static class Pricing
{
    private static readonly (string prefix, decimal input, decimal output)[] Claude =
    [
        ("claude-fable-5-1", 10m, 50m), ("claude-mythos-5-1", 10m, 50m), ("claude-fable-5", 10m, 50m),
        ("claude-opus-5-5", 4m, 20m), ("claude-opus-5", 5m, 25m), ("claude-opus-4-8", 5m, 25m), ("claude-opus-4-7", 5m, 25m),
        ("claude-opus-4-6", 5m, 25m), ("claude-sonnet-5-5", 2m, 10m), ("claude-sonnet-5", 2m, 10m), ("claude-sonnet-4-6", 3m, 15m),
        ("claude-haiku-5-5", 0.10m, 0.50m), ("claude-haiku-4-5", 1m, 5m),
    ];

    /// <summary>Estimated cost, or null when unknown (or included in a subscription).</summary>
    public static (decimal? cost, bool subscription) Estimate(ProviderProfile p, string model, int input, int output)
    {
        if (p.InputPricePerMillion is { } pin && p.OutputPricePerMillion is { } pout)
            return ((input * pin + output * pout) / 1_000_000m, false);
        switch (p.Kind)
        {
            case ProviderKind.ClaudeCode:
                return (null, true); // billed by the Claude subscription, not per token
            case ProviderKind.Anthropic:
                var m = model.ToLowerInvariant();
                var price = Claude.FirstOrDefault(c => m.StartsWith(c.prefix));
                return price.prefix is null ? (null, false) : ((input * price.input + output * price.output) / 1_000_000m, false);
            default:
                return (0m, false); // your own hardware
        }
    }
}

/// <summary>What a model call is for (set around a run so every call inside it is attributed).</summary>
public sealed record UsageScope(string Source, string? BotId = null, string? ConversationId = null)
{
    private static readonly AsyncLocal<UsageScope?> Current = new();
    public static UsageScope? Active => Current.Value;

    public static IDisposable Begin(string source, string? botId = null, string? conversationId = null)
    {
        var previous = Current.Value;
        Current.Value = new UsageScope(source, botId, conversationId);
        return new Restore(previous);
    }

    private sealed class Restore(UsageScope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Usage log in its own SQLite file (usage.db).</summary>
public sealed class UsageStore
{
    private readonly string _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    public UsageStore(IAppPaths paths)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.DataDirectory, "usage.db"), Pooling = true }.ToString();
    }

    public event Action? Recorded;

    private async Task<SqliteConnection> OpenAsync()
    {
        var c = new SqliteConnection(_connection);
        await c.OpenAsync();
        if (!_ready)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS usage(time TEXT NOT NULL, provider TEXT, kind TEXT, model TEXT, bot TEXT, conversation TEXT,
                    source TEXT, input INTEGER, output INTEGER, ms INTEGER, cost REAL, subscription INTEGER);
                CREATE INDEX IF NOT EXISTS ix_usage_time ON usage(time);
                CREATE TABLE IF NOT EXISTS replies(time TEXT NOT NULL, provider TEXT, model TEXT, bot TEXT, conversation TEXT, source TEXT,
                    total_ms INTEGER, first_ms INTEGER, rounds INTEGER, tools INTEGER, check_ran INTEGER, check_fixed INTEGER, failed INTEGER);
                CREATE INDEX IF NOT EXISTS ix_replies_time ON replies(time);
                """;
            await cmd.ExecuteNonQueryAsync();
            _ready = true;
        }
        return c;
    }

    public async Task RecordAsync(UsageRecord r)
    {
        try
        {
            await _lock.WaitAsync();
            try
            {
                await using var c = await OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO usage VALUES ($t, $p, $k, $m, $b, $c, $s, $i, $o, $ms, $cost, $sub)";
                cmd.Parameters.AddWithValue("$t", r.Time.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$p", r.Provider);
                cmd.Parameters.AddWithValue("$k", r.ProviderKind);
                cmd.Parameters.AddWithValue("$m", r.Model);
                cmd.Parameters.AddWithValue("$b", (object?)r.BotId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)r.ConversationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", r.Source);
                cmd.Parameters.AddWithValue("$i", r.InputTokens);
                cmd.Parameters.AddWithValue("$o", r.OutputTokens);
                cmd.Parameters.AddWithValue("$ms", r.Milliseconds);
                cmd.Parameters.AddWithValue("$cost", r.Cost is { } cost ? (double)cost : DBNull.Value);
                cmd.Parameters.AddWithValue("$sub", r.Subscription ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
            Recorded?.Invoke();
        }
        catch { /* usage logging must never break a reply */ }
    }

    public event Action? ReplyRecorded;

    public async Task RecordReplyAsync(ReplyRecord r)
    {
        try
        {
            await _lock.WaitAsync();
            try
            {
                await using var c = await OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO replies VALUES ($t, $p, $m, $b, $c, $s, $total, $first, $rounds, $tools, $ran, $fixed, $failed)";
                cmd.Parameters.AddWithValue("$t", r.Time.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$p", r.Provider);
                cmd.Parameters.AddWithValue("$m", r.Model);
                cmd.Parameters.AddWithValue("$b", (object?)r.BotId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)r.ConversationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", r.Source);
                cmd.Parameters.AddWithValue("$total", r.TotalMs);
                cmd.Parameters.AddWithValue("$first", r.FirstTokenMs is { } f ? f : DBNull.Value);
                cmd.Parameters.AddWithValue("$rounds", r.Rounds);
                cmd.Parameters.AddWithValue("$tools", r.ToolCalls);
                cmd.Parameters.AddWithValue("$ran", r.CheckRan ? 1 : 0);
                cmd.Parameters.AddWithValue("$fixed", r.CheckFixed ? 1 : 0);
                cmd.Parameters.AddWithValue("$failed", r.Failed ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _lock.Release(); }
            ReplyRecorded?.Invoke();
        }
        catch { /* never break a reply */ }
    }

    public async Task<List<ReplyRecord>> RepliesSinceAsync(DateTimeOffset since)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, provider, model, bot, conversation, source, total_ms, first_ms, rounds, tools, check_ran, check_fixed, failed FROM replies WHERE time >= $t ORDER BY time";
        cmd.Parameters.AddWithValue("$t", since.UtcDateTime.ToString("o"));
        var list = new List<ReplyRecord>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new ReplyRecord(DateTimeOffset.Parse(rd.GetString(0)).ToLocalTime(), rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4), rd.GetString(5), rd.GetInt64(6),
                rd.IsDBNull(7) ? null : rd.GetInt64(7), rd.GetInt32(8), rd.GetInt32(9), rd.GetInt32(10) == 1, rd.GetInt32(11) == 1, rd.GetInt32(12) == 1));
        return list;
    }

    public async Task<List<UsageRecord>> SinceAsync(DateTimeOffset since)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, provider, kind, model, bot, conversation, source, input, output, ms, cost, subscription FROM usage WHERE time >= $t ORDER BY time";
        cmd.Parameters.AddWithValue("$t", since.UtcDateTime.ToString("o"));
        var list = new List<UsageRecord>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new UsageRecord(DateTimeOffset.Parse(rd.GetString(0)).ToLocalTime(), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4), rd.IsDBNull(5) ? null : rd.GetString(5), rd.GetString(6),
                rd.GetInt32(7), rd.GetInt32(8), rd.GetInt64(9), rd.IsDBNull(10) ? null : (decimal)rd.GetDouble(10), rd.GetInt32(11) == 1));
        return list;
    }
}

/// <summary>Wraps a model client so every chat call is logged with its tokens, time and cost.</summary>
public sealed class MeteredLlmClient(ILlmClient inner, ProviderProfile profile, UsageStore store) : ILlmClient
{
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => inner.ListModelsAsync(ct);
    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) => inner.EmbedAsync(model, inputs, ct);
    public Task<ModelCapabilities?> GetCapabilitiesAsync(string model, CancellationToken ct = default) => inner.GetCapabilitiesAsync(model, ct);

    public async IAsyncEnumerable<LlmChunk> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        LlmStats? stats = null;
        var outputChars = 0;
        var gotAny = false;
        try
        {
            await foreach (var chunk in inner.StreamChatAsync(request, ct))
            {
                gotAny = true;
                if (chunk.Stats is not null) stats = chunk.Stats;
                outputChars += chunk.Content?.Length ?? 0;
                yield return chunk;
            }
        }
        finally
        {
            if (gotAny)
            {
                var scope = UsageScope.Active;
                // Servers that don't report tokens: estimate (~4 characters per token).
                var input = stats?.PromptTokens ?? request.Messages.Sum(m => m.Content.Length) / 4;
                var output = stats?.CompletionTokens ?? outputChars / 4;
                var (cost, subscription) = Pricing.Estimate(profile, request.Model, input, output);
                _ = store.RecordAsync(new UsageRecord(DateTimeOffset.Now, profile.Name, profile.Kind.ToString(), request.Model,
                    scope?.BotId, scope?.ConversationId, scope?.Source ?? "Other", input, output, sw.ElapsedMilliseconds, cost, subscription));
            }
        }
    }
}
