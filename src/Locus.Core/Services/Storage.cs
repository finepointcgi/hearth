using System.Text.Json;
using System.Text.Json.Serialization;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>Each host supplies where app data lives (MAUI: FileSystem.AppDataDirectory, desktop/web: user profile).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
}

public sealed class DefaultAppPaths : IAppPaths
{
    /// <summary>LOCUS_DATA_DIR overrides the location (e.g. a Docker volume at /data).</summary>
    public string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("LOCUS_DATA_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Locus");
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

public sealed class SettingsStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings? _cached;
    private string FilePath => Path.Combine(paths.DataDirectory, "settings.json");

    public event Action? Changed;

    public async Task<AppSettings> GetAsync()
    {
        if (_cached is not null) return _cached;
        await _lock.WaitAsync();
        try { return _cached ??= await Json.ReadAsync<AppSettings>(FilePath) ?? new AppSettings(); }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _lock.WaitAsync();
        try
        {
            _cached = settings;
            await Json.WriteAtomicAsync(FilePath, settings);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
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
            if (c is not null) list.Add(new(c.Id, c.Title, c.UpdatedAt, c.Pinned, c.BotIds, c.Unread));
        }
        return list.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt).ToList();
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
            .Where(c => c.BotIds.Count == 1 && c.BotIds[0] == bot.Id)
            .OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (existing is not null && await GetAsync(existing.Id) is { } conv) return conv;
        conv = new Conversation { Title = bot.Name, BotIds = [bot.Id] };
        await SaveAsync(conv);
        return conv;
    }

    public async Task DeleteAsync(string id)
    {
        var f = FileFor(id);
        if (File.Exists(f)) File.Delete(f);
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
