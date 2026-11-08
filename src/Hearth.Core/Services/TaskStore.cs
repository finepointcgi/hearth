using Hearth.Core.Models;

namespace Hearth.Core.Services;

public sealed class TaskStore(IAppPaths paths)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string Dir => Path.Combine(paths.DataDirectory, "tasks");
    private string FileFor(string id) => Path.Combine(Dir, Path.GetFileName(id) + ".json");

    public event Action<AgentTask>? Saved;

    public async Task<List<AgentTask>> ListAsync()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<AgentTask>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            if (await Json.ReadAsync<AgentTask>(f) is { } t) list.Add(t);
        return list.OrderByDescending(t => t.UpdatedAt).ToList();
    }

    public Task<AgentTask?> GetAsync(string id) => Json.ReadAsync<AgentTask>(FileFor(id));

    public async Task SaveAsync(AgentTask task)
    {
        task.UpdatedAt = DateTimeOffset.Now;
        await _lock.WaitAsync();
        try { await Json.WriteAtomicAsync(FileFor(task.Id), task); }
        finally { _lock.Release(); }
        Saved?.Invoke(task);
    }

    public async Task DeleteAsync(string id)
    {
        var f = FileFor(id);
        if (File.Exists(f)) File.Delete(f);
        await Task.CompletedTask;
    }
}
