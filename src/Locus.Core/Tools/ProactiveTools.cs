using System.Globalization;
using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Locus.Core.Tools;

/// <summary>
/// Tools that let a bot take initiative: come back to a chat later on its own (follow_up) and hand big jobs to a
/// background task (start_task). Services are looked up when used, since the task runner itself runs chats.
/// </summary>
public sealed class ProactiveToolProvider(IServiceProvider services) : IToolProvider
{
    public Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.TakeInitiative) return Task.FromResult<IReadOnlyList<ITool>>([]);
        var followUps = services.GetRequiredService<FollowUpStore>();
        return Task.FromResult<IReadOnlyList<ITool>>(
        [
            new FollowUpTool(followUps),
            new ListFollowUpsTool(followUps),
            new CancelFollowUpTool(followUps),
            new StartTaskTool(services),
            new ProposeBotTool(services.GetRequiredService<BotStore>()),
        ]);
    }

    /// <summary>"in 90 minutes", "tomorrow 9am" style input: a number of minutes, or a local date and time.</summary>
    public static DateTimeOffset? ParseWhen(JsonObject args, DateTimeOffset now)
    {
        if (args["in_minutes"] is JsonNode m && double.TryParse(m.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mins) && mins > 0)
            return now.AddMinutes(mins);
        var at = args.Str("at");
        if (string.IsNullOrWhiteSpace(at)) return null;
        if (DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) return when;
        if (DateTimeOffset.TryParse(at, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out when)) return when;
        return null;
    }
}

public sealed class FollowUpTool(FollowUpStore store) : ITool
{
    public static readonly TimeSpan Soonest = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Latest = TimeSpan.FromDays(90);

    public string Name => "follow_up";
    public string DisplayName => "Follow up later";
    public string Icon => "⏰";
    public string Description =>
        "Come back to this chat later on your own: at the given time you'll get this note and can use your tools again, " +
        "then the user sees what you found. Use it whenever something should be checked again (a reply to wait for, new " +
        "job postings, a price, a deadline, a long download) or the user asks to be reminded — instead of asking them " +
        "to remind you. Give in_minutes or at (local time). repeat: daily or weekly for ongoing watching.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["note"] = new JsonObject { ["type"] = "string", ["description"] = "What to do then, specific enough to act on, e.g. 'Search for new Director of Software roles in Detroit posted since today and compare them to his profile'" },
            ["in_minutes"] = new JsonObject { ["type"] = "number", ["description"] = "Minutes from now (at least 5)" },
            ["at"] = new JsonObject { ["type"] = "string", ["description"] = "Local date and time, e.g. 2026-11-05 09:00" },
            ["repeat"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("once", "daily", "weekly") },
        },
        ["required"] = new JsonArray("note"),
    };
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var note = args.Str("note")?.Trim();
        if (string.IsNullOrEmpty(note)) return ToolResult.Error("Say what to do when you follow up (note).");
        var now = DateTimeOffset.Now;
        if (ProactiveToolProvider.ParseWhen(args, now) is not { } due) return ToolResult.Error("Give in_minutes (a number) or at (a local date and time like 2026-11-05 09:00).");
        if (due < now + Soonest) due = now + Soonest;
        if (due > now + Latest) return ToolResult.Error("That's too far ahead: follow-ups can be at most 90 days away.");
        var repeat = args.Str("repeat") is "daily" or "weekly" ? args.Str("repeat") : null;
        try
        {
            var f = await store.AddAsync(new FollowUp
            {
                ConversationId = ctx.Conversation.Id, BotId = ctx.Bot?.Id, Note = note, DueAt = due, Repeat = repeat,
            });
            return new ToolResult($"Follow-up {f.Id} scheduled for {Describe(due, now)}{(repeat is null ? "" : $", then {repeat}")}. " +
                "Tell the user when you'll be back and what you'll check.");
        }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
    }

    public static string Describe(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.Date == now.Date ? "today" : when.Date == now.Date.AddDays(1) ? "tomorrow" : when.ToString("ddd MMM d", CultureInfo.InvariantCulture);
        return $"{day} at {when.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }
}

public sealed class ListFollowUpsTool(FollowUpStore store) : ITool
{
    public string Name => "list_follow_ups";
    public string DisplayName => "Scheduled follow-ups";
    public string Icon => "⏰";
    public string Description => "List the follow-ups scheduled in this chat (id, when, what).";
    public JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new JsonObject() };
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var mine = (await store.ListAsync()).Where(f => f.ConversationId == ctx.Conversation.Id).ToList();
        if (mine.Count == 0) return new ToolResult("No follow-ups scheduled in this chat.");
        var now = DateTimeOffset.Now;
        return new ToolResult(string.Join("\n", mine.Select(f =>
            $"{f.Id}: {FollowUpTool.Describe(f.DueAt, now)}{(f.Repeat is null ? "" : $" ({f.Repeat})")} — {f.Note}")));
    }
}

