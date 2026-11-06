using System.Text;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>A suggested tidy-up of the memory list, waiting for the user's OK.</summary>
public sealed class MemoryCleanup
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string? Profile { get; set; }
    /// <summary>Several facts replaced by one.</summary>
    public List<MemoryMerge> Merges { get; set; } = [];
    /// <summary>Duplicates and facts that are no longer true.</summary>
    public List<MemoryDrop> Drops { get; set; } = [];
    public bool IsEmpty => Profile is null && Merges.Count == 0 && Drops.Count == 0;
}

public sealed record MemoryMerge(List<string> Ids, string Text);
public sealed record MemoryDrop(string Id, string Reason);

public sealed class UserProfile
{
    public string Text { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public int FactsAtLastCleanup { get; set; }
    /// <summary>The profile before the last automatic update, so it can be undone.</summary>
    public string? PreviousText { get; set; }
    /// <summary>When new memories were last folded into the profile.</summary>
    public DateTimeOffset? ConsolidatedAt { get; set; }
}

/// <summary>
/// Keeps memory useful as it grows (like Hermes's always-loaded user profile): writes a short profile of the user,
/// merges duplicate facts and drops outdated ones. Changes are proposed and applied only when the user approves.
/// </summary>
public sealed class MemoryCurator(MemoryStore memories, SettingsStore settingsStore, ILlmClientFactory factory, ModelPicker models,
    ActivityLog activity, IAppPaths paths) : IDisposable
{
    private string PendingPath => Path.Combine(paths.DataDirectory, "memory-cleanup.json");
    private Timer? _timer;
    private int _busy;

    public event Action? Changed;

    public Task<UserProfile> ProfileAsync() => memories.Provider.GetProfileAsync();
    public async Task<MemoryCleanup?> PendingAsync() => await Json.ReadAsync<MemoryCleanup>(PendingPath);

    public async Task SaveProfileAsync(string text)
    {
        var p = await ProfileAsync();
        p.Text = text.Trim();
        p.UpdatedAt = DateTimeOffset.Now;
        await memories.Provider.SaveProfileAsync(p);
        Changed?.Invoke();
    }

    /// <summary>Checks hourly: folds new memories into the profile overnight, and proposes a tidy-up once 15+ facts were
    /// added since the last one.</summary>
    public void EnsureStarted() => _timer ??= new Timer(_ => _ = AutoAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));

    private async Task AutoAsync()
    {
        try
        {
            if (!(await settingsStore.GetAsync()).MemoryEnabled) return;
            if (ConsolidationDue(await ProfileAsync(), await memories.ListAsync(), DateTimeOffset.Now)) await ConsolidateAsync();
            if (await PendingAsync() is not null) return;
            var count = (await memories.ListAsync()).Count;
            if (count - (await ProfileAsync()).FactsAtLastCleanup >= 15) await ProposeAsync();
        }
        catch { /* best effort */ }
    }

    /// <summary>Recent memories listed in every prompt beyond this many get folded in even if it's not night yet.</summary>
    public const int FoldWhenRecentReach = 40;

    /// <summary>
    /// Fold new memories into the profile: once a night (between 2 and 6 am) when there are any, or any time the recent
    /// list gets long, or when it's been two days.
    /// </summary>
    public static bool ConsolidationDue(UserProfile profile, IReadOnlyList<Memory> facts, DateTimeOffset now)
    {
        var fresh = facts.Count(f => !f.Archived);
        if (fresh == 0) return false;
        if (fresh >= FoldWhenRecentReach) return true;
        var last = profile.ConsolidatedAt;
        if (last is null || now - last.Value >= TimeSpan.FromHours(48)) return fresh >= 3 || last is null && fresh >= 10;
        return now.Hour is >= 2 and < 6 && now - last.Value >= TimeSpan.FromHours(20);
    }

