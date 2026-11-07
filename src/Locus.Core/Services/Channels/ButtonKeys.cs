using System.Security.Cryptography;

namespace Locus.Core.Services.Channels;

/// <summary>
/// The short keys behind chat-app buttons (Telegram allows only 64 bytes of button data): key → the chat and reply a
/// button acts on, plus the items of the last weekly recap. Kept in button-keys.json so buttons on old messages
/// still work after a restart. Keys older than 60 days are dropped.
/// </summary>
public sealed class ButtonKeys(string filePath)
{
    public sealed record Target(string Conv, string Msg, DateTimeOffset At);
    public sealed record RecapRef(string Conv, string Msg, int Index);

    private sealed class Saved
    {
        public Dictionary<string, Target> Keys { get; set; } = [];
        public List<RecapRef> Recap { get; set; } = [];
    }

    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);
    private const int MaxKeys = 20000;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Saved? _data;

    private async Task<Saved> LoadAsync()
    {
        if (_data is not null) return _data;
        try { _data = await Json.ReadAsync<Saved>(filePath) ?? new(); }
        catch { _data = new(); } // a damaged file only costs old buttons
        return _data;
    }

    private Task SaveAsync() => Json.WriteAtomicAsync(filePath, _data!);

    /// <summary>A new key for a reply's buttons.</summary>
    public async Task<string> NewAsync(string conv, string msg)
    {
        await _lock.WaitAsync();
        try
        {
            var d = await LoadAsync();
            var now = DateTimeOffset.Now;
            if (d.Keys.Count >= MaxKeys || d.Keys.Count % 200 == 0)
            {
                foreach (var old in d.Keys.Where(k => now - k.Value.At > KeepFor).Select(k => k.Key).ToList()) d.Keys.Remove(old);
                foreach (var old in d.Keys.OrderBy(k => k.Value.At).Take(Math.Max(0, d.Keys.Count - MaxKeys + 1)).Select(k => k.Key).ToList())
                    d.Keys.Remove(old);
            }
            string key;
            do key = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(); while (d.Keys.ContainsKey(key));
            d.Keys[key] = new Target(conv, msg, now);
            await SaveAsync();
            return key;
        }
        finally { _lock.Release(); }
    }

    public async Task<Target?> FindAsync(string key)
    {
        await _lock.WaitAsync();
        try { return (await LoadAsync()).Keys.GetValueOrDefault(key); }
        finally { _lock.Release(); }
    }

    public async Task SetRecapAsync(IEnumerable<RecapRef> items)
    {
        await _lock.WaitAsync();
        try { (await LoadAsync()).Recap = items.ToList(); await SaveAsync(); }
        finally { _lock.Release(); }
    }

    public async Task<RecapRef?> RecapAsync(int index)
    {
        await _lock.WaitAsync();
        try { var r = (await LoadAsync()).Recap; return index >= 0 && index < r.Count ? r[index] : null; }
        finally { _lock.Release(); }
    }
}
