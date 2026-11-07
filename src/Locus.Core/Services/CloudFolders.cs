using Locus.Core.Services.Google;
using Locus.Core.Services.Microsoft;

namespace Locus.Core.Services;

/// <summary>A Google Drive or OneDrive folder whose files are kept indexed in the document library.</summary>
public sealed class CloudFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>"google" or "onedrive".</summary>
    public string Provider { get; set; } = "google";
    /// <summary>The folder's id on that service.</summary>
    public string FolderId { get; set; } = "";
    /// <summary>What to call it: the folder's name or path.</summary>
    public string Name { get; set; } = "";
    public string? Link { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastSync { get; set; }
    public int Files { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }

    /// <summary>The library's folder id for documents from here.</summary>
    public string LibraryId => "cloud-" + Id;
    public string ProviderName => Provider == "onedrive" ? "OneDrive" : "Google Drive";
}

/// <summary>A file in a cloud folder (as listed), with what's needed to tell whether it changed.</summary>
public sealed record CloudFile(string Id, string Name, string Path, string? Mime, DateTime ModifiedUtc, long Size, string? Link);

/// <summary>
/// Keeps Google Drive and OneDrive folders (a Job Search or Recipes folder…) indexed in the document library, so bots
/// answer from the user's own files with search_documents instead of fetching them each time. Checked every hour
/// and on demand; only new and changed files are downloaded.
/// </summary>
public sealed class CloudFolders(IAppPaths paths, DocumentStore docs, IServiceProvider services) : IDisposable
{
    public const int MaxFiles = 1000;
    public const long MaxFileBytes = 15 * 1024 * 1024;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private Timer? _timer;

    private string FilePath => Path.Combine(paths.DataDirectory, "documents", "cloud-folders.json");

    public event Action? Changed;
    public string? Status { get; private set; }

    public async Task<List<CloudFolder>> ListAsync() => await Json.ReadAsync<List<CloudFolder>>(FilePath) ?? [];

    public void EnsureStarted()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => _ = SyncAllAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
    }

    private T Get<T>() where T : notnull => (T)(services.GetService(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} isn't available."));

    /// <summary>Links a folder: a Google Drive folder link or name, or a OneDrive path like Documents/Job Search.
    /// Returns an error message, or null.</summary>
    public async Task<string?> AddAsync(string provider, string folder, CancellationToken ct = default)
    {
        folder = folder.Trim();
        if (folder.Length == 0) return "Enter a folder.";
        try
        {
            var (id, name, link) = provider == "onedrive"
                ? await Get<GraphClient>().FindFolderAsync(folder, ct)
                : await Get<DriveClient>().FindFolderAsync(folder, ct);
            var list = await ListAsync();
            if (list.Any(f => f.Provider == provider && f.FolderId == id)) return $"{name} is already linked.";
            var linked = new CloudFolder { Provider = provider, FolderId = id, Name = name, Link = link };
            await UpdateAsync(l => l.Add(linked));
            _ = Task.Run(() => SyncAsync(linked.Id));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
    }

    public async Task RemoveAsync(string id)
    {
        var folder = (await ListAsync()).FirstOrDefault(f => f.Id == id);
        await UpdateAsync(l => l.RemoveAll(f => f.Id == id));
        if (folder is not null) await docs.RemoveWhereAsync(d => d.FolderId == folder.LibraryId);
    }

    public async Task SyncAllAsync()
    {
        foreach (var f in await ListAsync()) await SyncAsync(f.Id);
    }

    /// <summary>Brings one folder up to date: new and changed files are downloaded and indexed, removed ones dropped.</summary>
    public async Task SyncAsync(string id, CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var folder = (await ListAsync()).FirstOrDefault(f => f.Id == id);
            if (folder is null || !folder.Enabled) return;
            Status = $"Listing {folder.Name}…";
            Changed?.Invoke();
            var google = folder.Provider != "onedrive";
            var all = google
                ? await Get<DriveClient>().ListFolderAsync(folder.FolderId, MaxFiles + 1, ct)
                : await Get<GraphClient>().ListFolderAsync(folder.FolderId, MaxFiles + 1, ct);
            var files = all.Where(f => Readable(f) && f.Size <= MaxFileBytes).ToList();
            var skipped = Math.Max(0, files.Count - MaxFiles);
            files = files.Take(MaxFiles).ToList();
            var source = (CloudFile f) => $"{folder.Provider}:{f.Id}";
            var present = files.Select(source).ToHashSet();

            var removed = await docs.RemoveWhereAsync(d => d.FolderId == folder.LibraryId && (d.Source is null || !present.Contains(d.Source)), notify: false);
            var known = (await docs.ListAsync()).Where(d => d.FolderId == folder.LibraryId && d.Source is not null)
                .GroupBy(d => d.Source!).ToDictionary(g => g.Key, g => g.First());
            var todo = files.Where(f => !known.TryGetValue(source(f), out var d) || d.SourceModifiedUtc != f.ModifiedUtc || d.SourceSize != f.Size).ToList();

            var embed = true;
            string? lastError = null;
            for (var i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = todo[i];
                Status = $"Indexing {f.Name} from {folder.ProviderName} ({i + 1}/{todo.Count})";
                if (i % 5 == 0) Changed?.Invoke();
                try
                {
                    var text = google
                        ? await Get<DriveClient>().TextOfAsync(f, ct)
                        : await Get<GraphClient>().TextOfAsync(f, ct);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var header = $"{f.Path} ({folder.ProviderName}{(f.Link is null ? "" : ", " + f.Link)})\n\n";
                    var info = await docs.AddAsync($"{f.Path} · {folder.ProviderName}", header + text, null, ct,
                        new DocumentSource(source(f), folder.LibraryId, f.ModifiedUtc, f.Size), embed, notify: false);
                    if (info.KeywordOnly) embed = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = $"{f.Name}: {ex.Message}";
                }
            }

            var count = (await docs.ListAsync()).Count(d => d.FolderId == folder.LibraryId);
            await SaveStateAsync(id, s =>
            {
                s.LastSync = DateTimeOffset.Now;
                s.Files = count;
                s.Skipped = skipped;
                s.Error = lastError;
            });
            if (todo.Count > 0 || removed > 0) docs.NotifyChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(id, f => f.Error = ex.Message);
        }
        finally
        {
            Status = null;
            _sync.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Files whose text can be read: Google Docs, Sheets and Slides, Word, PDF, text and the like.</summary>
    public static bool Readable(CloudFile f) =>
        f.Mime is DriveClient.GoogleDocMime or DriveClient.GoogleSheetMime or DriveClient.GoogleSlidesMime
        || f.Name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || TextExtractor.IsSupported(f.Name);

    private Task SaveStateAsync(string id, Action<CloudFolder> change) =>
        UpdateAsync(l => { if (l.Find(f => f.Id == id) is { } f) change(f); });

    private async Task UpdateAsync(Action<List<CloudFolder>> change)
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

    public void Dispose() => _timer?.Dispose();
}
