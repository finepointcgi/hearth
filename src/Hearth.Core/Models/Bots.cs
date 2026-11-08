namespace Hearth.Core.Models;

public enum BotToolMode { All, Selected, None }

public enum BrowserProfile
{
    /// <summary>The bot gets its own browser profile: its own cookies and sign-ins.</summary>
    Own,
    /// <summary>The bot uses the browser profile shared by all bots.</summary>
    Shared,
}

/// <summary>What a bot is allowed to do. Each switch maps to a group of tools.</summary>
public enum DesktopTarget { ThisComputer, Sandbox }

public sealed class BotAbilities
{
    public bool WebSearch { get; set; } = true;
    public bool Browser { get; set; } = true;
    public BrowserProfile BrowserProfile { get; set; } = BrowserProfile.Own;
    public bool ComputerUse { get; set; }
    /// <summary>Which screen computer use drives: the user's own, or a private Linux desktop in Docker.</summary>
    public DesktopTarget Desktop { get; set; } = DesktopTarget.ThisComputer;
    public bool Files { get; set; } = true;
    public bool Code { get; set; }
    /// <summary>With Code on: the project folder this bot works in by default (its chats and work handed to it get
    /// the code-session tools there). Code sessions started with this bot can pick another folder.</summary>
    public string? ProjectFolder { get; set; }
    /// <summary>How edits in <see cref="ProjectFolder"/> are approved outside a code session.</summary>
    public CodePermissionMode CodeMode { get; set; } = CodePermissionMode.Ask;
    public bool Images { get; set; } = true;
    public bool Documents { get; set; } = true;
    public bool Skills { get; set; } = true;
    public bool MessageBots { get; set; } = true;
    public bool Mcp { get; set; } = true;
    public bool GoogleSheets { get; set; } = true;
    public bool Gmail { get; set; }
    public bool Calendar { get; set; } = true;
    /// <summary>Outlook mail and calendar, OneDrive, Word, Excel and To Do (off by default, like Gmail: it's your email).</summary>
    public bool Microsoft365 { get; set; }
    /// <summary>Google Docs and Drive: find, read and create documents.</summary>
    public bool GoogleDocs { get; set; } = true;
    /// <summary>Look up people in your contacts (off by default: it's personal data).</summary>
    public bool Contacts { get; set; }

    /// <summary>The switch a tool belongs to (null = always allowed, e.g. the calculator).</summary>
    public static string? GroupOf(string tool) => tool switch
    {
        "calculate" or "get_current_datetime" or "convert_units" => null,
        "follow_up" or "list_follow_ups" or "cancel_follow_up" or "start_task" or "propose_new_bot" or "rename_chat" or "delegate_task" or "undo_change" => null,
        "web_search" or "fetch_url" => nameof(WebSearch),
        _ when tool.StartsWith("browser_") => nameof(Browser),
        _ when tool.StartsWith("desktop_") => nameof(ComputerUse),
        "list_files" or "read_file" or "write_file" => nameof(Files),
        "edit_project_file" or "write_project_file" or "undo_project_change" or "list_project_changes" => nameof(Files),
        "run_in_project" or "run_tools_script" => nameof(Code),
        "glob" or "grep" or "read" or "edit" or "write" or "bash" or "todo_write" or "undo_edit" => nameof(Code),
        "run_python" or "run_command" or "run_remote" => nameof(Code),
        "generate_image" => nameof(Images),
        "search_documents" or "read_linked_file" or "list_linked_files" => nameof(Documents),
        "use_skill" or "save_skill" or "improve_skill" => nameof(Skills),
        "message_bot" => nameof(MessageBots),
        "save_memory" or "delete_memory" or "recall_memory" or "search_past_chats" or "read_past_chat" or "who_is" or "note_about" or "life_threads" => "Memory",
        _ when tool.StartsWith("sheets_") => nameof(GoogleSheets),
        _ when tool.StartsWith("gmail_") => nameof(Gmail),
        _ when tool.StartsWith("calendar_") => nameof(Calendar),
        _ when tool.StartsWith("drive_") || tool.StartsWith("docs_") => nameof(GoogleDocs),
        "find_contact" => nameof(Contacts),
        _ when tool.StartsWith("outlook_") || tool.StartsWith("onedrive_") || tool.StartsWith("excel_") || tool == "todo" => nameof(Microsoft365),
        _ => nameof(Mcp),
    };

