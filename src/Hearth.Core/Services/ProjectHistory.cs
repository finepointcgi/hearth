namespace Hearth.Core.Services;

/// <summary>One change a bot made to a file in a project folder, with what was there before (for undo).</summary>
public sealed record ProjectChange(string Id, string FolderId, string Path, bool Existed, DateTimeOffset At, string Summary,
    string? ConversationId, string? BotId);

/// <summary>
/// Checkpoints for bots' edits to project folders: before each change the old file is copied aside, so any change
/// can be undone (newest first, per file). Keeps the last 500 changes.
/// </summary>
public sealed class ProjectHistory(IAppPaths paths)
{
    public const int Keep = 500;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string Dir => System.IO.Path.Combine(paths.DataDirectory, "project-history");
    private string IndexPath => System.IO.Path.Combine(Dir, "index.json");
    private string Backup(string id) => System.IO.Path.Combine(Dir, id + ".bak");

    public event Action? Changed;

    public async Task<List<ProjectChange>> ListAsync() => await Json.ReadAsync<List<ProjectChange>>(IndexPath) ?? [];

    /// <summary>Saves the file's current state; call right before changing it.</summary>
    public async Task<ProjectChange> RecordAsync(string folderId, string path, string summary, string? conversationId, string? botId)
    {
        var change = new ProjectChange(Guid.NewGuid().ToString("N")[..10], folderId, path, File.Exists(path), DateTimeOffset.Now,
            summary, conversationId, botId);
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Dir);
            if (change.Existed) File.Copy(path, Backup(change.Id), overwrite: true);
            var list = await ListAsync();
            list.Add(change);
            foreach (var old in list.Take(Math.Max(0, list.Count - Keep)).ToList())
            {
                list.Remove(old);
                try { File.Delete(Backup(old.Id)); } catch { }
            }
            await Json.WriteAtomicAsync(IndexPath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        return change;
    }

    /// <summary>Puts a file back the way it was before a change. Returns an error, or null.</summary>
    public async Task<string?> UndoAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var list = await ListAsync();
            var change = list.FirstOrDefault(c => c.Id == id);
            if (change is null) return "That change isn't in the history any more.";
            if (list.Any(c => c.Path == change.Path && c.At > change.At))
                return $"{System.IO.Path.GetFileName(change.Path)} was changed again after that; undo the newer change first.";
            if (change.Existed)
            {
                if (!File.Exists(Backup(id))) return "The saved copy is missing.";
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(change.Path)!);
                File.Copy(Backup(id), change.Path, overwrite: true);
            }
            else if (File.Exists(change.Path)) File.Delete(change.Path); // the bot created it
            list.Remove(change);
            try { File.Delete(Backup(id)); } catch { }
            await Json.WriteAtomicAsync(IndexPath, list);
        }
        finally { _lock.Release(); }
        Changed?.Invoke();
        return null;
    }
}
