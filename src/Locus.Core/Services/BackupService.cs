using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Locus.Core.Services;

public sealed class BackupSettings
{
    public bool Daily { get; set; } = true;
    /// <summary>Where daily copies go (default: a "backups" folder in the data folder). Point it at iCloud/Dropbox/NAS for off-machine copies.</summary>
    public string? Folder { get; set; }
    public int Keep { get; set; } = 7;
    public DateTimeOffset? LastBackup { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Lets the host restart Locus (the web host stops and its service manager starts it again).</summary>
public interface IAppRestarter
{
    bool CanRestart { get; }
    void Restart();
}

public sealed class NoRestarter : IAppRestarter
{
    public bool CanRestart => false;
    public void Restart() { }
}

/// <summary>
/// One-file backups of everything Locus keeps (bots, chats, memory, skills, settings and their encryption key), daily
/// copies, and restore. A restore is applied the next time Locus starts, after setting the current data aside.
/// </summary>
public sealed class BackupService(IAppPaths paths, SettingsStore settingsStore) : IDisposable
{
    public const string PendingRestore = "restore-pending.zip";
    private static readonly string[] SkipDirs = ["models", "backups", "browser", "browser-profiles", "playwright", "claude-code"];
    private Timer? _timer;

    public string DefaultFolder => Path.Combine(paths.DataDirectory, "backups");

    public void EnsureStarted() => _timer ??= new Timer(_ => _ = DailyAsync(), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(1));

    private async Task DailyAsync()
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            var b = settings.Backup;
            if (!b.Daily || b.LastBackup is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(23)) return;
            await BackUpToFolderAsync();
        }
        catch (Exception ex)
        {
            try
            {
                var s = await settingsStore.GetAsync();
                s.Backup.LastError = $"{DateTime.Now:MMM d, h:mm tt}: {ex.Message}";
                await settingsStore.SaveAsync(s);
            }
            catch { /* retried next hour */ }
        }
    }

    /// <summary>Writes a backup into the backup folder, prunes old ones, and returns its path.</summary>
    public async Task<string> BackUpToFolderAsync()
    {
        var settings = await settingsStore.GetAsync();
        var folder = string.IsNullOrWhiteSpace(settings.Backup.Folder) ? DefaultFolder : settings.Backup.Folder!;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"locus-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        var partial = path + ".partial";
        try
        {
            await using (var fs = File.Create(partial))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await WriteAsync(fs);
            }
            File.Move(partial, path, overwrite: true); // only complete backups get a real name
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
        foreach (var old in Directory.EnumerateFiles(folder, "locus-backup-*.zip").OrderByDescending(f => f).Skip(Math.Max(1, settings.Backup.Keep)))
            File.Delete(old);
        settings.Backup.LastBackup = DateTimeOffset.Now;
        settings.Backup.LastError = null;
        await settingsStore.SaveAsync(settings);
        return path;
    }

    /// <summary>Writes the backup zip to a stream.</summary>
    public async Task WriteAsync(Stream output)
    {
        var root = paths.DataDirectory;
        await CheckpointDatabasesAsync(root);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await using (var w = new StreamWriter(zip.CreateEntry("LOCUS-BACKUP.txt").Open()))
            await w.WriteAsync($"Locus backup {DateTimeOffset.Now:o}\nContains your settings, secrets and their key (vault.key). Keep it private.\n");
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var top = rel.Split('/')[0];
            if (SkipDirs.Contains(top) || rel == PendingRestore || rel.EndsWith("-wal") || rel.EndsWith("-shm") || rel.EndsWith(".tmp")
                || rel.EndsWith(".download") || rel.StartsWith("locus-before-restore")) continue;
            try
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                await using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var dst = entry.Open();
                await src.CopyToAsync(dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use, gone or unreadable: skip it */ }
        }
    }

    /// <summary>Folds SQLite write-ahead logs into the database files so the copy is complete.</summary>
    private static async Task CheckpointDatabasesAsync(string root)
    {
        foreach (var db in Directory.EnumerateFiles(root, "*.db"))
            try
            {
                await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* busy: the copy may miss the last seconds */ }
    }

    /// <summary>Checks an uploaded backup and stages it; it's applied when Locus next starts.</summary>
    public async Task<string?> StageRestoreAsync(Stream zipStream)
    {
        var staged = Path.Combine(paths.DataDirectory, PendingRestore);
        await using (var fs = File.Create(staged)) await zipStream.CopyToAsync(fs);
        try
        {
            using var zip = ZipFile.OpenRead(staged);
            if (zip.GetEntry("LOCUS-BACKUP.txt") is null && zip.GetEntry("settings.json") is null)
            {
                File.Delete(staged);
                return "That file isn't a Locus backup.";
            }
            if (zip.Entries.Any(e => e.FullName.Contains("..") || Path.IsPathRooted(e.FullName)))
            {
                File.Delete(staged);
                return "That backup contains unsafe paths.";
            }
        }
        catch (InvalidDataException)
        {
            File.Delete(staged);
            return "That file isn't a valid zip.";
        }
        return null;
    }

    public bool RestorePending => File.Exists(Path.Combine(paths.DataDirectory, PendingRestore));

    public void CancelRestore() => File.Delete(Path.Combine(paths.DataDirectory, PendingRestore));

    /// <summary>
    /// Call at startup before anything opens the data folder: if a restore is staged, moves the current data to
    /// "locus-before-restore-…" (inside the data folder) and unpacks the backup.
    /// </summary>
    public static string? ApplyPendingRestore(string dataDirectory)
    {
        var staged = Path.Combine(dataDirectory, PendingRestore);
        if (!File.Exists(staged)) return null;
        var aside = Path.Combine(dataDirectory, $"locus-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(aside);
        foreach (var entry in Directory.EnumerateFileSystemEntries(dataDirectory))
        {
            var name = Path.GetFileName(entry);
            if (name is PendingRestore || name.StartsWith("locus-before-restore") || name is "backups" or "models") continue;
            var target = Path.Combine(aside, name);
            if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target);
        }
        using (var zip = ZipFile.OpenRead(staged))
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName == "LOCUS-BACKUP.txt") continue;
                var dest = Path.GetFullPath(Path.Combine(dataDirectory, e.FullName));
                if (!dest.StartsWith(Path.GetFullPath(dataDirectory))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(dataDirectory, "vault.key")))
            File.SetUnixFileMode(Path.Combine(dataDirectory, "vault.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(staged);
        return aside;
    }

    public void Dispose() => _timer?.Dispose();
}