    public bool Allows(string tool, BotMemory memory) => GroupOf(tool) switch
    {
        null => true,
        nameof(WebSearch) => WebSearch,
        nameof(Browser) => Browser,
        nameof(ComputerUse) => ComputerUse,
        nameof(Files) => Files,
        nameof(Code) => Code,
        nameof(Images) => Images,
        nameof(Documents) => Documents,
        nameof(Skills) => Skills,
        nameof(MessageBots) => MessageBots,
        "Memory" => memory.Enabled,
        nameof(GoogleSheets) => GoogleSheets,
        nameof(Gmail) => Gmail,
        nameof(Calendar) => Calendar,
        nameof(Microsoft365) => Microsoft365,
        nameof(GoogleDocs) => GoogleDocs,
        nameof(Contacts) => Contacts,
        _ => Mcp,
    };
}

/// <summary>How a bot remembers things.</summary>
public sealed class BotMemory
{
    /// <summary>The bot keeps its own memory (notes it saves, shown in every chat with it).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>After each reply the bot quietly picks out things worth remembering.</summary>
    public bool AutoLearn { get; set; } = true;
    /// <summary>The bot can see the facts about you that all bots share.</summary>
    public bool UseSharedMemory { get; set; } = true;
    /// <summary>Facts about you that this bot learns are added to the shared memory too.</summary>
    public bool ShareWhatItLearns { get; set; } = true;
    /// <summary>How many memories go into each prompt; older ones are found with recall_memory.</summary>
    public int MaxInPrompt { get; set; } = 40;
}

public enum RoutineSchedule { EveryMinutes, Daily, Weekdays, Weekly }

/// <summary>What starts a routine.</summary>
public enum TriggerKind
{
    /// <summary>A clock schedule (see <see cref="Routine.Schedule"/>).</summary>
    Schedule,
    /// <summary>An HTTP POST to /hooks/{id} with the routine's secret (GitHub, Home Assistant, IFTTT, scripts…).</summary>
    Webhook,
    /// <summary>A file is added or changed in a folder (desktop only).</summary>
    Folder,
    /// <summary>A new item appears in an RSS/Atom feed (GitHub releases, blogs, YouTube, Reddit…).</summary>
    Feed,
    /// <summary>The readable text of a web page changes.</summary>
    PageChange,
    /// <summary>Rows are added or changed in a Google Sheet range.</summary>
    Sheet,
    /// <summary>New Gmail messages match a search (Url holds the query).</summary>
    Email,
    /// <summary>A Google Calendar event starts within LeadMinutes.</summary>
    Calendar,
}

