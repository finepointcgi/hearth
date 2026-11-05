using System.Collections.Concurrent;

namespace Locus.Core.Services;

/// <summary>A folder on this computer whose files are kept indexed in the document library.</summary>
public sealed class LinkedFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastSync { get; set; }
    public int Files { get; set; }
    /// <summary>Files skipped because the folder has more than <see cref="LinkedFolders.MaxFiles"/>.</summary>
    public int Skipped { get; set; }
    public string? Error { get; set; }
    public bool KeywordOnly { get; set; }
    /// <summary>Bots may change files here and run commands in it (each change asks first and can be undone).</summary>
    public bool Editable { get; set; }
    /// <summary>Sandbox image for commands in this project (e.g. mcr.microsoft.com/dotnet/sdk:10.0); null = the default.</summary>
    public string? SandboxImage { get; set; }
}

/// <summary>
/// Links folders on this computer to the document library: every supported file is indexed, and changes are picked
/// up as they happen (a file watcher) and by a full check every 15 minutes, so search always reflects what's there.
/// Bots can also read the files themselves (read_linked_file).
/// </summary>
public sealed class LinkedFolders(IAppPaths paths, DocumentStore docs) : IDisposable
{
    public const int MaxFiles = 3000;
    public const long MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Folders that are never worth indexing (dependencies, build output, version control, caches).</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages", "__pycache__",
        "venv", "env", "Library", "DerivedData", "Pods", "coverage",
    };

    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();
    private Timer? _timer;

    private string FilePath => System.IO.Path.Combine(paths.DataDirectory, "documents", "folders.json");

    public event Action? Changed;
    /// <summary>What the sync is doing right now ("Indexing notes.md (12/40)"), for the page.</summary>
    public string? Status { get; private set; }

    public async Task<List<LinkedFolder>> ListAsync() => await Json.ReadAsync<List<LinkedFolder>>(FilePath) ?? [];

    public void EnsureStarted()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = SyncAllAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));
    }

    /// <summary>Links a folder and starts indexing it. Returns an error message, or null.</summary>
    public async Task<string?> AddAsync(string path)
    {
        path = Normalize(path);
        if (path.Length == 0) return "Enter a folder path.";
        if (!Directory.Exists(path)) return $"Can't find the folder {path} (or Locus isn't allowed to open it).";
        var root = System.IO.Path.GetPathRoot(path);
        if (path == root || path == Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            return "Pick a specific folder rather than your whole drive or home folder.";
        if (path.StartsWith(Normalize(paths.DataDirectory), StringComparison.Ordinal)) return "That's Locus's own data folder.";
        var list = await ListAsync();
        if (list.Any(f => Inside(path, f.Path) || Inside(f.Path, path))) return "That folder is already linked (or inside one that is).";
        var folder = new LinkedFolder { Path = path };
        await UpdateAsync(l => l.Add(folder));
        _ = Task.Run(() => SyncAsync(folder.Id));
        return null;
    }

    public Task SetEditableAsync(string id, bool editable) =>
        SaveStateAsync(id, f => f.Editable = editable);

    public Task SetSandboxImageAsync(string id, string? image) =>
        SaveStateAsync(id, f => f.SandboxImage = string.IsNullOrWhiteSpace(image) ? null : image.Trim());

    /// <summary>The editable linked folder a path is safely inside (see <see cref="FolderForAsync"/>), or null.</summary>
    public async Task<LinkedFolder?> EditableFolderForAsync(string path) =>
        await FolderForAsync(path) is { Editable: true } f ? f : null;

    public async Task RemoveAsync(string id)
    {
        await UpdateAsync(l => l.RemoveAll(f => f.Id == id));
        if (_watchers.TryRemove(id, out var w)) w.Dispose();
        await docs.RemoveWhereAsync(d => d.FolderId == id);
    }

    /// <summary>
    /// The linked folder a file is safely inside, or null: no "..", symlinks or hidden files/folders (.env, .git,
    /// .ssh…) on the way, so a bot can't use a link to read outside what you shared.
    /// </summary>
    public async Task<LinkedFolder?> FolderForAsync(string path)
    {
        var full = Normalize(path);
        var folder = (await ListAsync()).FirstOrDefault(f => f.Enabled && Inside(full, f.Path));
        if (folder is null) return null;
        var rel = System.IO.Path.GetRelativePath(folder.Path, full);
        if (rel != "." && rel.Split(System.IO.Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'))) return null;
        for (var p = full; p.Length > folder.Path.Length; p = System.IO.Path.GetDirectoryName(p)!)
        {
            FileSystemInfo node = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            if (node.Exists && node.LinkTarget is not null) return null;
        }
        return folder;
    }

    public async Task SyncAllAsync()
    {
        foreach (var f in await ListAsync()) await SyncAsync(f.Id);
    }

    /// <summary>Brings one folder's index up to date: new and changed files are indexed, deleted ones removed.</summary>
    public async Task SyncAsync(string folderId, CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var folder = (await ListAsync()).FirstOrDefault(f => f.Id == folderId);
            if (folder is null || !folder.Enabled) return;
            if (!Directory.Exists(folder.Path))
            {
                await SaveStateAsync(folderId, f => f.Error = "The folder is missing or Locus isn't allowed to open it.");
                return;
            }
            Watch(folder);

            var files = Scan(folder.Path).ToList();
            var skipped = Math.Max(0, files.Count - MaxFiles);
            files = files.Take(MaxFiles).ToList();
            var onDisk = files.ToDictionary(f => f.FullName, StringComparer.Ordinal);
            var indexed = (await docs.ListAsync()).Where(d => d.FolderId == folderId).ToList();

            // Gone from the folder (or too many files now): drop from the index.
            var removed = await docs.RemoveWhereAsync(d => d.FolderId == folderId && (d.Source is null || !onDisk.ContainsKey(d.Source)), notify: false);
            var known = indexed.Where(d => d.Source is not null).GroupBy(d => d.Source!).ToDictionary(g => g.Key, g => g.First());
            var todo = files.Where(f => !known.TryGetValue(f.FullName, out var d)
                || d.SourceModifiedUtc != f.LastWriteTimeUtc || d.SourceSize != f.Length).ToList();

            var embed = true;
            var keywordOnly = folder.KeywordOnly && todo.Count == 0;
            string? lastError = null;
            for (var i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = todo[i];
                Status = $"Indexing {f.Name} ({i + 1}/{todo.Count})";
                if (i % 10 == 0) Changed?.Invoke();
                try
                {
                    var data = await File.ReadAllBytesAsync(f.FullName, ct);
                    var text = TextExtractor.Extract(f.Name, data);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var info = await docs.AddAsync(f.Name, text, null, ct,
                        new DocumentSource(f.FullName, folderId, f.LastWriteTimeUtc, f.Length), embed, notify: false);
                    // Once the embedding model fails, don't keep waiting on it for every file in this pass.
                    if (info.KeywordOnly) { embed = false; keywordOnly = true; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = $"{f.Name}: {ex.Message}";
                }
            }

            var count = (await docs.ListAsync()).Count(d => d.FolderId == folderId);
            await SaveStateAsync(folderId, s =>
            {
                s.LastSync = DateTimeOffset.Now;
                s.Files = count;
                s.Skipped = skipped;
                s.Error = lastError;
                if (todo.Count > 0) s.KeywordOnly = keywordOnly;
            });
            if (todo.Count > 0 || removed > 0) docs.NotifyChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(folderId, f => f.Error = ex.Message);
        }
        finally
        {
            Status = null;
            _sync.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Supported files under a folder, skipping hidden folders, dependencies and build output.</summary>
    public static IEnumerable<FileInfo> Scan(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir, "*", options).ToList();
                files = Directory.EnumerateFiles(dir, "*", options).ToList();
            }
            catch (Exception) { continue; }
            foreach (var f in files.Order(StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(f);
                if (name.StartsWith('.') || !TextExtractor.IsSupported(name)) continue;
                FileInfo info;
                try { info = new FileInfo(f); } catch (Exception) { continue; }
                if (info.Length is > 0 and <= MaxFileBytes && info.LinkTarget is null) yield return info;
            }
            foreach (var d in subdirs.OrderDescending(StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(d);
                if (name.StartsWith('.') || SkipDirs.Contains(name) || new DirectoryInfo(d).LinkTarget is not null) continue;
                stack.Push(d);
            }
        }
    }

    private void Watch(LinkedFolder folder)
    {
        if (_watchers.ContainsKey(folder.Id)) return;
        try
        {
            var w = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void OnChange(object _, FileSystemEventArgs e) => Debounce(folder.Id);
            w.Created += OnChange; w.Changed += OnChange; w.Deleted += OnChange; w.Renamed += (s, e) => Debounce(folder.Id);
            w.EnableRaisingEvents = true;
            if (!_watchers.TryAdd(folder.Id, w)) w.Dispose();
        }
        catch (Exception) { /* the 15-minute check still catches changes */ }
    }

    /// <summary>Waits for a burst of changes (saving, git checkout…) to settle before syncing.</summary>
    private void Debounce(string folderId)
    {
        var cts = new CancellationTokenSource();
        if (_pending.TryGetValue(folderId, out var old)) old.Cancel();
        _pending[folderId] = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _pending.TryRemove(folderId, out _);
            _ = SyncAsync(folderId);
        }, TaskScheduler.Default);
    }

    private Task SaveStateAsync(string id, Action<LinkedFolder> change) =>
        UpdateAsync(l => { if (l.Find(f => f.Id == id) is { } f) change(f); });

    private async Task UpdateAsync(Action<List<LinkedFolder>> change)
    {
        await _fileLock.WaitAsync();
        try
        {
            var list = await ListAsync();
            change(list);
            await Json.WriteAtomicAsync(FilePath, list);
        }
        finally { _fileLock.Release(); }
        Changed?.Invoke();
    }

    public static string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return "";
        if (path == "~" || path.StartsWith("~/")) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    private static bool Inside(string path, string folder) =>
        path == folder || path.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var w in _watchers.Values) w.Dispose();
    }
}
