using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Each host supplies where app data lives (MAUI: FileSystem.AppDataDirectory, desktop/web: user profile).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
}

public sealed class DefaultAppPaths : IAppPaths
{
    /// <summary>HEARTH_DATA_DIR overrides the location (e.g. a Docker volume at /data).</summary>
    public string DataDirectory { get; } =
        LegacyNames.Env("DATA_DIR") is { } dir ? dir
        : LegacyNames.DataFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create));
}

/// <summary>
/// Hearth was called Locus before. An install from then keeps working: its data folder and database are moved to
/// the new names on first start, and the old environment variables and settings keys are still read.
/// </summary>
public static class LegacyNames
{
    public const string Name = "Hearth", OldName = "Locus";

    /// <summary>HEARTH_<paramref name="name"/>, or the old LOCUS_<paramref name="name"/>.</summary>
    public static string? Env(string name) =>
        Environment.GetEnvironmentVariable("HEARTH_" + name) is { Length: > 0 } v ? v
        : Environment.GetEnvironmentVariable(OldName.ToUpperInvariant() + "_" + name) is { Length: > 0 } old ? old : null;

    /// <summary>The data folder under <paramref name="parent"/>, moved over from the old name if that's all there is.</summary>
    public static string DataFolder(string parent)
    {
        var dir = Path.Combine(parent, Name);
        var old = Path.Combine(parent, OldName);
        if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
        try { Directory.Move(old, dir); return dir; }
        catch { return old; } // in use or not allowed: keep using it where it is
    }

    /// <summary>The memory database's path, renamed from the old file name (with its journal files) if needed.</summary>
    public static string Database(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "hearth.db");
        var old = Path.Combine(dataDirectory, OldName.ToLowerInvariant() + ".db");
        if (File.Exists(path) || !File.Exists(old)) return path;
        try
        {
            foreach (var suffix in new[] { "-wal", "-shm", "" }) // the main file last: it decides which name is used
                if (File.Exists(old + suffix)) File.Move(old + suffix, path + suffix);
            return path;
        }
        catch { return File.Exists(path) ? path : old; }
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, value, Options);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteTextAtomicAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options);
        }
        catch (JsonException) { return default; }
    }
}

/// <summary>
/// App settings in settings.json. Secrets inside them (model-server API keys, MCP environment values and headers) are
/// encrypted on disk with the local vault key and decrypted in memory.
/// </summary>
public sealed class SettingsStore(IAppPaths paths)
{
    private const string SealedPrefix = "sealed:";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SecretBox _box = new(paths);
    private AppSettings? _cached;
    private string FilePath => Path.Combine(paths.DataDirectory, "settings.json");

    public event Action? Changed;

