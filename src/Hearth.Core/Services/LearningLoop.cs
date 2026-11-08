using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// The other half of teaching: showing what a reply drew on, fixing what made a reply wrong (👎), catching new
/// memories that contradict old ones, and offering to make requests that keep coming up into skills or routines.
/// </summary>
public sealed partial class LearningLoop(ILlmClientFactory factory, SettingsStore settingsStore, ConversationStore store, MemoryStore memories,
    SkillStore skills, PeopleStore people, Companion companion, BotStore bots, ChatHistoryIndex history)
{
    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    /// <summary>The bot that wrote a reply, and a client and model to think about it with.</summary>
    public async Task<(Bot? bot, ILlmClient client, string model, AppSettings settings)> ClientForAsync(Conversation conv, ChatMessage reply)
    {
        var settings = await settingsStore.GetAsync();
        var bot = reply.BotId is null ? null : await bots.GetAsync(reply.BotId);
        var client = factory.CreateForBot(settings, bot);
        var model = !string.IsNullOrWhiteSpace(bot?.Model) ? bot.Model
            : reply.Model ?? conv.Model ?? await ModelPicker.DefaultForAsync(factory, settings.ProviderFor(bot))
            ?? throw new InvalidOperationException("No model is available.");
        return (bot, client, model, settings);
    }

    private static async Task<string> AskAsync(ILlmClient client, string model, string system, string user, int maxTokens, TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sb = new StringBuilder();
        await foreach (var c in client.StreamChatAsync(new LlmRequest(model, [LlmMessage.System(system), LlmMessage.User(user)], 0.1, maxTokens, 8192, false), cts.Token))
            sb.Append(c.Content);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 1. what a reply drew on

    private static readonly HashSet<string> Plain =
    [
        "user", "users", "likes", "prefers", "wants", "would", "should", "their", "there", "about", "always", "never", "with", "from",
        "that", "this", "they", "when", "what", "have", "make", "more", "some", "very", "really", "into", "also", "only",
    ];

    private static readonly HashSet<string> NotNames =
    [
        "User", "The", "This", "That", "They", "He", "She", "It", "When", "Always", "Never", "Prefers", "Likes",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "January", "February", "March", "April",
        "May", "June", "July", "August", "September", "October", "November", "December", "AI",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'-]*")] private static partial Regex Word();

    /// <summary>
    /// Whether a fact visibly shows in a reply: a name from it appears (a capitalised word other than the first), or
    /// most of its meaningful words do.
    /// </summary>
    public static bool ShowsIn(string fact, string reply) => ShowsIn(fact, reply, ChatHistoryIndex.Tokens(reply).ToHashSet());

    private static bool ShowsIn(string fact, string reply, HashSet<string> replyWords)
    {
        var names = Word().Matches(fact).Skip(1).Select(m => m.Value.EndsWith("'s") ? m.Value[..^2] : m.Value)
            .Where(w => w.Length >= 3 && char.IsUpper(w[0]) && !NotNames.Contains(w)).Distinct();
        if (names.Any(n => Regex.IsMatch(reply, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(n)}(?![\p{{L}}\p{{N}}])"))) return true;
        var words = ChatHistoryIndex.Tokens(fact).Where(w => w.Length >= 4 && !Plain.Contains(w)).Distinct().ToList();
        if (words.Count == 0) return false;
        var hits = words.Count(replyWords.Contains);
        return hits >= 2 && hits * 2 >= words.Count;
    }

    /// <summary>The skills, memories, people and life threads that show in a reply (a handful at most).</summary>
    public async Task<List<LearnedItem>> UsedAsync(Bot? bot, AppSettings settings, ChatMessage reply, IReadOnlyList<Memory> recalled)
    {
        var used = new List<LearnedItem>();
        var text = reply.Content;
        if (text.Length < 20) return used;
        var words = ChatHistoryIndex.Tokens(text).ToHashSet();

        var allSkills = await skills.ListAsync();
        foreach (var call in reply.ToolCalls.Where(t => t is { Name: "use_skill", Status: ToolCallStatus.Done }))
        {
            string? name;
            try { name = JsonNode.Parse(call.ArgumentsJson)?["name"]?.ToString(); } catch { name = null; }
            if (name is not null && allSkills.FirstOrDefault(s => s.Name == Skill.Slug(name.TrimStart('/'))) is { } skill
                && used.All(u => u.Ref != skill.Id))
                used.Add(new LearnedItem { Kind = LearnedItem.UsedSkill, Text = "/" + skill.Name, Ref = skill.Id });
        }

        if (settings.MemoryEnabled)
        {
            var learnedNow = reply.LearnedItems.Select(i => i.Ref).ToHashSet();
            var max = Math.Max(5, bot?.Memory.MaxInPrompt ?? 60);
            var candidates = new List<(Memory m, string? scope)>();
            if (bot is null || bot.Memory.UseSharedMemory)
                candidates.AddRange(ChatService.RecentMemories(await memories.ListAsync(), max).Select(m => (m, (string?)null)));
            var own = bot is { Memory.Enabled: true } ? await memories.ListAsync(bot.Id) : [];
            candidates.AddRange(ChatService.RecentMemories(own, max).Select(m => (m, (string?)bot!.Id)));
            candidates.AddRange(recalled.Select(m => (m, own.Any(o => o.Id == m.Id) ? bot!.Id : (string?)null)));
            foreach (var (m, scope) in candidates.DistinctBy(c => c.m.Text))
            {
                if (learnedNow.Contains(m.Id) || !ShowsIn(m.Text, text, words)) continue;
                used.Add(new LearnedItem { Kind = LearnedItem.Memory, Text = m.Text, Ref = m.Id, Scope = scope });
                if (used.Count(u => u.Kind == LearnedItem.Memory) >= 4) break;
            }

            foreach (var t in await companion.ActiveAsync())
            {
                var titleWords = ChatHistoryIndex.Tokens(t.Title).Where(w => w.Length >= 3).ToList();
                if (titleWords.Count == 0 || !titleWords.All(words.Contains) && !ShowsIn(t.Title, text, words)) continue;
                used.Add(new LearnedItem { Kind = LearnedItem.Thread, Text = t.Title, Ref = t.Id });
                if (used.Count(u => u.Kind == LearnedItem.Thread) >= 2) break;
            }
        }

        var known = await people.ListAsync();
        var asked = reply.ToolCalls.Where(t => t.Name == "who_is").Select(t =>
        {
            try { return JsonNode.Parse(t.ArgumentsJson)?["name"]?.ToString(); } catch { return null; }
        }).OfType<string>().ToList();
        foreach (var e in known)
        {
            var hit = asked.Any(a => a.Equals(e.Name, StringComparison.OrdinalIgnoreCase))
                      || e.Name.Length >= 3 && Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(e.Name)}(?![\p{{L}}\p{{N}}])");
            if (!hit || e.Notes.Count == 0) continue;
            used.Add(new LearnedItem { Kind = LearnedItem.Person, Text = e.Name, Ref = e.Id });
            if (used.Count(u => u.Kind == LearnedItem.Person) >= 3) break;
        }
        return used;
    }

    // ------------------------------------------------------------------ 2. "that's wrong"

    /// <summary>
    /// 👎 on a reply: works out what made it wrong (a memory, a skill, or something it should have known) and fixes
    /// that, so the next reply gets it right. Each fix shows under the reply with Undo. Returns what was done.
    /// </summary>
    public async Task<string> CorrectAsync(Conversation conv, ChatMessage reply, string feedback, Func<Task> onUpdate, CancellationToken ct = default)
    {
        feedback = feedback.Trim();
        if (feedback.Length < 2) throw new InvalidOperationException("Say what was wrong.");
        var (bot, client, model, settings) = await ClientForAsync(conv, reply);
        using var usageScope = UsageScope.Begin("Corrections", bot?.Id, conv.Id);
        var at = conv.Messages.IndexOf(reply);
        var asked = (at < 0 ? conv.Messages : conv.Messages.Take(at)).LastOrDefault(m => m.Role == ChatRole.User);

        // Memories that may be behind it: the ones the reply drew on, and ones about what the feedback mentions.
        var scopes = new List<string?>();
        if (bot is null || bot.Memory.UseSharedMemory) scopes.Add(null);
        if (bot is { Memory.Enabled: true }) scopes.Add(bot.Id);
        var mems = new Dictionary<string, (string text, string? scope)>();
        foreach (var u in reply.Used.Where(u => u.Kind == LearnedItem.Memory && u.Ref is not null)) mems.TryAdd(u.Ref!, (u.Text, u.Scope));
        foreach (var scope in scopes)
        foreach (var m in await memories.SearchAsync(feedback + " " + (asked?.Content ?? ""), scope, 6))
            mems.TryAdd(m.Id, (m.Text, scope));

        var allSkills = await skills.ListAsync();
        var usedSkills = reply.Used.Where(u => u.Kind == LearnedItem.UsedSkill).Select(u => allSkills.FirstOrDefault(s => s.Id == u.Ref))
            .OfType<Skill>().ToList();

        var answer = await AskAsync(client, model,
            "An AI assistant gave a reply the user says is wrong. Fix the CAUSE so future replies get it right. Reply with one or more lines, nothing else:\n" +
            "FIX <memory id> | <corrected memory>   — a memory below is wrong or out of date\n" +
            "FORGET <memory id>   — a memory below is wrong and should go\n" +
            "SKILL <skill name> | <what to change in its steps>   — a skill below led it wrong\n" +
            "REMEMBER | <a short lasting rule or fact, e.g. \"Weekly reports go to Sarah, not Sam.\">   — something to keep in mind from now on\n" +
            "Use REMEMBER when no memory or skill below caused it. Skip anything that only applied to this one reply.",
            $"Memories (id: text):\n{(mems.Count == 0 ? "(none)" : string.Join("\n", mems.Select(m => $"- {m.Key}: {m.Value.text}")))}\n\n" +
            $"Skills it used:\n{(usedSkills.Count == 0 ? "(none)" : string.Join("\n\n", usedSkills.Select(s => $"/{s.Name}:\n{Clip(s.Instructions, 1500)}")))}\n\n" +
            $"The user asked:\n{Clip(asked?.Content ?? "", 1500)}\n\nThe reply:\n{Clip(reply.Content, 2000)}\n\nWhat the user says was wrong:\n{feedback}",
            600, TimeSpan.FromMinutes(3), ct);

        var memoryScope = bot is null || bot.Memory.UseSharedMemory ? null : bot.Id;
        var items = new List<LearnedItem>();
        foreach (var raw in answer.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', ' ');
            var bar = line.IndexOf('|');
            var head = (bar < 0 ? line : line[..bar]).Trim();
            var tail = bar < 0 ? "" : line[(bar + 1)..].Trim().Trim('"');
            var parts = head.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0].ToUpperInvariant())
            {
                case "FIX" when parts.Length == 2 && mems.TryGetValue(parts[1].Trim(), out var old) && tail.Length >= 4:
                    if (await memories.UpdateAsync(parts[1].Trim(), tail, old.scope))
                        items.Add(new LearnedItem { Kind = LearnedItem.FixedMemory, Text = tail, Ref = parts[1].Trim(), Scope = old.scope, Extra = old.text });
                    break;
                case "FORGET" when parts.Length == 2 && mems.TryGetValue(parts[1].Trim(), out var gone):
                    if (await memories.DeleteAsync(parts[1].Trim(), gone.scope))
                        items.Add(new LearnedItem { Kind = LearnedItem.ForgotMemory, Text = gone.text, Ref = parts[1].Trim(), Scope = gone.scope, Extra = gone.text });
                    break;
                case "SKILL" when parts.Length == 2 && tail.Length >= 4
                                  && allSkills.FirstOrDefault(s => s.Name == Skill.Slug(parts[1].Trim().TrimStart('/'))) is { } skill:
                    var rewritten = await AskAsync(client, model,
                        "You edit a skill: step-by-step instructions an AI assistant follows. Apply the change and reply with the complete new instructions only.",
                        $"Instructions:\n{skill.Instructions}\n\nChange:\n{tail}", 1500, TimeSpan.FromMinutes(3), ct);
                    if (rewritten.Trim().Length < 40 || rewritten.Trim() == skill.Instructions.Trim()) break;
                    await skills.ReviseAsync(skill, rewritten, null, "Corrected from a chat: " + Clip(feedback, 120), bot?.Name ?? "Hearth", pending: false);
                    items.Add(new LearnedItem { Kind = LearnedItem.ImprovedSkill, Text = "/" + skill.Name, Ref = skill.Id, Extra = skill.UpdatedAt.ToString("O") });
                    break;
                case "REMEMBER" when tail.Length is >= 4 and <= 300:
                    if (await memories.KnowsAsync(tail, memoryScope)) break;
                    var m = await memories.AddAsync(tail, memoryScope);
                    items.Add(new LearnedItem { Kind = LearnedItem.Memory, Text = tail, Ref = m.Id, Scope = memoryScope });
                    break;
            }
        }
        // The model couldn't place it: keep the user's own words as the lesson.
        if (items.Count == 0 && feedback.Length >= 8 && !await memories.KnowsAsync(feedback, memoryScope))
        {
            var text = Clip("Correction from the user: " + feedback, 300);
            var m = await memories.AddAsync(text, memoryScope);
            items.Add(new LearnedItem { Kind = LearnedItem.Memory, Text = text, Ref = m.Id, Scope = memoryScope });
        }

        // Skill health: a skill this reply followed that wasn't fixed just now collects the complaint.
        foreach (var used in usedSkills.Where(u => !items.Any(i => i.Kind == LearnedItem.ImprovedSkill && i.Ref == u.Id)))
            await skills.RecordWrongAsync(used.Id, feedback);

        reply.Feedback = feedback;
        lock (reply.LearnedItems) reply.LearnedItems.AddRange(items);
        await store.SaveAsync(conv, notify: false);
        await onUpdate();
        await CheckConflictsAsync(conv, reply, client, model, onUpdate, ct);
        return items.Count == 0 ? "Noted." : "Fixed: " + string.Join(" · ", items.Select(i => i.Label));
    }

    /// <summary>Skill health: rewrites a skill using what the user said was wrong with it (its 👎 notes).</summary>
    public async Task<string> FixSkillAsync(Skill skill, CancellationToken ct = default)
    {
        if (skill.WrongNotes.Count == 0) return "Nothing to fix: no complaints since it last changed.";
        var settings = await settingsStore.GetAsync();
        var client = factory.CreateForBot(settings, null);
        var model = await ModelPicker.DefaultForAsync(factory, settings.ProviderFor(null)) ?? throw new InvalidOperationException("No model is available.");
        using var usageScope = UsageScope.Begin("Corrections", null, null);
        var rewritten = await AskAsync(client, model,
            "You edit a skill: step-by-step instructions an AI assistant follows. Replies that followed it were marked wrong; change the " +
            "steps so those problems don't happen again, keep everything that still works, and reply with the complete new instructions only.",
            $"Instructions:\n{skill.Instructions}\n\nWhat the user said was wrong:\n{string.Join("\n", skill.WrongNotes.Select(n => "- " + n))}",
            1500, TimeSpan.FromMinutes(3), ct);
        if (rewritten.Trim().Length < 40) throw new InvalidOperationException("The model didn't return usable instructions. Try again, or edit it yourself.");
        var notes = skill.WrongNotes.Count;
        await skills.ReviseAsync(skill, rewritten, null, $"Fixed from {notes} 👎 note{(notes == 1 ? "" : "s")}", "Hearth", pending: false);
        return $"Rewrote /{skill.Name} from {notes} complaint{(notes == 1 ? "" : "s")}. ↶ undoes it.";
    }

    // ------------------------------------------------------------------ 3. contradictions

    /// <summary>
    /// Looks at the memories a reply just added for ones that contradict an older memory ("prefers long answers" vs
    /// "likes short answers"), and marks them so the user can say which is right.
    /// </summary>
    public async Task CheckConflictsAsync(Conversation conv, ChatMessage reply, ILlmClient client, string model, Func<Task> onUpdate,
        CancellationToken ct = default)
    {
        try
        {
            List<LearnedItem> fresh;
            lock (reply.LearnedItems) fresh = reply.LearnedItems.Where(i => i is { Kind: LearnedItem.Memory, Undone: false, ConflictChecked: false }).ToList();
            if (fresh.Count == 0) return;
            var mine = reply.LearnedItems.SelectMany(i => new[] { i.Ref, i.Extra }).OfType<string>().ToHashSet();
            var older = new Dictionary<string, (string text, string? scope)>();
            var prompt = new StringBuilder();
            for (var i = 0; i < fresh.Count; i++)
            {
                var item = fresh[i];
                var found = new List<(Memory m, string? scope)>();
                foreach (var scope in item.Scope is null ? new string?[] { null } : [item.Scope, null])
                    found.AddRange((await memories.SearchAsync(item.Text, scope, 5)).Select(m => (m, scope)));
                found = found.Where(f => !mine.Contains(f.m.Id)).DistinctBy(f => f.m.Id).ToList();
                if (found.Count == 0) continue;
                prompt.AppendLine($"N{i + 1}: {item.Text}");
                foreach (var (m, scope) in found)
                {
                    older.TryAdd(m.Id, (m.Text, scope));
                    prompt.AppendLine($"  - {m.Id}: {m.Text}");
                }
            }
            foreach (var item in fresh) item.ConflictChecked = true;
            if (older.Count == 0) return;

            var answer = await AskAsync(client, model,
                "You keep an AI assistant's memory consistent. Each new fact (N1, N2…) is listed with older memories that may relate. " +
                "A conflict means both can't be true now: a changed preference, a move, a new job, a different name or date. Being " +
                "related, more detailed or about a different thing is NOT a conflict. For each new fact that conflicts, reply with " +
                "a line \"N<number>: <older memory id>\". Reply NONE if nothing conflicts.",
                prompt.ToString(), 200, TimeSpan.FromSeconds(90), ct);
            var any = false;
            foreach (Match match in ConflictLine().Matches(answer))
            {
                var n = int.Parse(match.Groups[1].Value) - 1;
                var id = match.Groups[2].Value;
                if (n < 0 || n >= fresh.Count || !older.TryGetValue(id, out var old)) continue;
                fresh[n].ConflictRef = id;
                fresh[n].ConflictText = old.text;
                fresh[n].ConflictScope = old.scope;
                any = true;
            }
            await store.SaveAsync(conv, notify: false);
            if (any) await onUpdate();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* best effort */ }
    }

    [GeneratedRegex(@"N(\d+)\s*[:\-–>]+\s*([A-Za-z0-9_-]{3,})")] private static partial Regex ConflictLine();

    /// <summary>The user says which of two conflicting memories is right: "new", "old" or "both".</summary>
    public async Task<string> ResolveConflictAsync(Conversation conv, LearnedItem item, string keep)
    {
        if (item.ConflictRef is null || item.ConflictResolved is not null) return "Already settled.";
        // A reply sometimes saves the same news twice in different words: one answer settles them all.
        var same = conv.Messages.SelectMany(m => m.LearnedItems).Where(i => i != item && i.HasOpenConflict && i.ConflictRef == item.ConflictRef)
            .Prepend(item).ToList();
        string result;
        switch (keep)
        {
            case "new":
                await memories.DeleteAsync(item.ConflictRef, item.ConflictScope);
                result = $"Forgot \"{Clip(item.ConflictText ?? "", 60)}\".";
                break;
            case "old":
                foreach (var i in same)
                {
                    await memories.DeleteAsync(i.Ref ?? "", i.Scope);
                    if (i.Extra is { } copy) await memories.DeleteAsync(copy, null);
                    i.Undone = true;
                }
                result = "Kept what it knew before.";
                break;
            default:
                keep = "both";
                result = "Keeping both.";
                break;
        }
        foreach (var i in same) i.ConflictResolved = keep;
        await store.SaveAsync(conv, notify: false);
        return result;
    }

    /// <summary>The open contradictions in a reply, one per older memory (a fact saved twice asks once).</summary>
    public static List<LearnedItem> OpenConflicts(ChatMessage reply) =>
        reply.LearnedItems.Where(i => i.HasOpenConflict).DistinctBy(i => i.ConflictRef).ToList();

    // ------------------------------------------------------------------ 4. "you keep asking this"

    /// <summary>How many earlier chats must hold the same kind of request before offering (this one makes one more).</summary>
    public const int RepeatsBeforeOffer = 2;

    /// <summary>
    /// After a reply: if the user has asked for the same kind of job in other chats, offer (under the reply) to make it
    /// a skill, or a routine when it comes up on a rhythm.
    /// </summary>
    public async Task NudgeAsync(Conversation conv, ChatMessage lastUser, ChatMessage reply, ILlmClient client, string model, Func<Task> onUpdate,
        CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsStore.GetAsync();
            var ask = lastUser.Content.Trim();
            if (!settings.Teaching.Nudges || ask.Length < 15 || ask.StartsWith('/') || reply.Nudge is not null) return;
            if (reply.Used.Any(u => u.Kind == LearnedItem.UsedSkill) || reply.ToolCalls.Any(t => t.Name == "use_skill")
                || reply.LearnedItems.Any(i => i.Kind is LearnedItem.NewSkill or LearnedItem.ImprovedSkill)) return;
            if (conv.Messages.Any(m => m.Nudge is not null && DateTimeOffset.Now - m.CreatedAt < TimeSpan.FromDays(1))) return;
            var query = ChatHistoryIndex.Tokens(ask).Where(w => w.Length >= 3).ToHashSet();
            if (query.Count < 3) return;
            var similar = (await history.SearchAsync(ask, null, 12, conv.Id, 90))
                .Where(h => { var w = ChatHistoryIndex.Tokens(h.Excerpt).ToHashSet(); return query.Count(w.Contains) * 2 >= query.Count; })
                .DistinctBy(h => h.ConversationId).Take(6).ToList();
            if (similar.Count < RepeatsBeforeOffer) return;

            var known = await skills.ActiveAsync();
            var answer = await AskAsync(client, model,
                "You notice when a user keeps asking an AI assistant for the same kind of job, so it can offer to make it a reusable " +
                "skill or a scheduled routine. Reply with exactly one line:\n" +
                "SKILL | <times> | short-dash-name   — the same kind of job, done when asked\n" +
                "ROUTINE | <times> | <when, e.g. every Monday at 9am> | <what to do each time, as an instruction>   — it comes up on a regular rhythm\n" +
                $"NONE   — different jobs, one-offs, small talk, or an existing skill already covers it\n<times> counts the requests that are the same kind of job, this one included. Answer NONE unless it's at least {RepeatsBeforeOffer + 1}.",
                $"Now ({DateTimeOffset.Now:ddd d MMM}): {Clip(ask, 600)}\n\nEarlier, in other chats:\n" +
                string.Join("\n", similar.Select(h => $"- {h.When:ddd d MMM}: {Clip(h.Excerpt, 300)}")) +
                $"\n\nExisting skills: {(known.Count == 0 ? "(none)" : string.Join(", ", known.Select(s => s.Name)))}",
                120, TimeSpan.FromSeconds(90), ct);
            var line = answer.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3 || !int.TryParse(parts[1], out var times) || times < RepeatsBeforeOffer + 1) return;
            var nudge = parts[0].ToUpperInvariant() switch
            {
                "SKILL" when Skill.Slug(parts[2]) is { Length: >= 3 } name => new Nudge { Kind = Nudge.Skill, Times = times, Key = "skill:" + name },
                "ROUTINE" when parts.Length >= 4 && parts[3].Length >= 5 => new Nudge
                {
                    Kind = Nudge.Routine, Times = times, When = parts[2], Task = parts[3], Key = "routine:" + Skill.Slug(parts[3]),
                },
                _ => null,
            };
            if (nudge is null || settings.Teaching.DismissedNudges.Contains(nudge.Key)) return;
            reply.Nudge = nudge;
            await store.SaveAsync(conv, notify: false);
            await onUpdate();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* best effort */ }
    }

    /// <summary>"Not now" on an offer: it isn't made again for the same thing.</summary>
    public async Task DismissNudgeAsync(Conversation conv, ChatMessage reply)
    {
        if (reply.Nudge is not { } n) return;
        n.Outcome = "dismissed";
        var settings = await settingsStore.GetAsync();
        if (!settings.Teaching.DismissedNudges.Contains(n.Key))
        {
            settings.Teaching.DismissedNudges.Add(n.Key);
            if (settings.Teaching.DismissedNudges.Count > 200) settings.Teaching.DismissedNudges.RemoveAt(0);
            await settingsStore.SaveAsync(settings);
        }
        await store.SaveAsync(conv, notify: false);
    }

    /// <summary>The offer was taken up.</summary>
    public async Task NudgeTakenAsync(Conversation conv, ChatMessage reply)
    {
        if (reply.Nudge is not { } n) return;
        n.Outcome = "done";
        await store.SaveAsync(conv, notify: false);
    }
}
