using Locus.Core.Models;

namespace Locus.Core.Services;

public sealed class BotStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Bot>? _cache;
    private string FilePath => Path.Combine(paths.DataDirectory, "bots.json");

    public event Action? Changed;

    public async Task<List<Bot>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).ToList(); }
        finally { _lock.Release(); }
    }

    public async Task<Bot?> GetAsync(string id) => (await ListAsync()).FirstOrDefault(b => b.Id == id);

    public async Task<List<Bot>> GetManyAsync(IEnumerable<string> ids)
    {
        var all = await ListAsync();
        return ids.Select(id => all.FirstOrDefault(b => b.Id == id)).OfType<Bot>().ToList();
    }

    public async Task SaveAsync(Bot bot)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            var i = list.FindIndex(b => b.Id == bot.Id);
            if (i >= 0) list[i] = bot;
            else
            {
                if (list.Count >= Bot.MaxBots) throw new InvalidOperationException($"You can have up to {Bot.MaxBots} bots.");
                list.Add(bot);
            }
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            list.RemoveAll(b => b.Id == id);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task<(Bot bot, Routine routine)?> FindRoutineAsync(string routineId)
    {
        foreach (var b in await ListAsync())
            if (b.Routines.FirstOrDefault(r => r.Id == routineId) is { } r) return (b, r);
        return null;
    }

    /// <summary>Records a routine run without clobbering concurrent edits to the rest of the bot.</summary>
    public async Task MarkRoutineRunAsync(string botId, string routineId, DateTimeOffset when)
    {
        await _lock.WaitAsync();
        try
        {
            var r = (await LoadAsync()).FirstOrDefault(b => b.Id == botId)?.Routines.FirstOrDefault(x => x.Id == routineId);
            if (r is null) return;
            r.LastRun = when;
            await Json.WriteAtomicAsync(FilePath, _cache);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<Bot>> LoadAsync() => _cache ??= await Json.ReadAsync<List<Bot>>(FilePath) ?? [];
}
