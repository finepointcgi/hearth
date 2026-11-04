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

    /// <summary>Checks a few times a day; proposes a cleanup once 15+ facts were added since the last one.</summary>
    public void EnsureStarted() => _timer ??= new Timer(_ => _ = AutoAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));

    private async Task AutoAsync()
    {
        try
        {
            if (!(await settingsStore.GetAsync()).MemoryEnabled || await PendingAsync() is not null) return;
            var count = (await memories.ListAsync()).Count;
            if (count - (await ProfileAsync()).FactsAtLastCleanup >= 15) await ProposeAsync();
        }
        catch { /* best effort */ }
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
