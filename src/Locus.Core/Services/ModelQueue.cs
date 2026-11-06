using System.Collections.Concurrent;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Puts the chats you're waiting on ahead of background work (tasks, routines, heartbeat, the bot inbox) on a local
/// model server. A local server answers one request at a time or slows down with each extra one, so while a chat
/// reply is running, background runs hold off before their next model call. Hosted providers (Claude) are not queued.
/// </summary>
public static class ModelQueue
{
    private static readonly AsyncLocal<bool> Background = new();
    private static readonly ConcurrentDictionary<string, int> Chats = new();

    /// <summary>How often a waiting background run checks again.</summary>
    public static TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Background work waits at most this long per call, so a busy day of chatting can't starve it.</summary>
    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True inside a background run (and anything it starts, such as hand-offs to other bots).</summary>
    public static bool InBackground => Background.Value;

    /// <summary>Marks the current run and everything it starts as background work.</summary>
    public static void MarkBackground() => Background.Value = true;

    /// <summary>Only servers on this computer or the local network get queued; hosted APIs run requests side by side.</summary>
    public static bool IsQueued(ProviderProfile p) => p.Kind is not (ProviderKind.Anthropic or ProviderKind.ClaudeCode);

    /// <summary>A chat reply using <paramref name="p"/> is in progress until the result is disposed.</summary>
    public static IDisposable ChatRunning(ProviderProfile p)
    {
        if (!IsQueued(p)) return Nothing.Instance;
        Chats.AddOrUpdate(p.Id, 1, (_, n) => n + 1);
        return new Release(p.Id);
    }

    /// <summary>Number of chat replies using <paramref name="providerId"/> right now.</summary>
    public static int ChatsOn(string providerId) => Chats.GetValueOrDefault(providerId);

    /// <summary>
    /// For background work: waits while a chat is using the same server (up to <see cref="MaxWait"/>). Returns true if
    /// it had to wait. Chats never wait here.
    /// </summary>
    public static async Task<bool> WaitForChatsAsync(ProviderProfile p, CancellationToken ct)
    {
        if (!InBackground || !IsQueued(p) || ChatsOn(p.Id) == 0) return false;
        var until = DateTime.UtcNow + MaxWait;
        while (ChatsOn(p.Id) > 0 && DateTime.UtcNow < until)
            await Task.Delay(Poll, ct);
        return true;
    }

    private sealed class Release(string id) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Chats.AddOrUpdate(id, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