    public async Task<AppSettings> GetAsync()
    {
        if (_cached is not null) return _cached;
        await _lock.WaitAsync();
        try
        {
            if (_cached is not null) return _cached;
            if (!File.Exists(FilePath)) return _cached = new AppSettings();
            var node = JsonNode.Parse(await File.ReadAllTextAsync(FilePath)) as JsonObject ?? [];
            var hadPlaintext = Unseal(node);
            _cached = node.Deserialize<AppSettings>(Json.Options) ?? new AppSettings();
            if (hadPlaintext) await WriteAsync(_cached); // encrypt secrets saved by older versions
            return _cached;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _lock.WaitAsync();
        try
        {
            _cached = settings;
            await WriteAsync(settings);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    private async Task WriteAsync(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, Json.Options) as JsonObject ?? [];
        Seal(node);
        await Json.WriteAtomicAsync(FilePath, node);
    }

    /// <summary>Encrypts secrets in the JSON about to be written.</summary>
    private void Seal(JsonObject root)
    {
        root.Remove("ActiveProvider"); // computed; older versions wrote a duplicate (with its key) here
        foreach (var p in (root["Providers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (p["ApiKey"]?.GetValue<string>() is { Length: > 0 } key)
            {
                p["ApiKeySealed"] = _box.Seal(key);
                p.Remove("ApiKey");
            }
            if (p["ExtraApiKeys"] is JsonArray extra)
                for (var i = 0; i < extra.Count; i++)
                    if (extra[i]?.GetValue<string>() is { Length: > 0 } x && !x.StartsWith(SealedPrefix))
                        extra[i] = SealedPrefix + _box.Seal(x);
        }
        foreach (var m in (root["McpServers"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var section in new[] { "Environment", "Headers" })
                if (m[section] is JsonObject values)
                    foreach (var k in values.Select(kv => kv.Key).ToList())
                        if (values[k]?.GetValue<string>() is { Length: > 0 } v && !v.StartsWith(SealedPrefix))
                            values[k] = SealedPrefix + _box.Seal(v);
        if (root["Notifications"] is JsonObject n && n["Token"]?.GetValue<string>() is { Length: > 0 } t && !t.StartsWith(SealedPrefix))
            n["Token"] = SealedPrefix + _box.Seal(t);
        if (root["Api"] is JsonObject api && api["Key"]?.GetValue<string>() is { Length: > 0 } apiKey && !apiKey.StartsWith(SealedPrefix))
            api["Key"] = SealedPrefix + _box.Seal(apiKey);
        foreach (var w in (root["Webhooks"] as JsonArray ?? []).OfType<JsonObject>())
            if (w["Secret"]?.GetValue<string>() is { Length: > 0 } secret && !secret.StartsWith(SealedPrefix))
                w["Secret"] = SealedPrefix + _box.Seal(secret);
    }

    /// <summary>Decrypts secrets after reading. Returns true when the file still had plaintext secrets.</summary>
    private bool Unseal(JsonObject root)
    {
        var plaintext = root.Remove("ActiveProvider"); // duplicate copy written by older versions
        foreach (var p in (root["Providers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (p["ApiKeySealed"]?.GetValue<string>() is { Length: > 0 } sealedKey)
            {
                try { p["ApiKey"] = _box.Open(sealedKey); }
                catch { p["ApiKey"] = null; } // vault key lost: the user re-enters it
                p.Remove("ApiKeySealed");
            }
            else if (p["ApiKey"]?.GetValue<string>() is { Length: > 0 }) plaintext = true;
            if (p["ExtraApiKeys"] is JsonArray extra)
                for (var i = 0; i < extra.Count; i++)
                    if (extra[i]?.GetValue<string>() is { Length: > 0 } x)
                    {
                        if (!x.StartsWith(SealedPrefix)) { plaintext = true; continue; }
                        try { extra[i] = _box.Open(x[SealedPrefix.Length..]); } catch { extra[i] = ""; }
                    }
        }
        foreach (var m in (root["McpServers"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var section in new[] { "Environment", "Headers" })
                if (m[section] is JsonObject values)
                    foreach (var k in values.Select(kv => kv.Key).ToList())
                        if (values[k]?.GetValue<string>() is { Length: > 0 } v)
                        {
                            if (!v.StartsWith(SealedPrefix)) { plaintext = true; continue; }
                            try { values[k] = _box.Open(v[SealedPrefix.Length..]); }
                            catch { values[k] = ""; }
                        }
        if (root["Notifications"] is JsonObject n && n["Token"]?.GetValue<string>() is { Length: > 0 } t)
        {
            if (!t.StartsWith(SealedPrefix)) plaintext = true;
            else try { n["Token"] = _box.Open(t[SealedPrefix.Length..]); } catch { n["Token"] = null; }
        }
        if (root["Api"] is JsonObject api && api["Key"]?.GetValue<string>() is { Length: > 0 } apiKey)
        {
            if (!apiKey.StartsWith(SealedPrefix)) plaintext = true;
            else try { api["Key"] = _box.Open(apiKey[SealedPrefix.Length..]); } catch { api["Key"] = null; }
        }
        foreach (var w in (root["Webhooks"] as JsonArray ?? []).OfType<JsonObject>())
            if (w["Secret"]?.GetValue<string>() is { Length: > 0 } secret)
            {
                if (!secret.StartsWith(SealedPrefix)) plaintext = true;
                else try { w["Secret"] = _box.Open(secret[SealedPrefix.Length..]); } catch { w["Secret"] = null; }
            }
        return plaintext;
    }
}

public sealed class ConversationStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string Dir => Path.Combine(paths.DataDirectory, "conversations");
    private string FileFor(string id) => Path.Combine(Dir, Path.GetFileName(id) + ".json");

    public event Action? Changed;
    /// <summary>Raised with the saved instance, so an open chat can pick up messages written elsewhere.</summary>
    public event Action<Conversation>? Saved;

    public async Task<List<ConversationSummary>> ListAsync()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<ConversationSummary>();
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            var c = await Json.ReadAsync<Conversation>(file);
            if (c is not null && c.HandoffFrom is null) list.Add(new(c.Id, DisplayTitle(c), c.UpdatedAt, c.Pinned, c.BotIds, c.Unread, c.Topic, c.Code?.Name, c.CreatedAt));
        }
        return list.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt).ToList();
    }

    /// <summary>Untitled chats show their first message instead of "New chat".</summary>
    public static string DisplayTitle(Conversation c)
    {
        if (c.Title != "New chat") return c.Title;
        var first = c.Messages.FirstOrDefault(m => m.Role == ChatRole.User && m.Content.Trim().Length > 0)?.Content.Trim();
        if (first is null) return c.Title;
        first = first.ReplaceLineEndings(" ");
        return first.Length > 48 ? first[..48].TrimEnd() + "…" : first;
    }

    public Task<Conversation?> GetAsync(string id) => Json.ReadAsync<Conversation>(FileFor(id));

    /// <summary>Every saved chat with its file's last-write time (cheap: no file is read).</summary>
    public List<(string id, DateTime modified)> Files() =>
        !Directory.Exists(Dir) ? []
        : Directory.EnumerateFiles(Dir, "*.json").Select(f => (Path.GetFileNameWithoutExtension(f), File.GetLastWriteTimeUtc(f))).ToList();

    public DateTime FileTime(string id) => File.Exists(FileFor(id)) ? File.GetLastWriteTimeUtc(FileFor(id)) : DateTime.UtcNow;

    public async Task SaveAsync(Conversation conversation, bool notify = true)
    {
        await _lock.WaitAsync();
        try { await Json.WriteAtomicAsync(FileFor(conversation.Id), conversation); }
        finally { _lock.Release(); }
        Saved?.Invoke(conversation);
        if (notify) Changed?.Invoke();
    }

    /// <summary>The bot's main 1:1 chat (most recent), created on first use.</summary>
    public async Task<Conversation> GetOrCreateDirectAsync(Bot bot)
    {
        var existing = (await ListAsync())
            .Where(c => c.BotIds.Count == 1 && c.BotIds[0] == bot.Id && c.Project is null) // code sessions live in the Code tab
            .OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (existing is not null && await GetAsync(existing.Id) is { } conv) return conv;
        conv = new Conversation { Title = bot.Name, BotIds = [bot.Id] };
        await SaveAsync(conv);
        return conv;
    }

    /// <summary>
    /// The behind-the-scenes chat <paramref name="bot"/> uses when pulled into <paramref name="from"/> (one per bot per
    /// chat, so it remembers earlier hand-offs there). Not listed in the sidebar; deleted with that chat.
    /// </summary>
    public async Task<Conversation> GetOrCreateHandoffAsync(Conversation from, Bot bot)
    {
        var id = HandoffId(from.Id, bot.Id);
        if (await GetAsync(id) is { } conv) return conv;
        conv = new Conversation { Id = id, Title = $"{bot.Name} (pulled in)", BotIds = [bot.Id], HandoffFrom = from.Id };
        await SaveAsync(conv, notify: false);
        return conv;
    }

    private static string HandoffId(string chatId, string botId) => $"{chatId}-handoff-{botId}";

    public async Task DeleteAsync(string id)
    {
        var f = FileFor(id);
        if (File.Exists(f)) File.Delete(f);
        if (Directory.Exists(Dir))
            foreach (var h in Directory.EnumerateFiles(Dir, Path.GetFileName(id) + "-handoff-*.json")) File.Delete(h);
        Changed?.Invoke();
        await Task.CompletedTask;
    }

    public async Task DeleteAllAsync()
    {
        if (Directory.Exists(Dir))
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json")) File.Delete(f);
        Changed?.Invoke();
        await Task.CompletedTask;
    }
}
