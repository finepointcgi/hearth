using System.Text.RegularExpressions;

namespace Hearth.Core.Models;

/// <summary>
/// A reusable prompt ("skill") shared by every bot. Run it by typing /name in any chat or routine, or let a
/// bot load it with the use_skill tool when it fits the task.
/// </summary>
public sealed partial class Skill
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>Slash-command name: lowercase letters, digits and dashes.</summary>
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "⚡";
    /// <summary>One line: when to use it (shown to bots so they know when to load it).</summary>
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Bot id that wrote it ("hearth" for plain Hearth); null when the user wrote it.</summary>
    public string? CreatedBy { get; set; }
    /// <summary>Written by a bot and waiting for the user's OK; bots don't see it until approved.</summary>
    public bool Pending { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    /// <summary>How many times a reply that followed it got 👎 (all time).</summary>
    public int Wrong { get; set; }
    /// <summary>What the user said was wrong since the skill last changed (newest last, at most 5).</summary>
    public List<string> WrongNotes { get; set; } = [];
    /// <summary>When the user chose to keep it although it wasn't being used.</summary>
    public DateTimeOffset? KeptAt { get; set; }

    /// <summary>How long a skill can go unused before Hearth asks whether to keep it.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromDays(60);

    /// <summary>Not used (or kept) for <see cref="IdleAfter"/>.</summary>
    public bool IsIdle(DateTimeOffset now) =>
        !Pending && now - new[] { LastUsed ?? CreatedAt, KeptAt ?? DateTimeOffset.MinValue, CreatedAt }.Max() > IdleAfter;

    /// <summary>Got 👎 since it last changed.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool NeedsFixing => !Pending && WrongNotes.Count > 0;
    /// <summary>Earlier versions (newest first, at most 5) so a bad rewrite can be undone.</summary>
    public List<SkillRevision> Revisions { get; set; } = [];
    /// <summary>Where it was imported from (URL or file name).</summary>
    public string? Source { get; set; }
    /// <summary>Bundled text files (scripts, references), path → content.</summary>
    public Dictionary<string, string> Files { get; set; } = [];
    /// <summary>Things the import scan flagged for the user to look at.</summary>
    public List<string> Warnings { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public bool WrittenByBot => CreatedBy is not null;

    public static string Slug(string s) =>
        NonSlug().Replace(s.Trim().ToLowerInvariant().Replace(' ', '-'), "").Trim('-');

    [GeneratedRegex("[^a-z0-9-]")] private static partial Regex NonSlug();

    /// <summary>Finds "/name rest…" at the start of a message.</summary>
    public static (Skill skill, string rest)? Match(string text, IReadOnlyList<Skill> skills)
    {
        var t = text.TrimStart();
        if (!t.StartsWith('/')) return null;
        var end = t.IndexOfAny([' ', '\n', '\t']);
        var name = (end < 0 ? t[1..] : t[1..end]).ToLowerInvariant();
        var skill = skills.FirstOrDefault(s => s.Name == name);
        return skill is null ? null : (skill, end < 0 ? "" : t[(end + 1)..].Trim());
    }

    /// <summary>The text the model sees for a message that invokes this skill.</summary>
    public string Expand(string input) =>
        $"Use the \"{Name}\" skill. Its instructions:\n<skill>\n{Instructions.Trim()}\n</skill>\n\n" +
        (string.IsNullOrWhiteSpace(input) ? "(No extra input — apply the skill to the conversation so far.)" : $"Input:\n{input}");
}

public sealed record SkillRevision(string Instructions, DateTimeOffset At, string? Note, string? By);

/// <summary>Bots writing their own skills (like Hermes): after a multi-step job they save the method and improve it later.</summary>
public sealed class SkillLearningSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>New and changed skills wait on the Skills page for your OK.</summary>
    public bool NeedsReview { get; set; }
    /// <summary>Review a reply for a reusable method when it used at least this many tools.</summary>
    public int MinToolCalls { get; set; } = 4;
}

public static class SkillTemplates
{
    public static IReadOnlyList<Skill> All =>
    [
        new() { Name = "summarize", Icon = "📝", Description = "Summarize text, a page or the conversation",
            Instructions = "Summarize the input in at most 5 bullet points that capture what matters most, then add a one-line TL;DR. Keep names, numbers and dates exact." },
        new() { Name = "action-items", Icon = "✅", Description = "Turn meeting notes or a thread into decisions and tasks",
            Instructions = "From the input, extract:\n1. Decisions made\n2. Action items as a table: task | owner | due date (write 'unassigned'/'none' when missing)\n3. Open questions\nDon't invent owners or dates." },
        new() { Name = "proofread", Icon = "🔍", Description = "Fix grammar and clarity without changing the voice",
            Instructions = "Proofread the input. Return the corrected text first, then a short list of the notable changes. Keep the author's voice, tone and formatting; don't add new content." },
        new() { Name = "explain-code", Icon = "🧑‍🏫", Description = "Explain what a piece of code does",
            Instructions = "Explain the code: first one sentence on what it does overall, then walk through the important parts, then list bugs, edge cases or risks you notice." },
        new() { Name = "translate", Icon = "🌍", Description = "Translate text, keeping formatting",
            Instructions = "Translate the input. If the user names a target language use it, otherwise translate into English (or into Spanish if the input is already English). Preserve Markdown formatting, names and numbers. Output only the translation." },
        new() { Name = "eli5", Icon = "🧸", Description = "Explain something like I'm five",
            Instructions = "Explain the topic as if to a curious five-year-old: short sentences, one vivid everyday analogy, no jargon. Finish with one sentence for the grown-ups that states it precisely." },
        new() { Name = "daily-briefing", Icon = "☀️", Description = "Morning news briefing on the user's interests",
            Instructions = "Build a short briefing. Use what you remember about the user to pick 3–5 topics they care about (or general tech news if you know nothing). For each, use web_search (and fetch_url if needed) to find the most important development from the last 24 hours. Output a bulleted list: bold headline, two-sentence summary, [n] citation. End with one line on anything that needs their attention today." },
        new() { Name = "triage", Icon = "🚦", Description = "Triage an incoming event (webhook, file, feed item)",
            Instructions = "An event arrived. Work out what it is, decide how important it is (urgent / today / FYI / ignore) and say why in one line. Then give the next concrete step. If it's 'ignore', just say so briefly." },
    ];
}