    /// <summary>
    /// Long-term memory: rewrites the profile to include the memories added since the last time, and moves the ones it
    /// now covers to long-term storage (archived: still searchable, brought up when relevant, never deleted). The old
    /// profile is kept so the update can be undone.
    /// </summary>
    public async Task<(int folded, bool updated)> ConsolidateAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return (0, false);
        using var usageScope = UsageScope.Begin("Memory tidy-up");
        try
        {
            var facts = await memories.ListAsync();
            var fresh = facts.Where(f => !f.Archived).ToList();
            var profile = await ProfileAsync();
            if (fresh.Count == 0) return (0, false);
            var settings = await settingsStore.GetAsync();
            var model = await models.PickAsync(null) ?? throw new InvalidOperationException("No model is available.");
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    "You maintain an AI assistant's long-term profile of its user. Update the profile with the new memories: " +
                    "who they are, their work and goals, preferences (food, tools, how they like answers), key people, " +
                    "ongoing projects and constraints. Keep everything still true from the current profile, replace what newer " +
                    "memories contradict (newer date wins), keep exact names and numbers. At most 250 words, short plain " +
                    "sentences or bullets grouped by topic. Then list the ids of the new memories the profile now fully covers; " +
                    "leave out one-off details it doesn't cover. Reply in exactly this format:\n" +
                    "PROFILE:\n<text>\nFOLDED: id1, id2, …"),
                LlmMessage.User((profile.Text.Length > 0 ? $"Current profile:\n{profile.Text}\n\n" : "Current profile: (none yet)\n\n") +
                    "New memories (id | date | memory):\n" + string.Join("\n", fresh.Select(f => $"{f.Id} | {f.CreatedAt:yyyy-MM-dd} | {f.Text}"))),
            };
            // Background work: let chats go first on a local model server.
            ModelQueue.MarkBackground();
            await ModelQueue.WaitForChatsAsync(settings.ActiveProvider, ct);
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await foreach (var c in factory.CreateForChat(settings).StreamChatAsync(new LlmRequest(model, prompt, 0.2, 1200, 16384, false), cts.Token))
                sb.Append(c.Content);

            var (text, folded) = ParseConsolidation(sb.ToString(), fresh.Select(f => f.Id).ToHashSet());
            if (text is null) return (0, false);
            var updated = text != profile.Text;
            if (updated)
            {
                profile.PreviousText = profile.Text;
                profile.Text = text;
                profile.UpdatedAt = DateTimeOffset.Now;
            }
            profile.ConsolidatedAt = DateTimeOffset.Now;
            await memories.Provider.SaveProfileAsync(profile);
            await memories.SetArchivedAsync(folded, true);
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, "Memory: profile updated",
                $"{folded.Count} memories folded into your profile and moved to long-term storage."));
            Changed?.Invoke();
            return (folded.Count, updated);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>The new profile (null if the reply had none) and the ids of memories it covers.</summary>
    public static (string? profile, List<string> folded) ParseConsolidation(string reply, IReadOnlySet<string> freshIds)
    {
        var text = reply.Replace("\r", "");
        var p = text.IndexOf("PROFILE:", StringComparison.OrdinalIgnoreCase);
        var f = text.LastIndexOf("FOLDED:", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return (null, []);
        var profile = (f > p ? text[(p + 8)..f] : text[(p + 8)..]).Trim();
        if (profile.Length < 20) return (null, []);
        if (profile.Length > 2500) profile = profile[..2500];
        var folded = f < 0 ? [] : text[(f + 7)..].Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.Trim('`', '"', '.', '*')).Where(freshIds.Contains).Distinct().ToList();
        return (profile, folded);
    }

    /// <summary>Puts back the profile from before the last automatic update.</summary>
    public async Task UndoProfileAsync()
    {
        var p = await ProfileAsync();
        if (p.PreviousText is null) return;
        (p.Text, p.PreviousText) = (p.PreviousText, null);
        p.UpdatedAt = DateTimeOffset.Now;
        await memories.Provider.SaveProfileAsync(p);
        Changed?.Invoke();
    }

    /// <summary>Asks the model for a tidy-up and stores it for review. Returns null when nothing needs changing.</summary>
    public async Task<MemoryCleanup?> ProposeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return await PendingAsync();
        using var usageScope = UsageScope.Begin("Memory tidy-up");
        try
        {
            var facts = await memories.ListAsync();
            var profile = await ProfileAsync();
            if (facts.Count == 0 && profile.Text.Length == 0) return null;
            var settings = await settingsStore.GetAsync();
            var model = await models.PickAsync(null) ?? throw new InvalidOperationException("No model is available.");
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    "You curate an AI assistant's long-term memory about its user. Do three things:\n" +
                    "1. PROFILE: write a short profile of the user (max ~150 words, plain sentences): who they are, what they work on, " +
                    "how they like answers, key people and ongoing projects. Only use the facts given.\n" +
                    "2. MERGE: where several facts say the same thing or belong together, give their ids and one combined fact.\n" +
                    "3. DROP: ids of exact duplicates, or facts clearly replaced by a newer one (newer = later date), with a short reason.\n" +
                    "Don't drop facts just because they seem minor. Reply in exactly this format:\n" +
                    "PROFILE:\n<text>\nMERGE: id1, id2 => combined fact\n(one MERGE line each, or none)\nDROP: id => reason\n(one DROP line each, or none)"),
                LlmMessage.User((profile.Text.Length > 0 ? $"Current profile:\n{profile.Text}\n\n" : "") +
                    "Facts (id | date | fact):\n" + string.Join("\n", facts.Select(f => $"{f.Id} | {f.CreatedAt:yyyy-MM-dd} | {f.Text}"))),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            await foreach (var c in factory.CreateForChat(settings).StreamChatAsync(new LlmRequest(model, prompt, 0.2, 1500, 16384, false), cts.Token))
                sb.Append(c.Content);
            var cleanup = Parse(sb.ToString(), facts.Select(f => f.Id).ToHashSet());
            if (cleanup.Profile is { } p && p == profile.Text) cleanup.Profile = null;
            if (cleanup.IsEmpty) return null;
            await Json.WriteAtomicAsync(PendingPath, cleanup);
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, "Memory tidy-up ready to review",
                $"{cleanup.Merges.Count} merges, {cleanup.Drops.Count} removals{(cleanup.Profile is null ? "" : ", updated profile")}"));
            Changed?.Invoke();
            return cleanup;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public static MemoryCleanup Parse(string text, IReadOnlySet<string> validIds)
    {
        var result = new MemoryCleanup();
        var lines = text.Replace("\r", "").Split('\n');
        var profile = new List<string>();
        var inProfile = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('*', '-', ' ');
            if (line.StartsWith("PROFILE:", StringComparison.OrdinalIgnoreCase))
            {
                inProfile = true;
                if (line[8..].Trim() is { Length: > 0 } rest) profile.Add(rest);
                continue;
            }
            if (line.StartsWith("MERGE:", StringComparison.OrdinalIgnoreCase))
            {
                inProfile = false;
                var parts = line[6..].Split("=>", 2);
                if (parts.Length < 2) continue;
                var ids = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(validIds.Contains).Distinct().ToList();
                var merged = parts[1].Trim();
                if (ids.Count >= 2 && merged.Length > 3 && ids.All(id => !result.Merges.Any(m => m.Ids.Contains(id))))
                    result.Merges.Add(new MemoryMerge(ids, merged));
                continue;
            }
            if (line.StartsWith("DROP:", StringComparison.OrdinalIgnoreCase))
            {
                inProfile = false;
                var parts = line[5..].Split("=>", 2);
                var id = parts[0].Trim();
                if (validIds.Contains(id) && result.Drops.All(d => d.Id != id) && result.Merges.All(m => !m.Ids.Contains(id)))
                    result.Drops.Add(new MemoryDrop(id, parts.Length > 1 ? parts[1].Trim() : "duplicate or outdated"));
                continue;
            }
            if (inProfile && line.Length > 0 && !line.Equals("none", StringComparison.OrdinalIgnoreCase)) profile.Add(raw.Trim());
        }
        var p = string.Join("\n", profile).Trim();
        if (p.Length > 20) result.Profile = p.Length > 1500 ? p[..1500] : p;
        return result;
    }

    /// <summary>Applies the pending tidy-up (the parts the user kept).</summary>
    public async Task ApplyAsync(MemoryCleanup cleanup)
    {
        foreach (var m in cleanup.Merges)
        {
            await memories.UpdateAsync(m.Ids[0], m.Text);
            foreach (var id in m.Ids.Skip(1)) await memories.DeleteAsync(id);
        }
        foreach (var d in cleanup.Drops) await memories.DeleteAsync(d.Id);
        var profile = await ProfileAsync();
        if (cleanup.Profile is not null) { profile.Text = cleanup.Profile; profile.UpdatedAt = DateTimeOffset.Now; }
        profile.FactsAtLastCleanup = (await memories.ListAsync()).Count;
        await memories.Provider.SaveProfileAsync(profile);
        await DiscardAsync();
    }

    public async Task DiscardAsync()
    {
        if (File.Exists(PendingPath)) File.Delete(PendingPath);
        var profile = await ProfileAsync();
        profile.FactsAtLastCleanup = (await memories.ListAsync()).Count;
        await memories.Provider.SaveProfileAsync(profile);
        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();
}
