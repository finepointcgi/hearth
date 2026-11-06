using System.Collections.Concurrent;
using Locus.Core.Models;
using Microsoft.Data.Sqlite;

namespace Locus.Core.Services;

/// <summary>
/// The built-in memory: one SQLite database (locus.db) for every bot, with a scope column per bot. Full-text search
/// (FTS5) for keywords plus embeddings for recall by meaning, fused with reciprocal rank fusion.
/// </summary>
public sealed class SqliteMemoryProvider : IMemoryProvider, IDisposable
{
    private readonly IAppPaths _paths;
    private readonly IEmbedder _embedder;
    private readonly SettingsStore? _settings;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Dictionary<string, float[]>> _factVectors = new(); // scope → id → vec
    private Dictionary<(string chat, int idx), float[]>? _chatVectors;
    private readonly ConcurrentDictionary<string, float[]> _queryCache = new();
    private bool _ready;
    private Timer? _embedTimer;
    private readonly SemaphoreSlim _embedGate = new(1, 1);

    public SqliteMemoryProvider(IAppPaths paths, IEmbedder embedder, SettingsStore? settings = null)
    {
        _paths = paths;
        _embedder = embedder;
        _settings = settings;
        DatabasePath = Path.Combine(paths.DataDirectory, "locus.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = true }.ToString();
    }

    public string Name => "Built-in (SQLite)";
    public string DatabasePath { get; }

    private static string S(string? scope) => scope ?? "";

    private async Task<SqliteConnection> OpenAsync()
    {
        if (!_ready) await InitAsync();
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync();
        return c;
    }

    private async Task InitAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_ready) return;
            Directory.CreateDirectory(_paths.DataDirectory);
            await using var c = new SqliteConnection(_connectionString);
            await c.OpenAsync();
            await ExecAsync(c, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS memories(id TEXT PRIMARY KEY, scope TEXT NOT NULL, text TEXT NOT NULL, created TEXT NOT NULL, vec BLOB, vec_model TEXT);
                CREATE INDEX IF NOT EXISTS ix_memories_scope ON memories(scope);
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(text, id UNINDEXED, scope UNINDEXED, tokenize='porter unicode61');
                CREATE TABLE IF NOT EXISTS profile(id INTEGER PRIMARY KEY CHECK (id = 1), text TEXT NOT NULL, updated TEXT, facts_at_cleanup INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY, title TEXT NOT NULL, bot_ids TEXT NOT NULL, file_time TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS chat_messages(chat_id TEXT NOT NULL, idx INTEGER NOT NULL, created TEXT NOT NULL, speaker TEXT NOT NULL, text TEXT NOT NULL, vec BLOB, vec_model TEXT, PRIMARY KEY (chat_id, idx));
                CREATE VIRTUAL TABLE IF NOT EXISTS chat_fts USING fts5(text, chat_id UNINDEXED, idx UNINDEXED, tokenize='porter unicode61');
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
                """);
            await AddColumnAsync(c, "memories", "archived", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnAsync(c, "profile", "prev_text", "TEXT");
            await AddColumnAsync(c, "profile", "consolidated", "TEXT");
            await ImportJsonOnceAsync(c);
            _ready = true;
            _embedTimer = new Timer(_ => _ = EmbedBacklogAsync(wait: false), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        }
        finally { _initLock.Release(); }
    }

    /// <summary>Adds a column to a table from an older version of Locus (no-op when it's there).</summary>
    private static async Task AddColumnAsync(SqliteConnection c, string table, string column, string type)
    {
        await using (var info = Cmd(c, $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $n", ("$n", column)))
            if (await info.ExecuteScalarAsync() is not null) return;
        await ExecAsync(c, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    // ------------------------------------------------------------------ one-time import of the old JSON files

    private async Task ImportJsonOnceAsync(SqliteConnection c)
    {
        await using (var check = Cmd(c, "SELECT value FROM meta WHERE key='imported_json'"))
            if (await check.ExecuteScalarAsync() is not null) return;

        var files = new List<(string path, string scope)> { (Path.Combine(_paths.DataDirectory, "memories.json"), "") };
        var botsDir = Path.Combine(_paths.DataDirectory, "bots");
        if (Directory.Exists(botsDir))
            files.AddRange(Directory.EnumerateDirectories(botsDir).Select(d => (Path.Combine(d, "memories.json"), Path.GetFileName(d))));
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        foreach (var (path, scope) in files.Where(f => File.Exists(f.path)))
        {
            foreach (var m in await Json.ReadAsync<List<Memory>>(path) ?? [])
                await InsertMemoryAsync(c, tx, m.Id, scope, m.Text, m.CreatedAt);
            File.Move(path, path + ".before-sqlite.bak", overwrite: true);
        }
        var profilePath = Path.Combine(_paths.DataDirectory, "profile.json");
        if (File.Exists(profilePath) && await Json.ReadAsync<UserProfile>(profilePath) is { } p)
        {
            await using (var cmd = Cmd(c, "INSERT OR REPLACE INTO profile(id, text, updated, facts_at_cleanup) VALUES (1, $t, $u, $f)",
                             ("$t", p.Text), ("$u", p.UpdatedAt.ToString("o")), ("$f", p.FactsAtLastCleanup)))
            { cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(); }
            File.Move(profilePath, profilePath + ".before-sqlite.bak", overwrite: true);
        }
        await using (var done = Cmd(c, "INSERT INTO meta(key, value) VALUES ('imported_json', $v)", ("$v", DateTimeOffset.Now.ToString("o"))))
        { done.Transaction = tx; await done.ExecuteNonQueryAsync(); }
        await tx.CommitAsync();
    }

    private static async Task InsertMemoryAsync(SqliteConnection c, SqliteTransaction? tx, string id, string scope, string text, DateTimeOffset created)
    {
        await using var a = Cmd(c, "INSERT OR REPLACE INTO memories(id, scope, text, created) VALUES ($id, $s, $t, $c)",
            ("$id", id), ("$s", scope), ("$t", text), ("$c", created.ToString("o")));
        a.Transaction = tx;
        await a.ExecuteNonQueryAsync();
        await using var del = Cmd(c, "DELETE FROM memories_fts WHERE id = $id", ("$id", id));
        del.Transaction = tx;
        await del.ExecuteNonQueryAsync();
        await using var f = Cmd(c, "INSERT INTO memories_fts(text, id, scope) VALUES ($t, $id, $s)", ("$t", text), ("$id", id), ("$s", scope));
        f.Transaction = tx;
        await f.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ facts

    public async Task<List<Memory>> ListAsync(string? scope)
    {
        await using var c = await OpenAsync();
        await using var cmd = Cmd(c, "SELECT id, text, created, archived FROM memories WHERE scope = $s ORDER BY created, rowid", ("$s", S(scope)));
        var list = new List<Memory>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new Memory(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.GetInt64(3) != 0));
        return list;
    }

    public Task SetArchivedAsync(IReadOnlyCollection<string> ids, bool archived, string? scope) => WriteAsync(async c =>
    {
        foreach (var id in ids)
            await ExecAsync(c, "UPDATE memories SET archived = $a WHERE id = $id AND scope = $s", ("$a", archived ? 1 : 0), ("$id", id), ("$s", S(scope)));
    }, scope);

    public async Task<Memory> AddAsync(string text, string? scope)
    {
        var m = new Memory(Guid.NewGuid().ToString("N")[..6], text.Trim(), DateTimeOffset.Now);
        await WriteAsync(c => InsertMemoryAsync(c, null, m.Id, S(scope), m.Text, m.CreatedAt), scope);
        return m;
    }

    public async Task<bool> UpdateAsync(string id, string text, string? scope)
    {
        var changed = false;
        await WriteAsync(async c =>
        {
            await using var cmd = Cmd(c, "UPDATE memories SET text = $t, vec = NULL, vec_model = NULL WHERE id = $id AND scope = $s",
                ("$t", text.Trim()), ("$id", id), ("$s", S(scope)));
            changed = await cmd.ExecuteNonQueryAsync() > 0;
            if (!changed) return;
            await ExecAsync(c, "DELETE FROM memories_fts WHERE id = $id", ("$id", id));
            await ExecAsync(c, "INSERT INTO memories_fts(text, id, scope) VALUES ($t, $id, $s)", ("$t", text.Trim()), ("$id", id), ("$s", S(scope)));
        }, scope);
        return changed;
    }

    public async Task<bool> DeleteAsync(string id, string? scope)
    {
        var changed = false;
        await WriteAsync(async c =>
        {
            await using var cmd = Cmd(c, "DELETE FROM memories WHERE id = $id AND scope = $s", ("$id", id), ("$s", S(scope)));
            changed = await cmd.ExecuteNonQueryAsync() > 0;
            if (changed) await ExecAsync(c, "DELETE FROM memories_fts WHERE id = $id", ("$id", id));
        }, scope);
        return changed;
    }

    public Task ClearAsync(string? scope) => WriteAsync(async c =>
    {
        await ExecAsync(c, "DELETE FROM memories WHERE scope = $s", ("$s", S(scope)));
        await ExecAsync(c, "DELETE FROM memories_fts WHERE scope = $s", ("$s", S(scope)));
    }, scope);

    private async Task WriteAsync(Func<SqliteConnection, Task> work, string? scope)
    {
        await using var c = await OpenAsync();
        await _writeLock.WaitAsync();
        try { await work(c); }
        finally { _writeLock.Release(); }
        _factVectors.TryRemove(S(scope), out _);
        KickEmbedding();
    }

    public async Task<List<Memory>> SearchAsync(string query, string? scope, int max)
    {
        var all = (await ListAsync(scope)).ToDictionary(m => m.Id);
        if (all.Count == 0) return [];
        var keyword = new List<string>();
        if (FtsQuery(query) is { } q)
        {
            await using var c = await OpenAsync();
            await using var cmd = Cmd(c, "SELECT id FROM memories_fts WHERE memories_fts MATCH $q AND scope = $s ORDER BY bm25(memories_fts) LIMIT 50",
                ("$q", q), ("$s", S(scope)));
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) keyword.Add(r.GetString(0));
        }
        var semantic = new List<string>();
        if (await QueryVectorAsync(query) is { } qv)
        {
            var vectors = await FactVectorsAsync(scope, qv.model);
            semantic = Relevant(vectors.Select(kv => (kv.Key, Vectors.Dot(qv.vec, kv.Value))), 50);
        }
        return Fuse(keyword, semantic).Where(all.ContainsKey).Take(max).Select(id => all[id]).ToList();
    }

    /// <summary>
    /// Matches by meaning worth keeping. Similarity scales differ between models, so the cut-off is relative to the best
    /// match (60% of it) with a floor of 0.15.
    /// </summary>
    public static List<T> Relevant<T>(IEnumerable<(T item, float sim)> scored, int max)
    {
        var ranked = scored.OrderByDescending(x => x.sim).ToList();
        if (ranked.Count == 0) return [];
        var cutoff = Math.Max(0.15f, ranked[0].sim * 0.6f);
        return ranked.TakeWhile(x => x.sim >= cutoff).Take(max).Select(x => x.item).ToList();
    }

    /// <summary>Reciprocal rank fusion of the keyword and meaning rankings.</summary>
    public static List<T> Fuse<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        var score = new Dictionary<T, double>();
        for (var i = 0; i < a.Count; i++) score[a[i]] = score.GetValueOrDefault(a[i]) + 1.0 / (60 + i);
        for (var i = 0; i < b.Count; i++) score[b[i]] = score.GetValueOrDefault(b[i]) + 1.0 / (60 + i);
        return score.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>"newsletter pricing" → "newsletter" OR "pricing" (quoted, so user text can't inject FTS syntax).</summary>
    public static string? FtsQuery(string text)
    {
        var tokens = ChatHistoryIndex.Tokens(text).Select(t => t.Replace("\"", "")).Where(t => t.Length > 1).Distinct().Take(16).ToList();
        return tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    private async Task<bool> MeaningEnabledAsync() => _settings is null || (await _settings.GetAsync()).RecallByMeaning;

    private async Task<(float[] vec, string model)?> QueryVectorAsync(string query)
    {
        if (!await MeaningEnabledAsync() || await _embedder.ModelIdAsync() is not { } model) return null;
        var key = model + "\n" + query;
        if (_queryCache.TryGetValue(key, out var cached)) return (cached, model);
        try
        {
            var v = (await _embedder.EmbedAsync([query]))[0];
            if (_queryCache.Count > 200) _queryCache.Clear();
            _queryCache[key] = v;
            return (v, model);
        }
        catch { return null; }
    }

    private async Task<Dictionary<string, float[]>> FactVectorsAsync(string? scope, string model)
    {
        if (_factVectors.TryGetValue(S(scope), out var cached)) return cached;
        var map = new Dictionary<string, float[]>();
        await using var c = await OpenAsync();
        await using var cmd = Cmd(c, "SELECT id, vec FROM memories WHERE scope = $s AND vec IS NOT NULL AND vec_model = $m", ("$s", S(scope)), ("$m", model));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetString(0)] = Vectors.FromBytes((byte[])r[1]);
        _factVectors[S(scope)] = map;
        return map;
    }

    // ------------------------------------------------------------------ profile

    public async Task<UserProfile> GetProfileAsync()
    {
        await using var c = await OpenAsync();
        await using var cmd = Cmd(c, "SELECT text, updated, facts_at_cleanup, prev_text, consolidated FROM profile WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return new UserProfile();
        return new UserProfile
        {
            Text = r.GetString(0),
            UpdatedAt = r.IsDBNull(1) ? default : DateTimeOffset.Parse(r.GetString(1)),
            FactsAtLastCleanup = r.GetInt32(2),
            PreviousText = r.IsDBNull(3) ? null : r.GetString(3),
            ConsolidatedAt = r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
        };
    }

    public Task SaveProfileAsync(UserProfile p) => WriteAsync(c => ExecAsync(c,
        "INSERT OR REPLACE INTO profile(id, text, updated, facts_at_cleanup, prev_text, consolidated) VALUES (1, $t, $u, $f, $p, $c)",
        ("$t", p.Text), ("$u", p.UpdatedAt.ToString("o")), ("$f", p.FactsAtLastCleanup), ("$p", p.PreviousText),
        ("$c", p.ConsolidatedAt?.ToString("o"))), null);

    // ------------------------------------------------------------------ chat history

    public async Task IndexConversationAsync(Conversation conv, DateTime fileTime)
    {
        await using var c = await OpenAsync();
        await _writeLock.WaitAsync();
        try
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
            // Keep vectors of messages whose text didn't change, so re-saving a chat doesn't re-embed all of it.
            var old = new Dictionary<int, (string text, object vec, object model)>();
            await using (var sel = Cmd(c, "SELECT idx, text, vec, vec_model FROM chat_messages WHERE chat_id = $c", ("$c", conv.Id)))
            {
                sel.Transaction = tx;
                await using var r = await sel.ExecuteReaderAsync();
                while (await r.ReadAsync()) old[r.GetInt32(0)] = (r.GetString(1), r[2], r[3]);
            }
            foreach (var sql in new[] { "DELETE FROM chat_messages WHERE chat_id = $c", "DELETE FROM chat_fts WHERE chat_id = $c" })
                await using (var d = Cmd(c, sql, ("$c", conv.Id))) { d.Transaction = tx; await d.ExecuteNonQueryAsync(); }

            for (var i = 0; i < conv.Messages.Count; i++)
            {
                var m = conv.Messages[i];
                var text = m.Content;
                if (m.Role == ChatRole.User && !string.IsNullOrEmpty(m.AttachmentText))
                    text += "\n" + (m.AttachmentText.Length > 2000 ? m.AttachmentText[..2000] : m.AttachmentText);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var speaker = m.Role == ChatRole.User ? (m.FromBotId is null ? "User" : "Bot") : "Assistant";
                var keep = old.TryGetValue(i, out var o) && o.text == text;
                await using (var ins = Cmd(c, "INSERT INTO chat_messages(chat_id, idx, created, speaker, text, vec, vec_model) VALUES ($c, $i, $t, $s, $x, $v, $m)",
                                 ("$c", conv.Id), ("$i", i), ("$t", m.CreatedAt.ToString("o")), ("$s", speaker), ("$x", text),
                                 ("$v", keep ? o.vec : null), ("$m", keep ? o.model : null)))
                { ins.Transaction = tx; await ins.ExecuteNonQueryAsync(); }
                await using (var f = Cmd(c, "INSERT INTO chat_fts(text, chat_id, idx) VALUES ($x, $c, $i)", ("$x", text), ("$c", conv.Id), ("$i", i)))
                { f.Transaction = tx; await f.ExecuteNonQueryAsync(); }
            }
            await using (var up = Cmd(c, "INSERT OR REPLACE INTO chats(id, title, bot_ids, file_time) VALUES ($id, $t, $b, $f)",
                             ("$id", conv.Id), ("$t", conv.Title), ("$b", "," + string.Join(",", conv.BotIds) + ","), ("$f", fileTime.ToString("o"))))
            { up.Transaction = tx; await up.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
        }
        finally { _writeLock.Release(); }
        _chatVectors = null;
        KickEmbedding();
    }

    public async Task RemoveConversationAsync(string id)
    {
        await using var c = await OpenAsync();
        await _writeLock.WaitAsync();
        try
        {
            foreach (var sql in new[] { "DELETE FROM chat_messages WHERE chat_id = $c", "DELETE FROM chat_fts WHERE chat_id = $c", "DELETE FROM chats WHERE id = $c" })
                await ExecAsync(c, sql, ("$c", id));
        }
        finally { _writeLock.Release(); }
        _chatVectors = null;
    }

    public async Task<Dictionary<string, DateTime>> IndexedConversationsAsync()
    {
        await using var c = await OpenAsync();
        await using var cmd = Cmd(c, "SELECT id, file_time FROM chats");
        var map = new Dictionary<string, DateTime>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetString(0)] = DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return map;
    }

    public async Task<List<ChatHit>> SearchChatsAsync(string query, string? visibleTo, int limit, string? excludeConversation, int? withinDays)
    {
        await using var c = await OpenAsync();
        // Which chats may be searched, with their titles.
        var chats = new Dictionary<string, string>();
        await using (var cmd = Cmd(c, "SELECT id, title FROM chats WHERE ($b IS NULL OR bot_ids LIKE $b) AND ($x IS NULL OR id <> $x)",
                         ("$b", visibleTo is null ? null : $"%,{visibleTo},%"), ("$x", excludeConversation)))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) chats[r.GetString(0)] = r.GetString(1);
        if (chats.Count == 0) return [];
        var since = withinDays is { } d ? DateTimeOffset.Now.AddDays(-d) : (DateTimeOffset?)null;

        var keyword = new List<(string chat, int idx)>();
        if (FtsQuery(query) is { } q)
        {
            await using var cmd = Cmd(c, "SELECT chat_id, idx FROM chat_fts WHERE chat_fts MATCH $q ORDER BY bm25(chat_fts) LIMIT 400", ("$q", q));
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var key = (r.GetString(0), Convert.ToInt32(r.GetValue(1)));
                if (chats.ContainsKey(key.Item1)) keyword.Add(key);
            }
        }
        var semantic = new List<(string chat, int idx)>();
        if (await QueryVectorAsync(query) is { } qv)
            semantic = (await ChatVectorsAsync(c, qv.model))
                .Where(kv => chats.ContainsKey(kv.Key.chat))
                .Select(kv => (kv.Key, Vectors.Dot(qv.vec, kv.Value)))
                .Pipe(x => Relevant(x, 200));

        var hits = new List<ChatHit>();
        var tokens = ChatHistoryIndex.Tokens(query);
        var rank = 0;
        foreach (var key in Fuse(keyword, semantic))
        {
            rank++;
            if (hits.Any(h => h.ConversationId == key.chat)) continue; // best message per chat
            await using var cmd = Cmd(c, "SELECT created, speaker, text FROM chat_messages WHERE chat_id = $c AND idx = $i", ("$c", key.chat), ("$i", key.idx));
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) continue;
            var when = DateTimeOffset.Parse(r.GetString(0));
            if (since is not null && when < since) continue;
            hits.Add(new ChatHit(key.chat, chats[key.chat], when, key.idx, Math.Round(1.0 / rank, 3),
                $"{r.GetString(1)}: {ChatHistoryIndex.Snippet(r.GetString(2), tokens)}"));
            if (hits.Count >= limit) break;
        }
        return hits;
    }

    private async Task<Dictionary<(string chat, int idx), float[]>> ChatVectorsAsync(SqliteConnection c, string model)
    {
        if (_chatVectors is { } cached) return cached;
        var map = new Dictionary<(string, int), float[]>();
        await using var cmd = Cmd(c, "SELECT chat_id, idx, vec FROM chat_messages WHERE vec IS NOT NULL AND vec_model = $m", ("$m", model));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[(r.GetString(0), r.GetInt32(1))] = Vectors.FromBytes((byte[])r[2]);
        return _chatVectors = map;
    }

    // ------------------------------------------------------------------ background embedding

    private void KickEmbedding() => _ = Task.Run(() => EmbedBacklogAsync(wait: false));

    /// <summary>Embeds facts and chat messages that don't have a vector for the current model yet.</summary>
    /// <param name="wait">false: skip when a run is already going (timers and kicks); true: wait for it, then run.</param>
    public async Task<int> EmbedBacklogAsync(bool wait = true)
    {
        if (!await _embedGate.WaitAsync(wait ? Timeout.Infinite : 0)) return 0;
        var total = 0;
        try
        {
            if (!await MeaningEnabledAsync() || await _embedder.ModelIdAsync() is not { } model) return 0;
            await using var c = await OpenAsync();
            foreach (var (table, key) in new[] { ("memories", "id"), ("chat_messages", "chat_id || '|' || idx") })
                while (true)
                {
                    var batch = new List<(string key, string text)>();
                    await using (var cmd = Cmd(c, $"SELECT {key}, text FROM {table} WHERE vec IS NULL OR vec_model IS NOT $m LIMIT 32", ("$m", model)))
                    await using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) batch.Add((r.GetString(0), r.GetString(1)));
                    if (batch.Count == 0) break;
                    var vectors = await _embedder.EmbedAsync(batch.Select(b => b.text.Length > 2000 ? b.text[..2000] : b.text).ToList());
                    await _writeLock.WaitAsync();
                    try
                    {
                        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
                        for (var i = 0; i < batch.Count; i++)
                        {
                            await using var up = Cmd(c, $"UPDATE {table} SET vec = $v, vec_model = $m WHERE {key} = $k",
                                ("$v", Vectors.ToBytes(vectors[i])), ("$m", model), ("$k", batch[i].key));
                            up.Transaction = tx;
                            await up.ExecuteNonQueryAsync();
                        }
                        await tx.CommitAsync();
                    }
                    finally { _writeLock.Release(); }
                    total += batch.Count;
                    if (table == "memories") _factVectors.Clear(); else _chatVectors = null;
                }
        }
        catch { /* the embedder may be offline; try again on the next tick */ }
        finally { _embedGate.Release(); }
        return total;
    }

    public async Task<MemoryStats> StatsAsync()
    {
        await using var c = await OpenAsync();
        async Task<int> Count(string sql)
        {
            await using var cmd = Cmd(c, sql);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        var model = await MeaningEnabledAsync() ? await _embedder.ModelIdAsync() : null;
        return new MemoryStats(Name,
            await Count("SELECT COUNT(*) FROM memories"), await Count("SELECT COUNT(*) FROM memories WHERE vec IS NOT NULL"),
            await Count("SELECT COUNT(*) FROM chats"), await Count("SELECT COUNT(*) FROM chat_messages"),
            await Count("SELECT COUNT(*) FROM chat_messages WHERE vec IS NOT NULL"),
            !await MeaningEnabledAsync() ? "keywords only (recall by meaning is off)"
                : model is null ? "keywords only (no embedding model available)" : $"keywords + meaning ({_embedder.Description})",
            DatabasePath);
    }

    public void Dispose() => _embedTimer?.Dispose();
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