public sealed class CancelFollowUpTool(FollowUpStore store) : ITool
{
    public string Name => "cancel_follow_up";
    public string DisplayName => "Cancel follow-up";
    public string Icon => "⏰";
    public string Description => "Cancel a follow-up in this chat by id (from list_follow_ups), e.g. when it's no longer needed.";
    public JsonObject Parameters => Schema.Object(("id", "string", "Follow-up id", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var id = args.Str("id") ?? "";
        var removed = await store.UpdateAsync(list => list.RemoveAll(f => f.Id == id && f.ConversationId == ctx.Conversation.Id) > 0);
        return removed ? new ToolResult($"Follow-up {id} cancelled.") : ToolResult.Error($"No follow-up {id} in this chat.");
    }
}

public sealed class StartTaskTool(IServiceProvider services) : ITool
{
    public string Name => "start_task";
    public string DisplayName => "Start a background task";
    public string Icon => "🎯";
    public string Description =>
        "Hand a big job to a background task that plans it, works through it step by step (for as long as it takes, " +
        "surviving restarts) and reports back in this chat. Use it when the request needs many searches or steps — " +
        "research, comparing lots of options, a job hunt, a long write-up — rather than doing it all in one reply. " +
        "Write the goal with everything the task needs to know, since it won't see this chat.";
    public JsonObject Parameters => Schema.Object(("goal", "string", "The full goal, with the context and constraints from this chat", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var goal = args.Str("goal")?.Trim();
        if (string.IsNullOrEmpty(goal)) return ToolResult.Error("Give the goal.");
        var inTask = ctx.Conversation.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.RoutineName == "Task";
        if (inTask || ctx.Depth > 0) return ToolResult.Error("You're already working inside a task or for another bot: do the work here instead.");
        var task = await services.GetRequiredService<TaskRunner>().StartAsync(goal, ctx.Bot?.Id, reportTo: ctx.Conversation.Id);
        return new ToolResult($"Task {task.Id} started; it will post its plan, any question and the final result in this chat. " +
            "Tell the user it's running in the background (Inbox → Tasks shows progress).");
    }
}

/// <summary>
/// "Hiring": when the same kind of work keeps coming up, a bot can propose a new teammate for it. Always asks you
/// first (you see the name, role, instructions and abilities), then the bot joins your team.
/// </summary>
public sealed class ProposeBotTool(BotStore bots) : ITool, IPermissionInfo
{
    /// <summary>Abilities a proposal can ask for; anything left out is off (except teamwork, skills and documents).</summary>
    public static readonly string[] AbilityNames = ["web", "browser", "files", "code", "images", "sheets", "gmail", "calendar"];

    public string Name => "propose_new_bot";
    public string DisplayName => "Hire a new bot";
    public string Icon => "🧑‍💼";
    public string Description =>
        "Propose adding a new bot to the user's team, like hiring an employee, when a kind of work keeps coming up that " +
        "no current bot specialises in (check search_past_chats for how often it happens). The user sees your proposal " +
        "and approves or declines it. Give it a clear role and instructions, and only the abilities it needs.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Short first name, e.g. Scout" },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Role, e.g. Job hunter" },
            ["emoji"] = new JsonObject { ["type"] = "string" },
            ["instructions"] = new JsonObject { ["type"] = "string", ["description"] = "What it does and how, in the second person ('You find…')" },
            ["why"] = new JsonObject { ["type"] = "string", ["description"] = "Why the user would want it: what keeps coming up" },
            ["abilities"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(AbilityNames.Select(a => (JsonNode)a).ToArray()) } },
        },
        ["required"] = new JsonArray("name", "title", "instructions", "why"),
    };
    public bool RequiresApproval => true;
    public string Area => "hiring";
    public string AreaTitle => "New bots";
    public bool IsWrite => true;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var name = args.Str("name")?.Trim();
        var title = args.Str("title")?.Trim();
        var instructions = args.Str("instructions")?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(instructions))
            return ToolResult.Error("Give a name, title and instructions.");
        var team = await bots.ListAsync();
        if (team.Count >= Bot.MaxBots) return ToolResult.Error($"The team is full ({Bot.MaxBots} bots).");
        if (team.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Error($"There's already a bot called {name}. Pick another name, or improve that bot instead.");

        var wanted = Abilities(args);
        var bot = new Bot
        {
            Name = name.Length > 40 ? name[..40] : name,
            Title = title.Length > 60 ? title[..60] : title,
            Emoji = args.Str("emoji") is { Length: > 0 and <= 8 } e ? e : "🤖",
            Instructions = instructions,
            Abilities = new BotAbilities
            {
                WebSearch = wanted.Contains("web"), Browser = wanted.Contains("browser"), Files = wanted.Contains("files"),
                Code = wanted.Contains("code"), Images = wanted.Contains("images"), GoogleSheets = wanted.Contains("sheets"),
                Gmail = wanted.Contains("gmail"), Calendar = wanted.Contains("calendar"), Mcp = false,
            },
        };
        await bots.SaveAsync(bot);
        return new ToolResult($"{bot.Name} ({bot.Title}) joined the team. Hand them work with message_bot or by @mentioning " +
            $"@{bot.Name} in a chat. The user can fine-tune them under Bots → {bot.Name}.");
    }

    private static HashSet<string> Abilities(JsonObject args)
    {
        var node = args["abilities"];
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('[')) node = JsonNode.Parse(s);
        var list = node is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : (args.Str("abilities") ?? "").Split(',');
        return list.Select(x => x.Trim().ToLowerInvariant()).Where(AbilityNames.Contains).ToHashSet();
    }
}