/// <summary>A scheduled prompt a bot runs on its own (e.g. a morning briefing).</summary>
public sealed class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Routine";
    public string Prompt { get; set; } = "";
    public RoutineSchedule Schedule { get; set; } = RoutineSchedule.Daily;
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Local time, HH:mm.</summary>
    public string TimeOfDay { get; set; } = "08:00";
    /// <summary>Weekly: the day it runs.</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    public bool Enabled { get; set; } = true;
    /// <summary>Also send results to linked chat apps (Telegram, Discord…) that have routine notifications on.</summary>
    public bool NotifyChannels { get; set; } = true;
    /// <summary>A check-in: in chat apps it arrives as an ordinary message in your chat (no routine header), so a reply
    /// carries on the conversation.</summary>
    public bool Conversational { get; set; }
    public DateTimeOffset? LastRun { get; set; }

    public TriggerKind Trigger { get; set; } = TriggerKind.Schedule;
    /// <summary>Webhook: shared secret the caller must send (?token= or X-Hearth-Token header).</summary>
    public string WebhookSecret { get; set; } = NewSecret();
    /// <summary>Folder: path to watch.</summary>
    public string FolderPath { get; set; } = "";
    /// <summary>Folder: file pattern, e.g. *.pdf. Empty = all files.</summary>
    public string FilePattern { get; set; } = "";
    /// <summary>Feed / PageChange: URL to poll.</summary>
    public string Url { get; set; } = "";
    /// <summary>Calendar: how many minutes before an event starts to run.</summary>
    public int LeadMinutes { get; set; } = 15;
    /// <summary>Sheet: A1 range to watch, e.g. "Leads!A:F" (default: the first tab).</summary>
    public string SheetRange { get; set; } = "";
    /// <summary>Feed / PageChange / Sheet: minutes between checks. (For Sheet, Url holds the spreadsheet URL, ID or name.)</summary>
    public int PollMinutes { get; set; } = 15;

    public static string NewSecret() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Polling triggers: is it time to check the source again?</summary>
    public bool IsPollDue(DateTimeOffset now, DateTimeOffset? lastPolled) =>
        Enabled && (Trigger is TriggerKind.Feed or TriggerKind.PageChange or TriggerKind.Sheet or TriggerKind.Email or TriggerKind.Calendar)
        && (!string.IsNullOrWhiteSpace(Url) || Trigger is TriggerKind.Email or TriggerKind.Calendar)
        && (lastPolled is null || now - lastPolled.Value >= TimeSpan.FromMinutes(Math.Max(1, PollMinutes)));

    public bool IsDue(DateTimeOffset now)
    {
        if (Trigger != TriggerKind.Schedule) return false;
        if (!Enabled || string.IsNullOrWhiteSpace(Prompt)) return false;
        if (Schedule == RoutineSchedule.EveryMinutes)
            return LastRun is null || now - LastRun.Value >= TimeSpan.FromMinutes(Math.Max(5, IntervalMinutes));

        if (!TimeOnly.TryParse(TimeOfDay, out var t)) return false;
        if (Schedule == RoutineSchedule.Weekdays && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (Schedule == RoutineSchedule.Weekly && now.DayOfWeek != Day) return false;
        var dueToday = new DateTimeOffset(now.Date + t.ToTimeSpan(), now.Offset);
        // Run once per day, at or after the time, but not if we're more than 6 hours late (app was closed).
        return now >= dueToday && now - dueToday < TimeSpan.FromHours(6) && (LastRun is null || LastRun < dueToday);
    }

    public string Describe() => Trigger switch
    {
        TriggerKind.Webhook => "on webhook",
        TriggerKind.Folder => $"when files change in {Path.GetFileName(FolderPath.TrimEnd('/', '\\'))}",
        TriggerKind.Feed => "on new feed items",
        TriggerKind.PageChange => "when the page changes",
        TriggerKind.Sheet => "when the sheet changes",
        TriggerKind.Email => "on new email",
        TriggerKind.Calendar => $"{LeadMinutes} min before events",
        _ => Schedule switch
        {
            RoutineSchedule.EveryMinutes => $"every {IntervalMinutes} min",
            RoutineSchedule.Weekdays => $"weekdays at {TimeOfDay}",
            RoutineSchedule.Weekly => $"{Day}s at {TimeOfDay}",
            _ => $"daily at {TimeOfDay}",
        },
    };

    public string Icon => Trigger == TriggerKind.Schedule ? "⏰" : "⚡";

    /// <summary>Next time a scheduled routine will run (null for event triggers or when off).</summary>
    public DateTimeOffset? NextRun(DateTimeOffset now)
    {
        if (!Enabled || Trigger != TriggerKind.Schedule) return null;
        if (Schedule == RoutineSchedule.EveryMinutes)
            return LastRun is null ? now : LastRun.Value.AddMinutes(Math.Max(5, IntervalMinutes));
        if (!TimeOnly.TryParse(TimeOfDay, out var t)) return null;
        for (var d = 0; d < 8; d++)
        {
            var day = now.Date.AddDays(d);
            if (Schedule == RoutineSchedule.Weekdays && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (Schedule == RoutineSchedule.Weekly && day.DayOfWeek != Day) continue;
            var at = new DateTimeOffset(day + t.ToTimeSpan(), now.Offset);
            if (at > now && (LastRun is null || LastRun < at)) return at;
        }
        return null;
    }
}

/// <summary>A persistent AI teammate with its own role, instructions, model, tools, memory and routines.</summary>
public sealed class Bot
{
    /// <summary>
    /// The id saved for "no bot, just the assistant" (in chat-app links, "Always allow" choices, skill authors and
    /// notes). It's the app's first name and stays as it is so data saved before the rename still matches.
    /// </summary>
    public const string PlainId = "locus";
    public const int MaxBots = 50;
    public const int MaxPerGroup = 6;

    /// <summary>"Chief of Staff" → "chief-of-staff" (for API model names).</summary>
    public static string SlugName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Bot";
    public string Emoji { get; set; } = "🤖";
    /// <summary>
    /// A picture for the avatar, as a data: URL (a small PNG/JPEG/WebP, or a GIF kept as it is so it stays animated).
    /// Shown instead of <see cref="Emoji"/> wherever the app can show pictures; chat apps still get the emoji.
    /// </summary>
    public string? AvatarImage { get; set; }
    /// <summary>How the avatar moves while the bot is thinking or working: one of <see cref="WorkingAnimations"/>.</summary>
    public string WorkingAnimation { get; set; } = "pulse";

    /// <summary>The working animations a bot can pick: id and label.</summary>
    public static readonly (string Id, string Label)[] WorkingAnimations =
    [
        ("pulse", "Pulse"), ("dots", "Typing dots"), ("bounce", "Bounce"), ("spin", "Spinning ring"),
        ("wave", "Sound wave"), ("glow", "Glow"), ("wobble", "Wobble"), ("none", "None"),
    ];
    /// <summary>Accent colour for the avatar, CSS hex.</summary>
    public string Color { get; set; } = "#7c9cff";
    public string Title { get; set; } = "Assistant";
    public string Instructions { get; set; } = "";
    /// <summary>Model override; null uses whatever is selected in the chat.</summary>
    public string? Model { get; set; }
    /// <summary>Model server this bot runs on; null uses the app's active server.</summary>
    public string? ProviderId { get; set; }
    public BotAbilities Abilities { get; set; } = new();
    /// <summary>Example questions with what a good answer looks like, re-run after changes.</summary>
    public List<BotCheck> Checks { get; set; } = [];
    public BotMemory Memory { get; set; } = new();
    /// <summary>Advanced: "Selected" further limits the bot to <see cref="AllowedTools"/>.</summary>
    public BotToolMode ToolMode { get; set; } = BotToolMode.All;
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>The browser profile this bot uses: its id for its own profile, or "shared".</summary>
    public string BrowserProfileKey => Abilities.BrowserProfile == BrowserProfile.Own ? Id : "shared";
    /// <summary>Coordinators route work to specialists and summarise results ("chief of staff").</summary>
    public bool IsCoordinator { get; set; }
    public List<Routine> Routines { get; set; } = [];
    /// <summary>
    /// Other models this bot can run on ("claude": Claude Code's model, "fast": a small local one…). A coordinator
    /// picks one with message_bot when a problem needs a stronger, faster or more private model.
    /// </summary>
    public List<BotVersion> Versions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>The version named <paramref name="name"/> (case-insensitive), or null.</summary>
    public BotVersion? FindVersion(string? name) => string.IsNullOrWhiteSpace(name) ? null
        : Versions.FirstOrDefault(v => v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>This bot running on <paramref name="version"/>'s server and model (same id, memory and chats).</summary>
    public Bot WithVersion(BotVersion version)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<Bot>(System.Text.Json.JsonSerializer.Serialize(this))!;
        copy.ProviderId = version.ProviderId;
        copy.Model = string.IsNullOrWhiteSpace(version.Model) ? null : version.Model.Trim();
        return copy;
    }
}

/// <summary>Another model a bot can run on, and when to use it.</summary>
public sealed class BotVersion
{
    /// <summary>Short name a coordinator uses to pick it, e.g. "claude" or "fast".</summary>
    public string Name { get; set; } = "";
    /// <summary>Model server; null uses the app's active server.</summary>
    public string? ProviderId { get; set; }
    /// <summary>Model on that server; null uses the server's default.</summary>
    public string? Model { get; set; }
    /// <summary>When to use it, e.g. "stronger at research and code, but uses your Claude plan".</summary>
    public string Note { get; set; } = "";
}

public static class BotTemplates
{
    public static IReadOnlyList<Bot> All =>
    [
        new() { Name = "Chief of Staff", Emoji = "🧭", Color = "#f5b942", Title = "Coordinator", IsCoordinator = true,
            Abilities = new() { Browser = false, Images = false },
            Instructions = "You run the user's team of bots. Understand what the user wants, break it into pieces, hand each " +
                           "piece to the right specialist by @mentioning them, then pull their results together into one clear " +
                           "answer. Do small things yourself. Ask the user only for real judgment calls." },
        new() { Name = "Researcher", Emoji = "🔎", Color = "#4ade80", Title = "Research & fact-checking",
            Abilities = new() { Images = false },
            Instructions = "You find out what's true. Search the web, read primary sources, compare them and cite everything " +
                           "with [n]. Say clearly when evidence is weak or conflicting." },
        new() { Name = "Engineer", Emoji = "🛠️", Color = "#7c9cff", Title = "Software engineer",
            Abilities = new() { Code = true, Images = false },
            Instructions = "You write and debug code. In a code session, work in the project like a careful senior engineer: read " +
                           "before changing, make small exact edits, run the build and tests, and report what you verified. " +
                           "Otherwise prefer complete, runnable solutions and test with run_python when it helps. Point out bugs and edge cases." },
        new() { Name = "Writer", Emoji = "✍️", Color = "#f472b6", Title = "Writing & editing",
            Abilities = new() { Browser = false },
            Instructions = "You write clear, vivid, well-structured prose in the user's voice: emails, posts, docs, stories. " +
                           "Keep it tight; offer one strong draft rather than many options." },
        new() { Name = "Critic", Emoji = "🧐", Color = "#ff6b6b", Title = "Reviewer & devil's advocate",
            Abilities = new() { Browser = false, Images = false, Files = false },
            Instructions = "You review other bots' work and the user's ideas. Find the weakest points, missing cases and " +
                           "risks, and say how to fix them. Be direct but constructive. Don't rewrite everything." },
        new() { Name = "Planner", Emoji = "🗓️", Color = "#38bdf8", Title = "Plans, schedules & checklists",
            Instructions = "You turn goals into concrete plans: steps, owners, timelines and checklists. Keep plans realistic " +
                           "and flag dependencies." },
        new() { Name = "Comedian", Emoji = "🎭", Color = "#c084fc", Title = "Entertainment",
            Abilities = new() { Browser = false, Files = false, Documents = false }, Memory = new() { ShareWhatItLearns = false },
            Instructions = "You're the funny one: witty, sarcastic, irreverent. Roast gently, riff on what others say, " +
                           "and keep the group chat fun — but stay accurate if facts come up." },
    ];
}

public sealed class BotCheck
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Question { get; set; } = "";
    /// <summary>What a good answer does, in plain words (judged by a model).</summary>
    public string Expect { get; set; } = "";
    /// <summary>Optional words the answer must contain (comma-separated, case-insensitive).</summary>
    public string? MustInclude { get; set; }
    public BotCheckResult? Last { get; set; }
}

public sealed record BotCheckResult(bool Pass, string Reason, string Answer, DateTimeOffset At, string Model, long Milliseconds);
