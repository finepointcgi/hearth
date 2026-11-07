using System.Text;
using System.Text.Json.Nodes;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Tools;

namespace Locus.Core.Services;

public sealed record SendOptions(string Model, bool Think, bool DeepSearch, bool UseTools);

/// <summary>
/// Runs replies. Plain chats get one agent reply; bot chats run as that bot; group chats route the user's
/// message to the right bots and let bots hand work to each other with @mentions (bounded by MaxBotHops).
/// Each agent reply is a loop: stream → run tool calls (asking the user when needed) → feed back → repeat.
/// </summary>
public sealed class ChatService(
    ILlmClientFactory factory,
    SettingsStore settingsStore,
    ConversationStore store,
    WebSearchService search,
    MemoryStore memories,
    ToolRegistry tools,
    ImageGenService imageGen,
    BotStore bots,
    SkillStore skillStore,
    ApprovalCenter approvals,
    ActivityLog activity,
    MemoryCurator curator,
    ProjectContext projectContext,
    CodeTools codeTools,
    PeopleStore people,
    Companion companion,
    UsageStore usage,
    UndoLog undoLog)
{
    private const int MaxToolResultChars = 16000;
    private const int MaxBotCallDepth = 2;

    /// <summary>Entry point from the chat UI: replies to the last user message in any kind of conversation.</summary>
    public async Task GenerateAsync(Conversation conv, SendOptions opts, Func<Task> onUpdate, CancellationToken ct)
    {
        try { await GenerateCoreAsync(conv, opts, onUpdate, ct); }
        finally
        {
            // Name the chat once there's something to name it after (any kind of chat, groups included).
            var auto = (await settingsStore.GetAsync()).AutoTitle;
            var title = auto && NeedsTitle(conv);
            var topic = auto && conv.Topic is null && conv.Messages.Any(m => m is { Role: ChatRole.Assistant, Notice: null, Content.Length: > 0 });
            if (title || topic)
                _ = Task.Run(async () =>
                {
                    if (title) await AutoTitleAsync(conv, onUpdate);
                    if (topic) await AutoTopicAsync(conv, onUpdate);
                });
        }
    }

    private async Task GenerateCoreAsync(Conversation conv, SendOptions opts, Func<Task> onUpdate, CancellationToken ct)
    {
        var lastUser = conv.Messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser is { FromBotId: null }) await JoinMentionedBotsAsync(conv, lastUser.Content, null, onUpdate);
        if (conv.BotIds.Count == 0 && await DefaultBotForAsync(conv, lastUser) is { } lead)
        {
            // New chats go to the default bot (the Chief of Staff unless chosen otherwise), which runs the team.
            conv.BotIds.Add(lead.Id);
            await store.SaveAsync(conv);
        }
        if (conv.BotIds.Count == 0)
        {
            await RunAgentAsync(conv, null, opts, onUpdate, ct);
            return;
        }

        var members = await bots.GetManyAsync(conv.BotIds);
        if (members.Count == 0) throw new InvalidOperationException("The bots in this chat were deleted.");
        // A coordinator leading the chat plans first: answer itself, hand to one specialist, or run the team.
        var coordinator = members.FirstOrDefault(b => b.IsCoordinator);
        if (coordinator is not null && lastUser is { FromBotId: null, RoutineName: null } && Mentions(lastUser.Content, members, true).All(b => b.Id == coordinator.Id)
            && await PlanAsync(conv, coordinator, members, lastUser, opts, ct) is { } plan)
        {
            if (plan.Assign.Count > 0)
            {
                await RunTeamAsync(conv, coordinator, plan, opts, onUpdate, ct);
                return;
            }
            // The coordinator answers itself.
            members = [coordinator];
        }
        if (members.Count == 1)
        {
            var reply = await RunAgentAsync(conv, members[0], opts, onUpdate, ct);
            if (reply is null || reply.Error is not null) return;
            // The bot may have brought others in by @mentioning them: carry on as a group, newcomers first.
            var joined = await JoinMentionedBotsAsync(conv, reply.Content, members[0], onUpdate);
            if (joined.Count > 0)
                await RunGroupTurnAsync(conv, [.. members, .. joined], opts, onUpdate, ct, joined,
                    new() { [members[0].Id] = 1 });
            return;
        }
        await RunGroupTurnAsync(conv, members, opts, onUpdate, ct);
    }

    /// <summary>
    /// "@Name" for a bot that isn't in this chat adds it, so any chat can grow into a group just by mentioning
    /// someone (up to Bot.MaxPerGroup). Works for the user's messages and for bots' replies (when the bot is
    /// allowed to message bots). Returns the bots that joined.
    /// </summary>
    private async Task<List<Bot>> JoinMentionedBotsAsync(Conversation conv, string text, Bot? by, Func<Task> onUpdate)
    {
        if (!text.Contains('@') || by is { Abilities.MessageBots: false }) return [];
        var room = Bot.MaxPerGroup - conv.BotIds.Count;
        if (room <= 0) return [];

        var outsiders = (await bots.ListAsync()).Where(b => !conv.BotIds.Contains(b.Id)).ToList();
        var joining = Mentions(text, outsiders, includeEveryone: false).Take(room).ToList();
        if (joining.Count == 0) return [];

        var wasGroup = conv.BotIds.Count > 1;
        conv.BotIds.AddRange(joining.Select(b => b.Id));
        // A one-on-one chat titled after its bot becomes a group: retitle it after everyone in it.
        if (!wasGroup && conv.BotIds.Count > 1)
        {
            var all = await bots.GetManyAsync(conv.BotIds);
            if (all.Any(b => b.Name == conv.Title)) conv.Title = string.Join(", ", all.Select(b => b.Name));
        }
        var names = string.Join(", ", joining.Select(b => $"{b.Emoji} {b.Name}"));
        conv.Messages.Add(new ChatMessage
        {
            Role = ChatRole.Assistant,
            Notice = by is null ? $"{names} joined the chat." : $"{by.Emoji} {by.Name} added {names} to the chat.",
        });
        await store.SaveAsync(conv);
        await onUpdate();
        return joining;
    }

    // ---------------------------------------------------------------- group chats

    private async Task RunGroupTurnAsync(Conversation conv, List<Bot> members, SendOptions opts, Func<Task> onUpdate,
        CancellationToken ct, List<Bot>? startWith = null, Dictionary<string, int>? repliesSoFar = null)
    {
        var settings = await settingsStore.GetAsync();
        var lastUser = conv.Messages.Last(m => m.Role == ChatRole.User);
        var queue = new List<Bot>(startWith ?? Mentions(lastUser.Content, members, includeEveryone: true));
        if (queue.Count == 0) queue.AddRange(await RouteAsync(conv, members, lastUser.Content, opts.Model, ct));

        var repliesPerBot = repliesSoFar ?? [];
        var hops = repliesPerBot.Values.Sum();
        while (queue.Count > 0 && hops < Math.Max(1, settings.MaxBotHops) && !ct.IsCancellationRequested)
        {
            var bot = queue[0];
            queue.RemoveAt(0);
            repliesPerBot[bot.Id] = repliesPerBot.GetValueOrDefault(bot.Id) + 1;
            hops++;

            var reply = await RunAgentAsync(conv, bot, opts, onUpdate, ct, autoTitle: false);
            if (reply is null || reply.Error is not null) break;

            // Bots @mentioned in the reply who aren't here yet join the chat.
            members.AddRange(await JoinMentionedBotsAsync(conv, reply.Content, bot, onUpdate));
            // Handoffs: bots @mentioned in the reply speak next (each at most twice per user message).
            foreach (var next in Mentions(reply.Content, members, includeEveryone: false))
                if (next.Id != bot.Id && !queue.Contains(next) && repliesPerBot.GetValueOrDefault(next.Id) < 2)
                    queue.Add(next);
        }
        if (queue.Count > 0 && hops >= settings.MaxBotHops)
        {
            conv.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Notice = $"Paused after {hops} bot replies so they don't loop. Reply to keep going.",
            });
            await store.SaveAsync(conv);
            await onUpdate();
        }
    }

    /// <summary>Bots named as "@Name" in the text (longest names first so "@Ann Lee" beats "@Ann").</summary>
    public static List<Bot> Mentions(string text, IReadOnlyList<Bot> members, bool includeEveryone)
    {
        if (includeEveryone && (text.Contains("@everyone", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("@all", StringComparison.OrdinalIgnoreCase)))
            return members.ToList();
        var found = new List<(int pos, Bot bot)>();
        foreach (var b in members.OrderByDescending(b => b.Name.Length))
        {
            var i = text.IndexOf("@" + b.Name, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && !found.Any(f => f.pos == i)) found.Add((i, b));
        }
        return found.OrderBy(f => f.pos).Select(f => f.bot).Distinct().ToList();
    }

    /// <summary>How long the coordinator's planning step may take before it just answers.</summary>
    public static TimeSpan StraightRouteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The bot that answers a new chat with no bot in it: the chosen default, else the coordinator.</summary>
    private async Task<Bot?> DefaultBotForAsync(Conversation conv, ChatMessage lastUser)
    {
        if (lastUser.FromBotId is not null || lastUser.RoutineName is not null || conv.HandoffFrom is not null) return null;
        if (conv.Messages.Any(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)) return null; // an existing plain chat stays plain
        if (CodeTools.SessionFor(conv, null) is not null) return null; // code sessions work on their own
        var settings = await settingsStore.GetAsync();
        if (settings.DefaultBotId == "") return null;
        var all = await bots.ListAsync();
        return all.FirstOrDefault(b => b.Id == settings.DefaultBotId) ?? all.FirstOrDefault(b => b.IsCoordinator);
    }

    /// <summary>What the coordinator decided: who works on it (in order), and whether the reviewer checks it.</summary>
    public sealed record TeamPlan(List<(Bot bot, string task)> Assign, bool Review, Bot? Reviewer);

    /// <summary>
    /// The coordinator's planning step: one short model call deciding whether to answer itself, hand the request to one
    /// specialist, or split it across the team (with a review). Null when planning is off or it fails (it answers).
    /// </summary>
    private async Task<TeamPlan?> PlanAsync(Conversation conv, Bot coordinator, List<Bot> members, ChatMessage lastUser, SendOptions opts, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync();
        if (!settings.Teamwork.Plan || !coordinator.Abilities.MessageBots || !opts.UseTools) return null;
        if (lastUser.Content.TrimStart().StartsWith('/')) return null;
        var team = (await bots.ListAsync()).Where(b => b.Id != coordinator.Id).ToList();
        if (team.Count == 0) return null;
        using var usageScope = UsageScope.Begin("Routing", coordinator.Id, conv.Id);
        try
        {
            var roster = string.Join("\n", team.Select(b => $"- {b.Name}: {b.Title}. {Clip(b.Instructions.Split('.')[0], 120)}"));
            var recent = string.Join("\n", conv.Messages.TakeLast(6).SkipLast(1).Where(m => m.Content.Length > 0)
                .Select(m => $"{Speaker(m, [coordinator, .. team])}: {Clip(m.Content, 200)}"));
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    $"Plan how {coordinator.Name}, who leads a team of AI bots, should handle the user's new message. Decide how to handle it and reply with " +
                    "ONLY JSON: {\"mode\":\"self\"|\"one\"|\"team\",\"assign\":[{\"bot\":\"Name\",\"task\":\"…\"}],\"review\":true|false}\n" +
                    "- self: chat, quick questions, follow-ups about the conversation, things you can answer well in a few sentences. assign = [].\n" +
                    "- one: the whole request is clearly one specialist's job. Assign exactly one.\n" +
                    $"- team: it needs different kinds of work (facts then writing, code then review, research then a plan…). Assign 2–{Math.Max(2, settings.Teamwork.MaxSpecialists)} " +
                    "bots in the order they should work; later ones see earlier ones' results. Each task says exactly what to deliver, in one or two sentences.\n" +
                    "review: true when getting it right matters (facts, decisions, documents, code, plans), false for casual things."),
                LlmMessage.User($"Team:\n{roster}\n\nRecent messages:\n{recent}\n\nNew message from the user:\n{Clip(lastUser.Content, 1500)}"),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StraightRouteTimeout);
            // On the coordinator's own server and model, like its replies.
            var server = settings.ProviderFor(coordinator);
            var model = !string.IsNullOrWhiteSpace(coordinator.Model) ? coordinator.Model
                : server.Id != settings.ActiveProvider.Id ? await ModelPicker.DefaultForAsync(factory, server) ?? opts.Model : opts.Model;
            var planner = coordinator;
            // With automatic model choice on, planning is everyday work: the fast model.
            if (settings.ModelRouting is { Enabled: true, FastModel.Length: > 0 } fast && settings.Providers.Any(p => p.Id == (fast.FastProviderId ?? settings.ActiveProviderId)))
            {
                planner = new Bot { ProviderId = fast.FastProviderId ?? settings.ActiveProviderId };
                model = fast.FastModel;
            }
            await foreach (var c in factory.CreateForBot(settings, planner).StreamChatAsync(new LlmRequest(model, prompt, 0, 300, 4096, false), cts.Token))
                sb.Append(c.Content);
            return ParsePlan(sb.ToString(), team, settings.Teamwork);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Reads the planning step's JSON (tolerating code fences and chatter around it).</summary>
    public static TeamPlan? ParsePlan(string text, IReadOnlyList<Bot> team, TeamworkSettings settings)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(text[start..(end + 1)]) as JsonObject; } catch { return null; }
        if (o is null) return null;
        var mode = o["mode"]?.ToString().ToLowerInvariant();
        var assign = new List<(Bot, string)>();
        foreach (var a in o["assign"] as JsonArray ?? [])
        {
            var name = a?["bot"]?.ToString().Trim().TrimStart('@') ?? "";
            var bot = team.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var task = a?["task"]?.ToString().Trim() ?? "";
            if (bot is not null && task.Length > 0 && assign.All(x => x.Item1.Id != bot.Id)) assign.Add((bot, task));
        }
        if (mode == "self" || assign.Count == 0) return new TeamPlan([], false, null);
        if (mode == "one") assign = assign.Take(1).ToList();
        assign = assign.Take(Math.Max(1, settings.MaxSpecialists)).ToList();
        var review = settings.Review && o["review"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        // The reviewer: a bot whose job is reviewing (the Critic) that isn't already doing the work.
        var reviewer = team.FirstOrDefault(b => assign.All(x => x.Item1.Id != b.Id)
            && (b.Title.Contains("review", StringComparison.OrdinalIgnoreCase) || b.Name.Equals("Critic", StringComparison.OrdinalIgnoreCase)));
        return new TeamPlan(assign, review && reviewer is not null, reviewer);
    }

    /// <summary>Marks a reply as part of a team turn: <see cref="Behind"/> = the work behind the answer.</summary>
    public sealed record TeamPart(string Turn, bool Behind);

    /// <summary>The lead's quick reply before the team starts ("On it — I've got Researcher and Writer on this…").</summary>
    public static string AckText(TeamPlan plan)
    {
        var names = plan.Assign.Select(a => a.bot.Name).ToList();
        var who = names.Count switch { 1 => names[0], 2 => $"{names[0]} and {names[1]}", _ => string.Join(", ", names[..^1]) + " and " + names[^1] };
        if (names.Count == 1 && !plan.Review) return $"On it — I've asked {who} to take this. Back shortly.";
        return $"On it — I've got {who} on this" + (plan.Review ? $", and {plan.Reviewer!.Name} will check the work" : "") +
               ". I'll come back with the answer.";
    }

    /// <summary>
    /// The team at work. The coordinator replies at once ("on it"), the specialists work in order (each building on the
    /// last), the reviewer checks the result and the work is fixed once if needed, then the coordinator writes the
    /// answer. Everything between the quick reply and the answer is the work behind it: folded under the answer.
    /// One specialist with no review just answers.
    /// </summary>
    private async Task RunTeamAsync(Conversation conv, Bot coordinator, TeamPlan plan, SendOptions opts, Func<Task> onUpdate, CancellationToken ct)
    {
        var turn = Guid.NewGuid().ToString("N")[..12];
        var behind = new TeamPart(turn, true);
        var settings = await settingsStore.GetAsync();
        // Team members deliver within a few steps: a long wander keeps the user waiting on everyone after them.
        var roundLimit = Math.Min(settings.Tools.MaxRounds, 8);
        async Task SayAsync(string text, bool hidden, bool ack = false)
        {
            conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, BotId = coordinator.Id, Content = text, TeamTurn = turn, Behind = hidden, Ack = ack });
            await store.SaveAsync(conv);
            await onUpdate();
        }
        // A step that failed shows its error instead of disappearing into the folded work.
        bool Failed(ChatMessage? r) { if (r is { Error: not null }) r.Behind = false; return r is null || r.Error is not null; }

        var solo = plan.Assign.Count == 1 && !plan.Review;
        await SayAsync(AckText(plan), hidden: false, ack: true);
        var handout = string.Join("\n", plan.Assign.Select(a => $"@{a.bot.Name}: {a.task}"));
        await SayAsync(handout, hidden: true);
        var before = conv.Messages.Count;
        await JoinMentionedBotsAsync(conv, handout + (plan.Review ? $" @{plan.Reviewer!.Name}" : ""), coordinator, onUpdate);
        // "Chief added Researcher…" belongs with the work, not between the hand-out and the rest of it.
        foreach (var notice in conv.Messages.Skip(before).Where(x => x.TeamTurn is null)) { notice.TeamTurn = turn; notice.Behind = true; }

        var worked = new List<Bot>();
        foreach (var (bot, _) in plan.Assign)
        {
            if (ct.IsCancellationRequested) return;
            var reply = await RunAgentAsync(conv, bot, opts, onUpdate, ct, autoTitle: false,
                extraInstruction: $"{coordinator.Name} gave you a task above (\"@{bot.Name}: …\"). Do just that, using what teammates " +
                                  "before you found. Be efficient: a few well-chosen tool calls, then deliver the result itself (not a " +
                                  "description of what you'll do). Don't hand work to anyone else or @mention anyone." +
                                  (solo ? " Your reply goes straight to the user." : ""),
                team: solo ? new TeamPart(turn, false) : behind, roundLimit: roundLimit);
            if (Failed(reply)) return;
            worked.Add(bot);
        }
        if (solo) return;

        if (plan.Review && plan.Reviewer is { } reviewer && !ct.IsCancellationRequested)
        {
            await SayAsync($"@{reviewer.Name}, please check the work above against what the user asked.", hidden: true);
            var review = await RunAgentAsync(conv, reviewer, opts, onUpdate, ct, autoTitle: false,
                extraInstruction: "Review your teammates' work above against the user's request: wrong or unsupported facts, gaps, weak " +
                                  "reasoning, anything that doesn't answer what was asked. List the problems and say who should fix what, " +
                                  "naming them with @Name. If it's solid, reply only: Looks good.",
                team: behind, roundLimit: Math.Min(roundLimit, 4));
            if (Failed(review)) return;
            if (!LooksGood(review!.Content))
            {
                var fixers = Mentions(review.Content, worked, includeEveryone: false);
                if (fixers.Count == 0) fixers = [worked[^1]];
                foreach (var fixer in fixers.Take(2))
                {
                    if (ct.IsCancellationRequested) return;
                    await SayAsync($"@{fixer.Name}, please fix what {reviewer.Name} raised.", hidden: true);
                    var fix = await RunAgentAsync(conv, fixer, opts, onUpdate, ct, autoTitle: false,
                        extraInstruction: $"{reviewer.Name} reviewed the work above. Fix the problems that are yours: give the corrected " +
                                          "result in full (not just the changes). Don't @mention anyone.",
                        team: behind, roundLimit: roundLimit);
                    if (Failed(fix)) return;
                }
            }
        }

        if (ct.IsCancellationRequested) return;
        await RunAgentAsync(conv, coordinator, opts, onUpdate, ct, autoTitle: false,
            extraInstruction: "The team's work is in (above). Now write the final answer for the user: one clear, complete answer that " +
                              "combines their results and includes the fixes from the review. Don't hand off again, don't @mention " +
                              "anyone, don't say \"on it\" again and don't describe who did what — just give the answer.",
            team: new TeamPart(turn, false), roundLimit: Math.Min(roundLimit, 3));
    }

    /// <summary>A review that found nothing to fix.</summary>
    public static bool LooksGood(string review)
    {
        var t = review.Trim().TrimStart('*', '_', '#', ' ', '✅').Trim();
        return t.StartsWith("Looks good", StringComparison.OrdinalIgnoreCase) && t.Length < 400
               || t.Length < 60 && (t.StartsWith("LGTM", StringComparison.OrdinalIgnoreCase) || t.StartsWith("No issues", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No @mention: ask the model which member(s) should answer.</summary>
    private async Task<List<Bot>> RouteAsync(Conversation conv, List<Bot> members, string message, string model, CancellationToken ct,
        bool straightToSpecialist = false)
    {
        using var usageScope = UsageScope.Begin("Routing", null, conv.Id);
        var fallback = members.FirstOrDefault(b => b.IsCoordinator) ?? members[0];
        try
        {
            var settings = await settingsStore.GetAsync();
            var roster = string.Join("\n", members.Select(b => $"- {b.Name}: {b.Title}{(b.IsCoordinator ? " (coordinator)" : "")}"));
            var recent = string.Join("\n", conv.Messages.TakeLast(6).SkipLast(1)
                .Where(m => m.Content.Length > 0)
                .Select(m => $"{Speaker(m, members)}: {Clip(m.Content, 200)}"));
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(straightToSpecialist
                    ? "You route a user's message to a team of AI bots. Reply with ONLY one bot's name. Pick a specialist " +
                      "only when the whole request is clearly that one specialist's job (e.g. research a topic, write code, " +
                      "edit a text). Otherwise — chat, quick questions, follow-ups to the conversation, requests with several " +
                      "parts or that need several specialists, anything unclear — pick the coordinator."
                    : "You route messages in a group chat of AI bots. Reply with ONLY the names of the bots that " +
                      "should answer, comma-separated. Usually pick exactly one: the best fit for the request. Pick the " +
                      "coordinator for broad or multi-part requests. Pick several only if the user clearly addresses several."),
                LlmMessage.User($"Bots:\n{roster}\n\nRecent messages:\n{recent}\n\nNew message from the user:\n{Clip(message, 800)}\n\nWho should answer?"),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Skipping the coordinator is only worth it when the check is quick; otherwise let the coordinator answer.
            cts.CancelAfter(straightToSpecialist ? StraightRouteTimeout : TimeSpan.FromSeconds(45));
            await foreach (var c in factory.CreateForChat(settings)
                               .StreamChatAsync(new LlmRequest(model, prompt, 0, 40, 4096, false), cts.Token))
                sb.Append(c.Content);
            var answer = sb.ToString();
            var picked = members.Where(b => answer.Contains(b.Name, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            return picked.Count > 0 ? picked : [fallback];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return [fallback];
        }
    }

    // ---------------------------------------------------------------- one agent reply

    /// <summary>Generates one assistant reply (as <paramref name="bot"/> when given) and saves the conversation.</summary>
    public async Task<ChatMessage?> RunAgentAsync(Conversation conv, Bot? bot, SendOptions opts, Func<Task> onUpdate,
        CancellationToken ct, bool autoTitle = true, int depth = 0, bool unattended = false, IReadOnlyList<ITool>? extraTools = null, int? historyWindow = null,
        string? untrustedInput = null, bool learn = true, string? extraInstruction = null, TeamPart? team = null, int? roundLimit = null)
    {
        var settings = await settingsStore.GetAsync();
        // The work behind earlier team answers stays out: the answers carry it. This turn's work is kept.
        var lastUserIndex = conv.Messages.FindLastIndex(m => m.Role == ChatRole.User);
        var history = conv.Messages.Where((m, i) => !m.Behind || i > lastUserIndex).ToList();
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User)
            ?? throw new InvalidOperationException("No user message to reply to.");
        // Automatic model choice: everyday messages to the fast model, hard work to the strong one.
        var route = ModelRouter.Pick(settings, bot, lastUser.Content, lastUser.Images.Count + lastUser.AttachedFiles.Count,
            CodeTools.SessionFor(conv, bot) is not null);
        var routedBot = route is { } rt ? (bot is null ? new Bot { ProviderId = rt.provider.Id } : bot.WithVersion(new BotVersion { ProviderId = rt.provider.Id, Model = rt.model })) : bot;
        var client = factory.CreateForBot(settings, routedBot);
        // A bot on its own server uses its model, or that server's default — not the model picked for the chat.
        var ownServer = settings.ProviderFor(routedBot).Id != settings.ActiveProvider.Id;
        var model = route?.model ?? (!string.IsNullOrWhiteSpace(bot?.Model) ? bot.Model
            : ownServer ? await ModelPicker.DefaultForAsync(factory, settings.ProviderFor(bot)) ?? opts.Model
            : opts.Model);
        untrustedInput ??= lastUser.UntrustedSource; // e.g. a web page attached with @https://…
        var allBots = bot is null ? [] : await bots.ListAsync();
        var members = conv.BotIds.Select(id => allBots.FirstOrDefault(b => b.Id == id)).OfType<Bot>().ToList();
        using var usageScope = UsageScope.Begin(lastUser.RoutineName switch
        {
            "Heartbeat" => "Heartbeat",
            "Task" => "Task",
            "Bot check" => "Bot check",
            "Helper" => "Helpers",
            "API" => "API",
            not null => "Routine",
            _ when depth > 0 => "Bot handoff",
            _ when untrustedInput?.StartsWith("an email") == true => "Bot inbox",
            _ => "Chat",
        }, bot?.Id, conv.Id);
        // Chats you're waiting on go ahead of background work on a local model server.
        var server = settings.ProviderFor(routedBot);
        if (depth == 0 && IsBackgroundRun(lastUser, untrustedInput)) ModelQueue.MarkBackground();
        using var chatRunning = ModelQueue.InBackground ? null : ModelQueue.ChatRunning(server);

        var reply = new ChatMessage { Role = ChatRole.Assistant, Model = model, IsStreaming = true, BotId = bot?.Id, TeamTurn = team?.Turn, Behind = team?.Behind ?? false };
        var replyTimer = System.Diagnostics.Stopwatch.StartNew();
        long? firstWordsMs = null;
        var checkRan = false;
        if (route is { } chosen) reply.Trace.Add(new TraceStep("route", chosen.reason, 0));
        conv.Messages.Add(reply);
        conv.Model = opts.Model;
        await onUpdate();

        try
        {
            var available = opts.UseTools ? (await tools.GetToolsAsync(settings, ct)).ToList() : [];
            if (bot is not null)
            {
                available = bot.ToolMode switch
                {
                    BotToolMode.None => [],
                    BotToolMode.Selected => available.Where(t => bot.AllowedTools.Contains(t.Name)).ToList(),
                    _ => available.Where(t => bot.Abilities.Allows(t.Name, bot.Memory)).ToList(),
                };
                var others = allBots.Where(b => b.Id != bot.Id).ToList();
                if (opts.UseTools && others.Count > 0 && depth < MaxBotCallDepth && bot.ToolMode != BotToolMode.None
                    && bot.Abilities.MessageBots)
                    available.Add(new MessageBotTool(this, bot, others, opts with { DeepSearch = false }));
            }
            // Helpers: the replying bot (or Locus) can split work across temporary helper agents. Not for helpers themselves.
            if (opts.UseTools && depth == 0 && settings.Tools.MaxHelpers > 0 && bot?.ToolMode != BotToolMode.None)
                available.Add(new DelegateTool(this, bot, opts with { DeepSearch = false }));
            // Coding (Claude Code-style tools scoped to one folder): the chat's code session, or — for a bot with the
            // Code ability — its own project folder, so its chats and work other bots hand it get them too.
            var codeSession = CodeTools.SessionFor(conv, bot);
            if (opts.UseTools && (bot is null || bot is { ToolMode: not BotToolMode.None, Abilities.Code: true }))
                foreach (var t in codeTools.For(codeSession))
                    if (available.All(a => a.Name != t.Name)) available.Add(t);
            available = available.Where(t => !PermissionCatalog.IsBlocked(t, settings)).ToList();
            // Code sessions keep their own task list (todo_write).
            if (codeSession is not null) available.RemoveAll(t => t.Name == UpdatePlanTool.ToolName);
            if (extraTools is not null) available.AddRange(extraTools.Where(t => available.All(a => a.Name != t.Name)));
            var byName = available.ToDictionary(t => t.Name);
            // Offer only the tools this message is likely to need (any available tool still runs if called by name).
            var offered = available;
            List<ToolDefinition>? liveDefinitions = null;
            if (settings.Tools.PickToolsPerMessage && opts.UseTools && bot?.ToolMode != BotToolMode.Selected)
            {
                var recentText = string.Join("\n", history.Where(m => m.Role == ChatRole.User).TakeLast(3).Select(m => m.Content));
                var used = history.Where(m => m.Role == ChatRole.Assistant).TakeLast(6)
                    .SelectMany(m => m.ToolCalls).Select(t => t.Name).ToHashSet();
                var always = (extraTools ?? []).Select(t => t.Name)
                    .Concat(codeSession is null ? [] : codeTools.For(codeSession).Select(t => t.Name)).ToHashSet();
                var (picked, notLoaded) = ToolSelector.Select(available, recentText, used, always);
                offered = picked;
                if (notLoaded.Count > 0)
                {
                    var loader = new LoadToolsTool(notLoaded, group =>
                    {
                        var g = notLoaded.FirstOrDefault(x => x.Name == group);
                        if (g is null || liveDefinitions is null) return [];
                        var add = available.Where(t => g.Contains(t.Name) && liveDefinitions.All(d => d.Name != t.Name)).ToList();
                        liveDefinitions.AddRange(add.Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)));
                        return add.Select(t => t.Name).ToList();
                    });
                    offered = [.. offered, loader];
                    byName[loader.Name] = loader;
                }
            }

            var system = await BuildSystemPromptAsync(conv, bot, members, allBots, settings, byName, opts, unattended, codeSession);
            if (extraInstruction is not null) system.AppendLine().AppendLine().AppendLine("RIGHT NOW: " + extraInstruction);

            if (opts.DeepSearch && !byName.ContainsKey("web_search") && !string.IsNullOrWhiteSpace(settings.SearxngUrl))
            {
                // Model has no tools: search once up front and inject results.
                reply.Thinking = "Searching the web…";
                await onUpdate();
                reply.Sources = await search.SearchAsync(settings.SearxngUrl, lastUser.Content, settings.SearchResultCount, ct);
                reply.Thinking = null;
                if (reply.Sources.Count > 0)
                {
                    system.AppendLine().AppendLine()
                        .AppendLine("Web search results for the user's latest message (cite them as [1], [2], … where relevant):");
                    for (var i = 0; i < reply.Sources.Count; i++)
                        system.AppendLine($"[{i + 1}] {reply.Sources[i].Title} — {reply.Sources[i].Url}\n{reply.Sources[i].Snippet}");
                }
            }

            // On a phone call: speak briefly and plainly (and, on a call a bot placed, who it's talking to).
            if (Phone.PhoneService.IsPhoneTurn(lastUser.RoutineName))
                system.Append(Phone.PhoneService.Guide(conv));
            var skills = await skillStore.ActiveAsync();
            if (byName.ContainsKey("use_skill") && skills.Count > 0)
            {
                system.AppendLine().AppendLine().AppendLine("Saved skills (load one with use_skill when it fits the task — check here before working something out from scratch):");
                foreach (var sk in skills)
                    system.AppendLine($"- {sk.Name}: {sk.Description}{(sk.Uses > 0 ? $" (used {sk.Uses}×)" : "")}");
            }
            if (byName.ContainsKey("search_past_chats"))
                system.AppendLine().AppendLine(
                    "When the user refers to something from an earlier conversation, look it up with search_past_chats instead of guessing.");
            if (byName.Count > 0)
                system.AppendLine().AppendLine(
                    "Use tools for what you can't know yourself (current facts, the user's own files, email, calendar) or to do " +
                    "something. Explanations, comparisons and advice you can give from what you know: answer directly, without " +
                    "searching past chats or looking through files unless the user points to them. Put answers in the chat, not " +
                    "in a file, unless the user asks for a file.");
            if (byName.ContainsKey("save_skill"))
                system.AppendLine().AppendLine(
                    "Learn as you go: when you finish a multi-step task you'd likely be asked to repeat, save the method with " +
                    "save_skill. When a skill's steps were wrong or you found a better way, fix it with improve_skill.");

            var baseMessages = new List<LlmMessage> { LlmMessage.System(system.ToString()) };
            // Long-running tasks pass a window so their history doesn't outgrow the model's context.
            var window = historyWindow is > 0 && history.Count > historyWindow ? history.TakeLast(historyWindow.Value).ToList() : history;
            // Fit the model's context window: tool schemas and the system prompt first, then as much history as fits
            // (summarising the oldest part of long chats).
            var budget = ContextWindow.BudgetFor(server, settings);
            var toolTokens = offered.Sum(t => ContextWindow.Tokens(t.Name + t.Description + t.Parameters.ToJsonString()) + 10);
            var historyRoom = budget - toolTokens - ContextWindow.Tokens(system.ToString());
            baseMessages.AddRange(await FitHistoryAsync(conv, window, bot, allBots, skills, client, model, historyRoom, reply, onUpdate, ct));
            // Long-term memories that match this message ride along with it rather than in the system prompt, so the
            // start of the prompt stays the same from message to message (local servers reuse it).
            if (settings.MemoryEnabled && baseMessages.FindLastIndex(m => m.Role == "user") is var ask and >= 0
                && await RelevantMemoriesAsync(bot, lastUser.Content, Math.Max(5, bot?.Memory.MaxInPrompt ?? 60)) is { Count: > 0 } recalled)
            {
                var m = baseMessages[ask];
                baseMessages[ask] = new LlmMessage
                {
                    Role = "user", Images = m.Images,
                    Content = m.Content + "\n\n[From long-term memory, possibly relevant:\n" + string.Join("\n", recalled.Select(r => "- " + r.Text)) + "]",
                };
            }

            var ctx = new ToolContext
            {
                Conversation = conv, Reply = reply, Settings = settings, Client = client, Model = model,
                Bot = bot, Depth = depth, Unattended = unattended, TaintedBy = untrustedInput, Tools = byName, Code = codeSession,
            };
            var definitions = liveDefinitions = offered.Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList();
            var content = new StringBuilder();
            var thinking = new StringBuilder();
            var maxRounds = Math.Max(1, byName.ContainsKey("todo_write")
                ? Math.Max(settings.Tools.MaxRounds, settings.Tools.CodeMaxRounds)
                : settings.Tools.MaxRounds);
            if (roundLimit is > 0) maxRounds = Math.Min(maxRounds, roundLimit.Value);

            // See the last round below. Turned off for the rest of the reply if the server ignores it.
            var listToolsOnLastStep = server.Kind == ProviderKind.OpenAICompatible && !IgnoresNoToolCalls.ContainsKey(server.Id);
            // Replies cut off at the length limit are continued (Settings → "Continue cut-off replies").
            var continuing = false;
            var continuations = 0;
            // A step that hit the length limit with nothing to show: the server dropped an unfinished tool call
            // (usually a long answer being written into a file). Say so, rather than "continue where you stopped".
            var lostStep = false;
            for (var round = 0; round < maxRounds; round++)
            {
                var room = budget - toolTokens;
                var messages = baseMessages.Concat(ToolRounds(reply)).ToList();
                if (ContextWindow.Tokens(messages) is var used && used > room)
                {
                    ContextWindow.ShortenOlderResults(reply.ToolCalls, used - (int)(room * ContextWindow.RefitShare));
                    messages = baseMessages.Concat(ToolRounds(reply)).ToList();
                }
                messages = ContextWindow.FitToolResults(messages, room);
                // Last round: withhold tools so the model has to answer.
                var lastRound = round >= maxRounds - 1;
                // Last round the model has to answer. Servers that support it keep the tools listed with calls switched
                // off: the tools are at the start of the prompt, and dropping them would make a local server read the
                // whole (long, by now) prompt again before answering.
                var keepListed = lastRound && listToolsOnLastStep;
                // A copy: load_tools can add to the list later, and each request should keep what it was sent.
                var offer = definitions.Count > 0 && (!lastRound || keepListed) && !continuing ? definitions.ToList() : null;
                if (continuing)
                {
                    messages.Add(new LlmMessage { Role = "assistant", Content = content.ToString() });
                    messages.Add(LlmMessage.User("[Locus] Your reply was cut off by the length limit. Continue exactly where it " +
                        "stopped, mid-sentence if needed. Don't repeat or restate anything already written."));
                }
                if (lostStep)
                {
                    messages.Add(LlmMessage.User(LostStepPrompt(settings.MaxTokens)));
                    lostStep = false;
                }
                // Without being told, local models answer a tool-less last round with yet another call written as text.
                if (lastRound && definitions.Count > 0 && reply.ToolCalls.Count > 0)
                    messages.Add(LlmMessage.User("[Locus] That's all the tool steps for this reply. Don't call any more tools: " +
                        "answer now from the results above, and say briefly what you couldn't check."));
                var request = new LlmRequest(model, messages, settings.Temperature, settings.MaxTokens,
                    settings.ContextLength, opts.Think, offer) { NoToolCalls = lastRound && offer is not null };

                if (content.Length > 0 && !continuing && !content.ToString().EndsWith("\n\n")) content.Append("\n\n");
                continuing = false;
                var cutOff = false;
                List<LlmToolCall> calls = [];
                var roundStart = content.Length;
                string? providerState = null;
                if (ModelQueue.InBackground && ModelQueue.ChatsOn(server.Id) > 0)
                {
                    reply.Notice = $"Waiting while a chat uses {server.Name}…";
                    await onUpdate();
                    await ModelQueue.WaitForChatsAsync(server, ct);
                    reply.Notice = null;
                }
                var roundTimer = System.Diagnostics.Stopwatch.StartNew();
                LlmStats? roundStats = null;
                // A busy model server (another chat or a background task using it) can take minutes to start
                // answering: say so instead of just spinning.
                var firstChunk = new TaskCompletionSource();
                var others = Interlocked.Increment(ref _modelCallsInFlight) - 1;
                string? waitingNotice = null;
                var waiting = Task.Run(async () =>
                {
                    if (await Task.WhenAny(firstChunk.Task, Task.Delay(WaitingNoticeAfter, ct).ContinueWith(_ => { }, TaskScheduler.Default)) == firstChunk.Task || ct.IsCancellationRequested) return;
                    var busy = Math.Max(others, Volatile.Read(ref _modelCallsInFlight) - 1);
                    reply.Notice = waitingNotice = busy > 0
                        ? $"⏳ Waiting for the model: it's busy with {busy} other {(busy == 1 ? "chat or task" : "chats or tasks")}. Your reply starts when it's free."
                        : "⏳ Waiting for the model to start answering…";
                    await onUpdate();
                }, CancellationToken.None);
                try
                {
                await foreach (var chunk in client.StreamChatAsync(request, ct))
                {
                    if (!firstChunk.Task.IsCompleted)
                    {
                        firstChunk.TrySetResult();
                        await waiting;
                        if (waitingNotice is not null && reply.Notice == waitingNotice) reply.Notice = null;
                    }
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        firstWordsMs ??= replyTimer.ElapsedMilliseconds;
                        content.Append(chunk.Content);
                    }
                    if (!string.IsNullOrEmpty(chunk.Thinking)) thinking.Append(chunk.Thinking);
                    if (chunk.Stats is not null) reply.Stats = roundStats = chunk.Stats;
                    if (chunk.ToolCalls is not null) calls.AddRange(chunk.ToolCalls);
                    if (chunk.Notice is not null) reply.Notice = chunk.Notice;
                    if (chunk.Notice?.StartsWith(CutOffPrefix) == true) cutOff = true;
                    if (chunk.ProviderState is not null) providerState = chunk.ProviderState;
                    reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();
                    reply.Thinking = thinking.Length > 0 ? thinking.ToString().Trim() : null;
                    await onUpdate();
                }
                }
                finally
                {
                    Interlocked.Decrement(ref _modelCallsInFlight);
                    firstChunk.TrySetResult();
                    await waiting;
                    if (waitingNotice is not null && reply.Notice == waitingNotice) reply.Notice = null;
                }

                reply.Trace.Add(new TraceStep("model", model, roundTimer.ElapsedMilliseconds, roundStats?.PromptTokens, roundStats?.CompletionTokens));
                if (calls.Count == 0 && !lastRound && offer is not null)
                {
                    // Some local models write the calls as text; run them instead of ending with them printed.
                    var (written, rest) = TextToolCalls.Find(content.ToString(roundStart, content.Length - roundStart), byName.Keys.ToHashSet());
                    if (written.Count > 0)
                    {
                        calls.AddRange(written);
                        content.Length = roundStart;
                        content.Append(rest);
                        reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();
                        await onUpdate();
                    }
                }
                if (lastRound && keepListed && offer is not null && calls.Count > 0)
                {
                    // The server ignored "no tool calls" (oMLX does): ask again without the tools, which forces an answer.
                    listToolsOnLastStep = false;
                    IgnoresNoToolCalls[server.Id] = true;
                    content.Length = roundStart;
                    while (content.Length > 0 && char.IsWhiteSpace(content[^1])) content.Length--;
                    reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();
                    round--;
                    continue;
                }
                if (lastRound && calls.Count > 0)
                {
                    // Out of tool steps: calls the server sent anyway don't run.
                    calls.Clear();
                    reply.Notice ??= $"This reply used all {maxRounds} tool steps before finishing. Ask it to continue, or raise \"Max tool rounds per reply\" under Tools.";
                }
                if (lastRound && definitions.Count > 0)
                {
                    // Out of tool steps but it still wrote a call: don't show the call as the answer.
                    var (late, rest) = TextToolCalls.Find(content.ToString(roundStart, content.Length - roundStart), byName.Keys.ToHashSet());
                    if (late.Count > 0)
                    {
                        content.Length = roundStart;
                        content.Append(rest);
                        reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();
                        reply.Notice ??= $"This reply used all {maxRounds} tool steps before finishing. Ask it to continue, or raise \"Max tool rounds per reply\" under Tools.";
                        await onUpdate();
                    }
                }
                if (calls.Count == 0 && cutOff && settings.ContinueCutOffReplies && continuations < MaxContinuations && !ct.IsCancellationRequested)
                {
                    continuations++;
                    if (content.ToString(roundStart, content.Length - roundStart).Trim().Length == 0)
                    {
                        lostStep = true;
                        content.Length = roundStart;
                        while (content.Length > 0 && char.IsWhiteSpace(content[^1])) content.Length--;
                        reply.Trace.Add(new TraceStep("note", "step hit the length limit with nothing to show; retried", 0));
                    }
                    else continuing = true;
                    reply.Notice = null;
                    round--; // continuing isn't a tool step
                    continue;
                }
                if (calls.Count == 0) break;

                // Text in a round that ends with tool calls is work in progress ("Let me search…", or a draft answer
                // the model then decides to check). It goes with the steps, so the answer is only the final round's
                // text instead of several drafts stacked up.
                var preface = content.ToString(roundStart, content.Length - roundStart).Trim();
                // A coordinator that has just handed the work over ("@Researcher please…") stops there: the teammate
                // does it next, instead of the coordinator also doing it with its own tools first.
                if (bot is { IsCoordinator: true } && calls.All(c => c.Name != "message_bot")
                    && allBots.Any(b => b.Id != bot.Id && preface.Contains("@" + b.Name, StringComparison.OrdinalIgnoreCase)))
                    break;
                content.Length = roundStart;
                while (content.Length > 0 && char.IsWhiteSpace(content[^1])) content.Length--;
                reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();

                var batch = calls.Select(call => new ToolCall
                {
                    Id = call.Id, Name = call.Name, ArgumentsJson = call.ArgumentsJson, Round = round,
                    ProviderState = call == calls[0] ? providerState : null,
                    Preface = call == calls[0] && preface.Length > 0 ? preface : null,
                }).ToList();
                if (CanRunInParallel(batch))
                {
                    // Several teammates asked in one turn work at the same time.
                    reply.ToolCalls.AddRange(batch);
                    await Task.WhenAll(batch.Select(tc => RunToolAsync(tc, byName.GetValueOrDefault(tc.Name), ctx, onUpdate, ct)));
                }
                else
                    foreach (var tc in batch)
                    {
                        reply.ToolCalls.Add(tc);
                        await RunToolAsync(tc, byName.GetValueOrDefault(tc.Name), ctx, onUpdate, ct);
                    }
            }

            // Check before answering: after research, the model re-reads its answer against the tool results and fixes
            // anything they don't support.
            if (ShouldCheck(settings.CheckAnswers, reply) && !ct.IsCancellationRequested)
                checkRan = await CheckAnswerAsync(reply, baseMessages, definitions, budget - toolTokens, client, model, settings, opts, bot, onUpdate, ct);
        }
        catch (OperationCanceledException)
        {
            foreach (var tc in reply.ToolCalls.Where(t => t.Status is ToolCallStatus.Running or ToolCallStatus.AwaitingApproval or ToolCallStatus.Pending))
            {
                tc.Status = ToolCallStatus.Error;
                tc.Result = "Cancelled.";
            }
            if (reply.Content.Length == 0) reply.Content = "_(stopped)_";
        }
        catch (Exception ex)
        {
            reply.Error = ex.InnerException is { } inner && ex is HttpRequestException
                ? $"{ex.Message} ({inner.Message})"
                : ex.Message;
        }
        finally
        {
            reply.IsStreaming = false;
            conv.UpdatedAt = DateTimeOffset.Now;
            var failed = reply.Error is not null && reply.Content.Length == 0 && reply.ToolCalls.Count == 0;
            if (failed) conv.Messages.Remove(reply);
            await store.SaveAsync(conv);
            if (failed) conv.Messages.Add(reply); // show the error, but don't persist it
            await onUpdate();
            _ = usage.RecordReplyAsync(new ReplyRecord(DateTimeOffset.Now, settings.ProviderFor(routedBot).Name, model, bot?.Id, conv.Id,
                UsageScope.Active?.Source ?? "Chat", replyTimer.ElapsedMilliseconds, firstWordsMs, reply.Trace.Count(t => t.Kind == "model" && !t.Name.StartsWith("check: ") && !t.Name.StartsWith("revise: ")),
                reply.ToolCalls.Count, checkRan, reply.Checked is not null, reply.Error is not null));
        }

        // Helpers count too: delegated bots often do the real multi-step work.
        var reflect = learn && reply.Error is null && reply.Content.Length > 0 && settings.SkillLearning.Enabled
                      && lastUser.RoutineName != "Heartbeat" // routine check-ins aren't methods worth keeping
                      && (bot is null || bot.Abilities.Skills) && reply.SkillNotes.Count == 0
                      && reply.ToolCalls.Count(t => t.Status == ToolCallStatus.Done) >= Math.Max(2, settings.SkillLearning.MinToolCalls);
        if (reflect)
            _ = Task.Run(() => ReflectAsync(conv, bot, reply, client, model, settings, onUpdate));

        var learnMemory = learn && reply.Error is null && reply.Content.Length > 0 && depth == 0 && settings.MemoryEnabled
                    && (bot is null ? settings.MemoryAutoLearn : bot.Memory is { Enabled: true, AutoLearn: true });
        if (learnMemory)
            _ = Task.Run(() => LearnAsync(conv, bot, reply, client, model, onUpdate));

        return reply;
    }

    /// <summary>Instructions for a code session: how to work in the folder (like Claude Code), the folder's own
    /// instruction file (CLAUDE.md / AGENTS.md…), the git state and the current task list.</summary>
    public static async Task<string> CodeSessionGuideAsync(CodeSession code, bool botFolder = false)
    {
        var sb = new StringBuilder()
            .Append(botFolder
                ? "PROJECT FOLDER. Your project is the folder " + code.Directory + " (paths are relative to it). When a request is about that code, work as a coding agent"
                : "CODE SESSION. You are working as a coding agent in the folder " + code.Directory + " (paths are relative to it)")
            .Append(". Work like a careful senior engineer:\n")
            .Append("- Explore before changing: find files with glob, search with grep, read files with read. Don't guess at code you haven't read.\n")
            .Append("- For work with 3 or more steps, keep a task list with todo_write and update it as you go (one item in_progress at a time).\n")
            .Append("- Make small, exact changes with edit (write only for new files or full rewrites). Follow the project's existing style and conventions.\n")
            .Append("- Check your work: build, run the tests or the linter with bash, and fix what fails. Don't claim something works if you didn't check it.\n")
            .Append("- Don't commit, push, delete files or install things unless the user asked for it.\n")
            .Append("- Keep replies short: say what you changed (file paths) and what you verified, or what's left. No long recaps.");
        sb.Append(code.Mode switch
        {
            CodePermissionMode.Plan => "\n\nPLAN MODE: you can only read. Investigate the code, then reply with a concise, concrete plan " +
                                       "(which files change and how, and how you'll verify it). Don't try to change anything; the user switches " +
                                       "modes when they approve the plan.",
            CodePermissionMode.AcceptEdits => "\n\nThe user accepts file edits without reviewing each one; commands may still ask.",
            _ => "\n\nThe user reviews each edit and command before it runs.",
        });

        foreach (var name in ProjectContext.ProjectFiles)
        {
            var path = Path.Combine(code.Directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length > 200_000) continue;
            var text = (await File.ReadAllTextAsync(path)).Trim();
            if (text.Length == 0) continue;
            if (text.Length > ProjectContext.MaxCharsPerFile) text = text[..ProjectContext.MaxCharsPerFile] + "\n… (cut; read the whole file with read)";
            sb.Append($"\n\nProject instructions ({name} — follow them):\n").Append(text);
            break;
        }

        if (GitSummary(code.Directory) is { } git) sb.Append("\n\n").Append(git);

        if (code.Todos.Count > 0)
        {
            sb.Append("\n\nYour task list so far:");
            foreach (var t in code.Todos)
                sb.Append("\n- [").Append(t.Status switch { CodeTodoStatus.Completed => "x", CodeTodoStatus.InProgress => "~", _ => " " }).Append("] ").Append(t.Content);
        }
        return sb.ToString();
    }

    /// <summary>The branch, from .git/HEAD (no process needed). Null when the folder isn't a git checkout.</summary>
    private static string? GitSummary(string dir)
    {
        try
        {
            var head = Path.Combine(dir, ".git", "HEAD");
            if (!File.Exists(head)) return Directory.Exists(Path.Combine(dir, ".git")) ? "This folder is a git repository." : null;
            var text = File.ReadAllText(head).Trim();
            return text.StartsWith("ref: refs/heads/")
                ? $"This folder is a git repository on branch {text["ref: refs/heads/".Length..]} (use bash for git status/diff)."
                : "This folder is a git repository (detached HEAD).";
        }
        catch (IOException) { return null; }
    }

    /// <summary>How to take initiative (Settings → "Take initiative"): act on clear next steps, come back later on your
    /// own, and hand big jobs to a background task.</summary>
    /// <summary>
    /// Coordinators used to research and answer first, then @mention a specialist to do it again. Delegating is
    /// their first move, not their last.
    /// </summary>
    public const string CoordinatorGuide =
        "As the coordinator, delegate first. When a request (or part of it) is a specialist's job — research, code, " +
        "writing, review — your whole reply is a short hand-off: one line saying who you're asking and why, with the " +
        "@mention and everything they need, e.g. \"Let me get Researcher on this. @Researcher please research…\". " +
        "Don't use tools for it, don't answer it yourself first and don't give a preliminary answer. Do it yourself only " +
        "when it's quick (a fact you know, a calculation, a date). When the specialists' results are in, reply once with " +
        "the combined answer for the user.";

    /// <summary>The memories listed in every prompt: not yet in long-term storage, newest <paramref name="max"/>, oldest first.</summary>
    public static List<Memory> RecentMemories(IReadOnlyList<Memory> all, int max) =>
        all.Where(m => !m.Archived).OrderByDescending(m => m.CreatedAt).Take(max).Reverse().ToList();

    /// <summary>After a step that hit the length limit and produced nothing (a lost, unfinished tool call).</summary>
    public static string LostStepPrompt(int maxTokens) =>
        $"[Locus] Your last step reached the length limit ({maxTokens:N0} tokens) before anything came through — most likely a " +
        "tool call carrying a lot of text, which was lost. Write your answer to the user directly in the chat. If it really " +
        "has to go into a file, write the file in smaller parts (several calls, appending).";

    /// <summary>How a cut-off notice starts (see OpenAICompatibleClient.CutOffNotice).</summary>
    private const string CutOffPrefix = "The reply was cut off";
    /// <summary>How many times a cut-off reply is continued before stopping with the notice.</summary>
    public const int MaxContinuations = 2;

    /// <summary>How many long-term memories may ride along with a message.</summary>
    public const int RecalledPerMessage = 6;

    /// <summary>Memories not listed in the prompt (long-term, or past the recent list) that match the user's message.</summary>
    private async Task<List<Memory>> RelevantMemoriesAsync(Bot? bot, string message, int max)
    {
        if (string.IsNullOrWhiteSpace(message)) return [];
        var found = new List<Memory>();
        var scopes = new List<string?>();
        if (bot is null || bot.Memory.UseSharedMemory) scopes.Add(null);
        if (bot is { Memory.Enabled: true }) scopes.Add(bot.Id);
        foreach (var scope in scopes)
        {
            var all = await memories.ListAsync(scope);
            if (all.Count == 0) continue;
            var listed = RecentMemories(all, max).Select(m => m.Id).ToHashSet();
            if (listed.Count == all.Count) continue;
            found.AddRange((await memories.SearchAsync(message, scope, 20)).Where(m => !listed.Contains(m.Id)));
        }
        return found.DistinctBy(m => m.Text).Take(RecalledPerMessage).ToList();
    }

    /// <summary>Tasks, routines, heartbeat, bot checks and the bot inbox: nobody is waiting on the reply.</summary>
    public static bool IsBackgroundRun(ChatMessage lastUser, string? untrustedInput) =>
        lastUser.RoutineName is not null and not ("Helper" or "API") && !Phone.PhoneService.IsPhoneTurn(lastUser.RoutineName)
        || untrustedInput?.StartsWith("an email") == true;

    /// <summary>
    /// What each teammate runs on and its other versions, so a coordinator can pick a stronger, faster or more private
    /// model for a problem. Null when no teammate has versions.
    /// </summary>
    public static string? TeamModels(Bot self, IReadOnlyList<Bot> allBots, AppSettings settings)
    {
        var team = allBots.Where(b => b.Id != self.Id).ToList();
        if (team.All(b => b.Versions.Count == 0)) return null;
        string On(string? providerId, string? model)
        {
            var server = providerId is { } id && settings.Providers.FirstOrDefault(p => p.Id == id) is { } p ? p : settings.ActiveProvider;
            return $"{(string.IsNullOrWhiteSpace(model) ? "default model" : model)} on {server.Name}";
        }
        var sb = new StringBuilder("Teammates' models. Each runs on its usual model unless you pick one of its versions with " +
            "message_bot's version argument. Pick a version when the problem needs it (harder reasoning, long research, " +
            "code, speed, privacy) and say which one you used and why in one short line:");
        foreach (var b in team)
        {
            sb.Append($"\n- {b.Name}: usually {On(b.ProviderId, b.Model)}");
            foreach (var v in b.Versions)
                sb.Append($"\n  - version \"{v.Name}\": {On(v.ProviderId, v.Model)}{(v.Note.Length > 0 ? " — " + v.Note : "")}");
        }
        return sb.ToString();
    }

    public static string InitiativeGuide(IReadOnlyDictionary<string, ITool> tools, bool unattended)
    {
        var sb = new StringBuilder("Take initiative. When the next step is clear and safe, do it instead of asking whether to. ");
        sb.Append(unattended
            ? "No one is watching right now, so don't ask questions: make sensible assumptions and note them. "
            : "If something is unclear, make a sensible assumption, say what you assumed and keep going; ask only when a wrong guess would be costly, and then ask everything you need at once. ");
        sb.Append("Deliver what you have rather than nothing, and end with the next step you'll take or recommend.");
        if (tools.ContainsKey("follow_up"))
            sb.Append(" When something should be checked again later (waiting on a reply, new postings, a price, a deadline, ")
              .Append("a slow job), schedule it with follow_up and tell the user when you'll be back, instead of asking them to remind you.");
        if (tools.ContainsKey("start_task") && !unattended)
            sb.Append(" For work that needs many searches or steps (research across many sources, comparing lots of options, ")
              .Append("a job hunt), hand it to start_task with a complete goal so it runs in the background and reports back here.");
        if (tools.ContainsKey("delegate_task"))
            sb.Append(" When a job has several independent parts (researching several companies, checking many postings), ")
              .Append("hand them to helpers with delegate_task so they're done at the same time.");
        if (tools.ContainsKey("propose_new_bot"))
            sb.Append(" If a kind of work keeps coming up that no one on the team specialises in, suggest hiring a new bot for ")
              .Append("it with propose_new_bot (the user approves it first). Don't propose one for a one-off request.");
        if (tools.ContainsKey(UpdatePlanTool.ToolName) && !unattended)
            sb.Append(" For a task with 3 or more distinct steps, show your plan first with update_plan and tick steps off as you go.");
        if (tools.ContainsKey("add_routine") && !unattended)
            sb.Append(" When the user wants something done regularly (weekly, every morning), set it up with add_routine on the bot that does it.");
        return sb.ToString();
    }

    private async Task<StringBuilder> BuildSystemPromptAsync(Conversation conv, Bot? bot, List<Bot> members, List<Bot> allBots,
        AppSettings settings, Dictionary<string, ITool> byName, SendOptions opts, bool unattended, CodeSession? codeSession = null)
    {
        // Date only: a clock time would change the prompt every minute and defeat prompt caching
        // (get_current_datetime gives the exact time).
        var date = DateTime.Now.ToString("dddd, MMMM d, yyyy");
        StringBuilder system;
        if (bot is null)
        {
            system = new StringBuilder(Personas.Render(Personas.Get(conv.PersonaId), settings.CustomSystemPrompt, settings.ActiveProvider));
        }
        else
        {
            system = new StringBuilder()
                .Append($"You are {bot.Name}, the user's {bot.Title}. You are one of their AI teammates (\"bots\") in Locus, ")
                .Append($"{Personas.Where(settings.ActiveProvider)}. Current date: {date}. Format answers in Markdown.\n\n")
                .Append("Your role and instructions:\n").Append(bot.Instructions.Trim().Length > 0 ? bot.Instructions.Trim() : "Be helpful.");
            if (!string.IsNullOrWhiteSpace(settings.CustomSystemPrompt))
                system.Append("\n\nAdditional instructions from the user:\n").Append(settings.CustomSystemPrompt);
        }

        // SOUL.md: the personality and voice the user gave Locus (every bot keeps its own role on top of it).
        if (await projectContext.SoulAsync() is { } soul)
            system.AppendLine().AppendLine().AppendLine("Personality and voice (from SOUL.md — keep to it unless your role says otherwise):").AppendLine(soul);

        if (settings.MemoryEnabled)
        {
            // Three layers: the profile (always), recent memories not yet folded into it (listed here), and long-term
            // memories (archived), which are attached to the user's message when relevant (see RelevantMemoriesAsync).
            var max = Math.Max(5, bot?.Memory.MaxInPrompt ?? 60);
            var hidden = 0;
            if (bot is null || bot.Memory.UseSharedMemory)
            {
                if ((await curator.ProfileAsync()).Text is { Length: > 0 } profile)
                    system.AppendLine().AppendLine().AppendLine("Who the user is (their profile):").AppendLine(profile);
                var shared = await memories.ListAsync();
                var recent = RecentMemories(shared, max);
                hidden += shared.Count - recent.Count;
                if (recent.Count > 0)
                {
                    system.AppendLine().AppendLine().AppendLine("What you remember about the user (id: fact):");
                    foreach (var m in recent) system.AppendLine($"- {m.Id}: {m.Text}");
                }
            }
            if (bot is { Memory.Enabled: true } && await memories.ListAsync(bot.Id) is { Count: > 0 } own)
            {
                var recent = RecentMemories(own, max);
                hidden += own.Count - recent.Count;
                if (recent.Count > 0)
                {
                    system.AppendLine().AppendLine($"Your own notes as {bot.Name} (id: note):");
                    foreach (var m in recent) system.AppendLine($"- {m.Id}: {m.Text}");
                }
            }
            if (hidden > 0)
                system.AppendLine($"({hidden} more memories are in long-term storage. Ones that look relevant are attached to the user's " +
                    $"message as \"From long-term memory\"{(byName.ContainsKey("recall_memory") ? "; search the rest with recall_memory" : "")}.)");
        }

        // The lead (plain Locus, the coordinator, or a bot you talk to one-on-one) knows you: its notes on how to be with
        // you, and what's going on in your life.
        if (settings.MemoryEnabled && (bot is null || bot.IsCoordinator || members.Count <= 1) && (bot is null || bot.Memory.Enabled)
            && await companion.PromptAsync(bot?.Id) is { Length: > 0 } companionship)
            system.AppendLine().AppendLine().AppendLine(companionship);

        // People and companies: just the names (who_is has the notes), so the prompt stays short.
        if (byName.ContainsKey("who_is") && await people.ListAsync() is { Count: > 0 } known)
            system.AppendLine().Append("People and companies you have notes on (look them up with who_is before you write to, call or ")
                .Append("discuss them; add what you learn with note_about): ")
                .AppendLine(string.Join(", ", known.OrderBy(e => e.Name).Take(60).Select(e => e.Name)) + (known.Count > 60 ? ", …" : "") + ".");

        if (bot is not null && members.Count > 1)
        {
            system.AppendLine().AppendLine()
                .AppendLine("You are in a GROUP CHAT with the user and these bots:");
            foreach (var b in members.Where(b => b.Id != bot.Id))
                system.AppendLine($"- {b.Name}: {b.Title}{(b.IsCoordinator ? " (coordinator)" : "")}");
            system.Append("Messages from other bots appear as \"[Name]: …\". Stick to your role and keep replies focused; ")
                .Append("don't repeat what others already said. To hand work to another bot, @mention them by exact name ")
                .Append("(e.g. \"@Researcher please check…\") — they reply next. Only @mention a bot when you need them to act. ")
                .Append("Never write lines on behalf of other bots or prefix your reply with your own name.");
        }
        else if (bot is not null && byName.ContainsKey("message_bot"))
        {
            system.AppendLine().AppendLine().AppendLine("Other bots on the team you can contact with the message_bot tool:");
            foreach (var b in allBots.Where(b => b.Id != bot.Id))
                system.AppendLine($"- {b.Name}: {b.Title}");
            if (bot.IsCoordinator)
                system.Append("As the coordinator, delegate specialist work to them with message_bot and combine the results. Independent requests to different bots can go out in the same turn so they work in parallel.");
        }

        if (bot is { IsCoordinator: true } && allBots.Any(b => b.Id != bot.Id) && (members.Count > 1 || bot.Abilities.MessageBots))
        {
            system.AppendLine().AppendLine().Append(CoordinatorGuide);
            if (byName.ContainsKey("message_bot") && TeamModels(bot, allBots, settings) is { } team)
                system.AppendLine().AppendLine().Append(team);
        }

        if (bot is not null && bot.Abilities.MessageBots && members.Count < Bot.MaxPerGroup)
        {
            var outside = allBots.Where(b => b.Id != bot.Id && !conv.BotIds.Contains(b.Id)).ToList();
            if (outside.Count > 0)
                system.AppendLine().AppendLine()
                    .Append("If someone who isn't in this chat is needed, bring them in by @mentioning their exact name in your reply ")
                    .Append("— they join the chat and reply next. Only do this when their help is really needed. Not in this chat: ")
                    .Append(string.Join(", ", outside.Select(b => $"{b.Name} ({b.Title})"))).Append('.');
        }

        if (byName.Count > 0)
        {
            system.AppendLine().AppendLine()
                .Append("You can call tools. Use them whenever they make the answer more accurate: calculate for arithmetic, ")
                .Append("get_current_datetime for anything time-related, web tools for current or niche facts, ")
                .Append("search_documents for the user's own files. Don't invent tool results. After using tools, answer naturally.");
            if (byName.ContainsKey("save_memory"))
                system.Append(" When the user shares a lasting personal fact or preference, save it with save_memory.");
            if (byName.ContainsKey("edit_project_file"))
                foreach (var (folder, file, text) in await projectContext.ProjectInstructionsAsync())
                    system.AppendLine().AppendLine().AppendLine($"Project instructions for {folder} (its {file}; follow them when working there):").AppendLine(text);
            if (byName.ContainsKey("edit_project_file"))
                system.Append(" To change code in the user's projects: read the file with read_linked_file, make small exact edits ")
                      .Append("with edit_project_file (write_project_file only for new files), then check your work with ")
                      .Append("run_in_project (build, tests). Explain what you changed and why.");
        }

        if (codeSession is not null && byName.ContainsKey("todo_write"))
            system.AppendLine().AppendLine().Append(await CodeSessionGuideAsync(codeSession, conv.Code is null));

        if (settings.TakeInitiative && byName.Count > 0)
            system.AppendLine().AppendLine().Append(InitiativeGuide(byName, unattended));

        if (opts.DeepSearch && byName.ContainsKey("web_search"))
        {
            system.AppendLine().AppendLine()
                .Append("DEEPSEARCH MODE: research thoroughly before answering. Run several web_search queries from different ")
                .Append("angles, read the 2–4 most relevant pages with fetch_url, cross-check claims, then write a well-structured ")
                .Append("answer with [n] citations matching the search result numbers.");
        }

        if (unattended)
            system.AppendLine().AppendLine().Append("This is a scheduled routine: the user isn't watching right now. " +
                "Do the task and leave a concise report they can read later.");
        return system;
    }

    /// <summary>Messages to different bots (each in its own chat) are safe to run at once.</summary>
    private readonly object _helperGate = new();
    private int _helpersRunning;
    private readonly Queue<TaskCompletionSource> _helperQueue = new();

    /// <summary>Waits for a helper slot (Tools.MaxHelpers at a time, read live so changing it applies at once).</summary>
    private async Task<IDisposable> HelperSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            var max = Math.Max(1, (await settingsStore.GetAsync()).Tools.MaxHelpers);
            TaskCompletionSource wait;
            lock (_helperGate)
            {
                if (_helpersRunning < max)
                {
                    _helpersRunning++;
                    return new HelperSlot(this);
                }
                wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _helperQueue.Enqueue(wait);
            }
            using (ct.Register(() => wait.TrySetCanceled(ct))) await wait.Task;
        }
    }

    private sealed class HelperSlot(ChatService owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (owner._helperGate)
            {
                owner._helpersRunning--;
                while (owner._helperQueue.TryDequeue(out var next))
                    if (next.TrySetResult()) break;
            }
        }
    }

    /// <summary>The most helpers that have run at once (for tests).</summary>
    public int PeakHelpers { get; private set; }

    /// <summary>Runs one helper: a throwaway chat as <paramref name="bot"/>, unattended, returning its answer.</summary>
    internal async Task<string> RunHelperAsync(Bot? bot, string goal, string? shared, SendOptions opts, ToolContext parent, CancellationToken ct)
    {
        using var slot = await HelperSlotAsync(ct);
        lock (_helperGate) PeakHelpers = Math.Max(PeakHelpers, _helpersRunning);
        var conv = new Conversation { Title = "Helper", BotIds = bot is null ? [] : [bot.Id] };
        conv.Messages.Add(new ChatMessage
        {
            Role = ChatRole.User, RoutineName = "Helper",
            Content = "You're a helper doing one part of a bigger job. Do it with your tools, then reply with just the " +
                      "result (facts, numbers, names, links) in a compact form — no greeting, no questions.\n\n" +
                      (string.IsNullOrWhiteSpace(shared) ? "" : $"Background:\n{shared.Trim()}\n\n") + $"Your part:\n{goal}",
        });
        try
        {
            using var scope = UsageScope.Begin("Helpers", bot?.Id, parent.Conversation.Id);
            var reply = await RunAgentAsync(conv, bot, opts, () => Task.CompletedTask, ct, autoTitle: false,
                depth: parent.Depth + 1, unattended: true, untrustedInput: parent.TaintedBy, learn: false);
            if (reply is null) return "(no answer)";
            // What a helper read from the web (or email…) comes back with its answer: treat it the same way.
            foreach (var tc in reply.ToolCalls)
                if (parent.Tools.TryGetValue(tc.Name, out var t) && ContentTrust.UntrustedSource(t) is { } source)
                    parent.TaintedBy ??= source;
            if (reply.Error is not null) return $"(failed: {reply.Error})";
            var text = reply.Content.Trim();
            return text.Length > 8000 ? text[..8000] + "…" : text.Length == 0 ? "(no answer)" : text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"(failed: {ex.Message})"; }
        finally { try { await store.DeleteAsync(conv.Id); } catch { } }
    }

    public static bool CanRunInParallel(IReadOnlyList<ToolCall> batch)
    {
        if (batch.Count >= 2 && batch.All(t => t.Name == "delegate_task")) return true; // helpers run side by side
        if (batch.Count < 2 || batch.Any(t => t.Name != "message_bot")) return false;
        var targets = batch.Select(t =>
        {
            try { return JsonNode.Parse(t.ArgumentsJson)?["bot"]?.ToString().Trim().TrimStart('@').ToLowerInvariant(); }
            catch { return null; }
        }).ToList();
        return targets.All(t => !string.IsNullOrEmpty(t)) && targets.Distinct().Count() == targets.Count;
    }

    private async Task RunToolAsync(ToolCall tc, ITool? tool, ToolContext ctx, Func<Task> onUpdate, CancellationToken ct)
    {
        if (tool is null)
        {
            tc.Status = ToolCallStatus.Error;
            var close = ctx.Tools?.Keys.Where(n => n.Contains(tc.Name, StringComparison.OrdinalIgnoreCase)
                || tc.Name.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(3).ToList() ?? [];
            tc.Result = $"Unknown tool '{tc.Name}'." + (close.Count > 0 ? $" Did you mean {string.Join(" or ", close)}?" : " Use one of the tools you were given.");
            return;
        }

        // Local models get arguments wrong now and then: say exactly what was wrong and what's expected so the
        // next round can fix it, instead of a bare "invalid".
        var (args, problem) = Tools.ToolArguments.Check(tc.ArgumentsJson, tool.Parameters);
        if (problem is not null)
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = problem;
            return;
        }

        var held = ContentTrust.ShouldHold(tool, ctx) && !PermissionCatalog.IsBlocked(tool, ctx.Settings);
        if (held) tc.ApprovalReason = $"Asking because this conversation just read {ctx.TaintedBy}, which could contain hidden instructions.";
        var who = ctx.Bot?.Id ?? "locus";
        // "Always allow" (chosen on an earlier approval card) skips asking for this tool and this bot. Never for calls.
        var alwaysAllowed = AlwaysAllowable(tool.Name) && !PermissionCatalog.IsBlocked(tool, ctx.Settings)
            && ctx.Settings.AlwaysAllow.GetValueOrDefault(who)?.Contains(tool.Name) == true;
        if (!alwaysAllowed && (held || PermissionCatalog.NeedsApproval(tool, ctx.Settings, ctx)))
        {
            if (AlwaysAllowable(tool.Name))
            {
                tc.AlwaysAllowFor = ctx.Bot?.Name ?? "Locus";
                tc.AlwaysAllow = async () =>
                {
                    var s = await settingsStore.GetAsync();
                    var list = s.AlwaysAllow.TryGetValue(who, out var l) ? l : s.AlwaysAllow[who] = [];
                    if (!list.Contains(tool.Name)) list.Add(tool.Name);
                    await settingsStore.SaveAsync(s);
                };
            }
            if (ctx.Unattended || ctx.Depth > 0)
            {
                tc.Status = ToolCallStatus.Denied;
                tc.Result = held
                    ? $"Not run: this run read {ctx.TaintedBy}, so actions that change things need the user's OK, and they aren't available. " +
                      "Finish without it and tell the user what you'd like to do (they can allow this area under Permissions)."
                    : "This tool needs the user's approval and they aren't available right now. Continue without it, " +
                      "or tell the user what you'd like to run.";
                return;
            }
            tc.Status = ToolCallStatus.AwaitingApproval;
            tc.Approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Registered centrally so the dashboard and Telegram/Discord can answer it too.
            var pending = approvals.Register(ctx.Conversation, ctx.Bot, tc, tc.Approval);
            await onUpdate();
            bool approved;
            try { approved = await tc.Approval.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { approvals.Cancel(pending.Id); throw; }
            tc.Approval = null;
            tc.AlwaysAllow = null;
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Approval,
                $"{(approved ? "Allowed" : "Denied")} {tc.Name}", null, ctx.Bot?.Id, ctx.Conversation.Id, approved));
            if (!approved)
            {
                tc.Status = ToolCallStatus.Denied;
                tc.Result = "The user declined to run this tool. Continue without it.";
                await onUpdate();
                return;
            }
        }

        tc.Status = ToolCallStatus.Running;
        await onUpdate();
        var toolTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ToolResult result;
            List<UndoStep> undoSteps;
            using (UndoScope.Begin(out undoSteps))
                result = tool is MessageBotTool handoff
                    ? await handoff.RunAsync(args, ctx, tc, onUpdate, ct)
                    : await tool.ExecuteAsync(args, ctx, ct);
            if (undoSteps.Count > 0 && tool is not MessageBotTool)
            {
                await undoLog.AddAsync(tc.Id, tc.Name, ctx.Bot?.Id, ctx.Conversation.Id, undoSteps);
                tc.Undoable = true;
            }
            tc.DurationMs = toolTimer.ElapsedMilliseconds;
            lock (ctx.Reply.Trace) ctx.Reply.Trace.Add(new TraceStep("tool", tc.Name, tc.DurationMs));
            tc.Result = Tools.Schema.Truncate(result.Text, MaxToolResultChars);
            if (ContentTrust.UntrustedSource(tool) is { } source && !result.IsError)
            {
                ctx.TaintedBy ??= source;
                if (ctx.Settings.Tools.GuardUntrustedContent) tc.Result = ContentTrust.Label(source, tc.Result);
            }
            tc.Status = result.IsError ? ToolCallStatus.Error : ToolCallStatus.Done;
            if (result.Images is { Count: > 0 }) ctx.Reply.GeneratedImages.AddRange(result.Images);
            tc.Image = result.ModelImage;
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Tool,
                $"{ctx.Bot?.Name ?? "Locus"}: {tc.Name}", Audit(tc), ctx.Bot?.Id, ctx.Conversation.Id, !result.IsError));
            if (result.Sources is { Count: > 0 }) ctx.Reply.Sources.AddRange(result.Sources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = $"Tool failed: {ex.Message}";
        }
        await onUpdate();
    }

    /// <summary>Whether this reply gets checked against its tool results (Settings → Check answers).</summary>
    public static bool ShouldCheck(AnswerCheck mode, ChatMessage reply) =>
        reply.Error is null && reply.Content.Length > 0 && mode switch
        {
            AnswerCheck.Off => false,
            AnswerCheck.AnyTools => reply.ToolCalls.Any(t => t.Status == ToolCallStatus.Done),
            _ => reply.ToolCalls.Any(t => t.Status == ToolCallStatus.Done && ReadsSources(t.Name)),
        };

    /// <summary>Tools whose results are evidence an answer should match: web, documents, email, files.</summary>
    private static bool ReadsSources(string tool) =>
        tool is "web_search" or "fetch_url" or "search_documents" or "read_linked_file" or "read_file" or "message_bot"
        || tool.StartsWith("browser_") || tool is "drive_read" or "onedrive_read" or "excel_read" or "sheets_read" or "gmail_read" or "outlook_read";

    private const string CheckPrompt =
        "[Locus] Before this goes to the user, fact-check your answer above against the tool results in this conversation. " +
        "For each specific claim (numbers, names, dates, quotes, prices, links, citations), is it actually supported by what the " +
        "tools returned? Reply with exactly OK if everything is supported. Otherwise list only the problems, one per line: the claim, " +
        "and what the results actually say (or that nothing supports it).";

    private const string RevisePrompt =
        "[Locus] Rewrite your answer for the user, fixing those problems: correct or remove unsupported claims, and say plainly " +
        "what couldn't be verified. Keep everything that was right, the same format and length. Don't mention this check.";

    /// <summary>
    /// One extra turn asking the model to check its answer against the tool results; when it finds problems, a second
    /// turn rewrites the answer. Both continue the same conversation, so a local server reuses what it already read.
    /// </summary>
    private async Task<bool> CheckAnswerAsync(ChatMessage reply, List<LlmMessage> baseMessages, IReadOnlyList<ToolDefinition> tools, int room,
        ILlmClient client, string model, AppSettings settings, SendOptions opts, Bot? bot, Func<Task> onUpdate, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var notice = reply.Notice;
        reply.Notice = "🔎 Checking the answer against the sources…";
        await onUpdate();
        try
        {
            var messages = baseMessages.Concat(ToolRounds(reply)).ToList();
            // Fitted exactly as the reply's last step was, so the prompt starts the same and stays cached.
            messages = ContextWindow.FitToolResults(messages, room);
            messages.Add(new LlmMessage { Role = "assistant", Content = reply.Content });
            messages.Add(LlmMessage.User(CheckPrompt));
            // The tools stay listed (any calls the model makes are ignored) so the prompt starts the same as
            // the reply's last step and a local server reuses what it already read.
            var listed = tools.Count > 0 ? tools : null;
            var verdict = await CompleteAsync(client, new LlmRequest(model, messages, 0.1, 350, settings.ContextLength, false, listed), ct);
            reply.Trace.Add(new TraceStep("model", "check: " + model, timer.ElapsedMilliseconds));
            // A model that answers with a tool call written as text: drop it (the check simply doesn't apply).
            var toolNames = tools.Select(t => t.Name).ToHashSet();
            verdict = TextToolCalls.Find(verdict, toolNames).rest;
            var problems = verdict.Trim().Trim('.', '*', '"').Trim();
            if (problems.Length == 0 || problems.Equals("OK", StringComparison.OrdinalIgnoreCase) || problems.StartsWith("OK", StringComparison.OrdinalIgnoreCase) && problems.Length < 40)
            {
                reply.Notice = notice;
                return true;
            }
            reply.Notice = "🔎 Fixing what the check found…";
            await onUpdate();
            messages.Add(new LlmMessage { Role = "assistant", Content = problems });
            messages.Add(LlmMessage.User(RevisePrompt));
            var revised = await CompleteAsync(client, new LlmRequest(model, messages, settings.Temperature, settings.MaxTokens, settings.ContextLength, false, listed), ct);
            reply.Trace.Add(new TraceStep("model", "revise: " + model, timer.ElapsedMilliseconds));
            revised = TextToolCalls.Find(revised, toolNames).rest;
            // A rewrite that's only a line about what it's going to do ("Checking each claim…") isn't an answer.
            if (StripSelfPrefix(revised, bot).Trim() is { Length: > 20 } better && IsRealRewrite(better, reply.Content))
            {
                reply.Content = better;
                reply.Checked = problems.Length > 600 ? problems[..600] + "…" : problems;
            }
            reply.Notice = notice;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reply.Notice = notice; // the unchecked answer stands
            return false;
        }
        finally { await onUpdate(); }
    }

    /// <summary>Whether a rewritten answer can replace the original: not a fraction of its length, not a promise of work.</summary>
    public static bool IsRealRewrite(string rewrite, string original)
    {
        if (original.Length > 200 && rewrite.Length < original.Length * 0.35) return false;
        var firstLine = rewrite.Split('\n')[0].Trim();
        return !(rewrite.Length < 160 && System.Text.RegularExpressions.Regex.IsMatch(firstLine,
            @"^(I'?ll|I will|Let me|Checking|Now I|Going to|I'm going to|First,? I)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static async Task<string> CompleteAsync(ILlmClient client, LlmRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in client.StreamChatAsync(request, ct))
            if (!string.IsNullOrEmpty(chunk.Content)) sb.Append(chunk.Content);
        return sb.ToString();
    }

    /// <summary>Tools that can be set to "always allow" (placing phone calls always asks).</summary>
    public static bool AlwaysAllowable(string tool) => tool is not "call_phone";

    /// <summary>Short, readable summary of a tool call for the action history.</summary>
    private static string Audit(ToolCall tc)
    {
        var args = tc.ArgumentsJson.Length > 220 ? tc.ArgumentsJson[..220] + "…" : tc.ArgumentsJson;
        var result = (tc.Result ?? "").Replace('\n', ' ');
        return $"{args} → {(result.Length > 160 ? result[..160] + "…" : result)}";
    }

    /// <summary>Replays a reply's tool rounds as assistant tool_calls + tool results.</summary>
    /// <summary>
    /// The chat history to send, within <paramref name="room"/> tokens. Only the last two replies keep their tool
    /// steps. When it still doesn't fit, the oldest part is summarised (saved on the chat and extended later) and
    /// sent as a note instead of those messages.
    /// </summary>
    private async Task<List<LlmMessage>> FitHistoryAsync(Conversation conv, List<ChatMessage> window, Bot? bot, List<Bot> allBots,
        IReadOnlyList<Skill> skills, ILlmClient client, string model, int room, ChatMessage reply, Func<Task> onUpdate, CancellationToken ct)
    {
        room = Math.Max(800, room);
        var recentReplies = window.Where(m => m.Role == ChatRole.Assistant && m.Notice is null && (bot is null || m.BotId == bot.Id))
            .TakeLast(2).Select(m => m.Id).ToHashSet();

        int Cost(ChatMessage m) => ContextWindow.Tokens(ToLlmMessages([m], bot, allBots, skills, recentReplies));
        List<LlmMessage> Build(IEnumerable<ChatMessage> msgs, string? summary)
        {
            var list = new List<LlmMessage>();
            if (!string.IsNullOrWhiteSpace(summary))
                list.Add(LlmMessage.User($"[Summary of the earlier part of this conversation, which isn't shown]\n{summary.Trim()}"));
            list.AddRange(ToLlmMessages(msgs, bot, allBots, skills, recentReplies));
            return list;
        }

        // Start after what the saved summary already covers.
        var start = conv.SummaryThroughId is { } through && window.FindIndex(m => m.Id == through) is var at and >= 0 ? at + 1 : 0;
        var summary = start > 0 ? conv.Summary : null;
        var visible = window.Skip(start).ToList();
        var built = Build(visible, summary);
        if (ContextWindow.Tokens(built) <= room) return built;

        // Too long: fold the older messages into the summary, keeping the recent ones as they are.
        var cut = ContextWindow.RecentStart(visible, Cost, (int)(room * ContextWindow.RecentShare));
        if (cut <= 0) return ContextWindow.FitToolResults(built, room);
        var older = visible.Take(cut).ToList();
        var recent = visible.Skip(cut).ToList();
        var names = allBots.ToDictionary(b => b.Id, b => b.Name);
        string? newSummary = null;
        try
        {
            using var scope = UsageScope.Begin("Summaries", bot?.Id, conv.Id);
            var transcript = ContextWindow.TranscriptForSummary(summary, older, id => id is not null && names.TryGetValue(id, out var n) ? n : "Assistant", (int)(room * 0.8));
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(4));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model,
                [LlmMessage.System(ContextWindow.SummaryInstructions), LlmMessage.User(transcript)], 0.2, 1500, 0, false), cts.Token))
                sb.Append(c.Content);
            newSummary = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"<think>[\s\S]*?</think>", "").Trim();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* fall back to leaving the older part out */ }

        if (!string.IsNullOrWhiteSpace(newSummary))
        {
            conv.Summary = newSummary;
            conv.SummaryThroughId = older[^1].Id;
            var replyAt = conv.Messages.IndexOf(reply);
            conv.Messages.Insert(replyAt < 0 ? conv.Messages.Count : replyAt, new ChatMessage
            {
                Role = ChatRole.Assistant,
                Notice = $"This chat is long, so the earlier part ({older.Count} messages) was summarised to fit the model.",
            });
            await store.SaveAsync(conv);
            await onUpdate();
            return ContextWindow.FitToolResults(Build(recent, newSummary), room);
        }
        var fallback = Build(recent, summary);
        fallback.Insert(0, LlmMessage.User($"[{older.Count} earlier messages aren't shown because the conversation is long.]"));
        return ContextWindow.FitToolResults(fallback, room);
    }

    private static IEnumerable<LlmMessage> ToolRounds(ChatMessage m)
    {
        // Only the newest screenshot is sent back to the model; older ones would just eat the context window.
        var lastImage = m.ToolCalls.LastOrDefault(t => t.Image is not null);
        foreach (var round in m.ToolCalls.GroupBy(t => t.Round).OrderBy(g => g.Key))
        {
            yield return new LlmMessage
            {
                Role = "assistant",
                Content = round.First().Preface ?? "",
                ToolCalls = round.Select(t => new LlmToolCall(t.Id, t.Name, t.ArgumentsJson)).ToList(),
                ProviderState = round.First().ProviderState,
            };
            foreach (var t in round)
                yield return new LlmMessage
                {
                    Role = "tool", ToolCallId = t.Id, ToolName = t.Name,
                    Content = t.SentChars is { } n ? ContextWindow.ClipResult(t.Result ?? "", n) : t.Result ?? "",
                };
            if (lastImage is not null && round.Contains(lastImage))
                yield return LlmMessage.User(
                    $"[Screenshot from {lastImage.Name}. It is {lastImage.Image!.FileName}. Use its pixel coordinates for clicks.]",
                    [lastImage.Image]);
        }
    }

    /// <summary>
    /// History from the replying bot's point of view: its own messages are "assistant"; the user's and other
    /// bots' messages are "user", with other bots prefixed "[Name]: ".
    /// </summary>
    private static IEnumerable<LlmMessage> ToLlmMessages(IEnumerable<ChatMessage> history, Bot? self, List<Bot> allBots,
        IReadOnlyList<Skill> skills, ISet<string>? fullToolsFor = null)
    {
        foreach (var m in history)
        {
            if (m.Error is not null && m.Content.Length == 0) continue;
            if (m.IsStreaming) continue;
            if (m.Content.Length == 0 && m.Notice is not null && m.Trace.Count == 0) continue; // UI-only notes ("X joined")
            if (m.Role == ChatRole.User)
            {
                var text = Skill.Match(m.Content, skills) is { } hit ? hit.skill.Expand(hit.rest) : m.Content;
                if (!string.IsNullOrEmpty(m.EventPayload))
                    text = $"{text}\n\n<event>\n{m.EventPayload}\n</event>";
                if (!string.IsNullOrEmpty(m.AttachmentText))
                    text = $"{text}\n\n<attached_documents>\n{m.AttachmentText}\n</attached_documents>";
                if (m.FromBotId is not null)
                    text = $"[Message from {allBots.FirstOrDefault(b => b.Id == m.FromBotId)?.Name ?? "another bot"}]: {text}";
                else if (m.RoutineName == Phone.PhoneService.CalleeName)
                    text = $"[On the call]: {text}";
                else if (m.RoutineName is Phone.PhoneService.TaskName or Phone.PhoneService.ResultName)
                    text = $"[Locus] {text}";
                else if (m.RoutineName is not null and not ("Helper" or "API" or Phone.PhoneService.CallerName))
                    text = $"[Scheduled routine \"{m.RoutineName}\"]: {text}";
                yield return LlmMessage.User(text, m.Images);
            }
            else if (m.Role == ChatRole.Assistant)
            {
                var mine = self is null || m.BotId == self.Id;
                if (mine)
                {
                    // Older replies keep their answers; their tool steps (often long results) are left out.
                    if (fullToolsFor is null || fullToolsFor.Contains(m.Id))
                        foreach (var r in ToolRounds(m)) yield return r;
                    if (m.Content.Length > 0) yield return new LlmMessage { Role = "assistant", Content = m.Content };
                }
                else if (m.Content.Length > 0)
                {
                    var name = allBots.FirstOrDefault(b => b.Id == m.BotId)?.Name ?? "Locus";
                    yield return LlmMessage.User($"[{name}]: {m.Content}");
                }
            }
        }
    }

    private static int _modelCallsInFlight;
    /// <summary>Servers seen calling tools despite tool_choice "none": their last step drops the tools straight away.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> IgnoresNoToolCalls = new();
    /// <summary>How long a reply can wait for its first word before the chat says the model is busy.</summary>
    public static TimeSpan WaitingNoticeAfter { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Text from several tool rounds runs together with piles of blank lines (models often send "\n\n" before a
    /// call): keep at most one blank line, except inside code blocks.
    /// </summary>
    public static string TidyRounds(string text)
    {
        if (!text.Contains("\n\n\n")) return text;
        var parts = text.Split("```");
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = System.Text.RegularExpressions.Regex.Replace(parts[i], @"\n[ \t]*(?:\n[ \t]*){2,}", "\n\n");
        return string.Join("```", parts);
    }

    private static string StripSelfPrefix(string text, Bot? bot)
    {
        text = TidyRounds(text);
        if (bot is null) return text;
        var t = text.TrimStart();
        foreach (var p in new[] { $"[{bot.Name}]:", $"{bot.Name}:", $"**{bot.Name}:**", $"**{bot.Name}**:" })
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return t[p.Length..];
        return text;
    }

    private static string Speaker(ChatMessage m, List<Bot> members) =>
        m.Role == ChatRole.User ? "User" : members.FirstOrDefault(b => b.Id == m.BotId)?.Name ?? "Bot";

    /// <summary>Delivers a message from one bot to another (in the recipient's own chat) and returns the reply.</summary>
    internal async Task<string> MessageBotAsync(Bot from, Bot to, string message, SendOptions opts, int depth, CancellationToken ct,
        string? untrusted = null, Conversation? pulledInto = null, ToolCall? call = null, Func<Task>? onUpdate = null)
    {
        // Pulled into a chat: the work happens in a behind-the-scenes chat (kept out of the sidebar) and shows live in
        // the chat it was pulled into, as the teammate's own message.
        var conv = pulledInto is null ? await store.GetOrCreateDirectAsync(to) : await store.GetOrCreateHandoffAsync(pulledInto, to);
        await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, $"{from.Name} → {to.Name}",
            message.Length > 200 ? message[..200] + "…" : message, to.Id, pulledInto?.Id ?? conv.Id));
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = message, FromBotId = from.Id });
        if (pulledInto is null) conv.Unread = true;
        async Task Shown()
        {
            if (call is null || onUpdate is null) return;
            if (conv.Messages.LastOrDefault() is { Role: ChatRole.Assistant } working) call.Handoff = working;
            await onUpdate();
        }
        var reply = await RunAgentAsync(conv, to, opts, Shown, ct, autoTitle: false, depth: depth,
            untrustedInput: untrusted is null ? null : $"{untrusted} (via {from.Name})");
        if (call is not null && reply is not null) call.Handoff = reply;
        if (reply?.Error is not null) return $"{to.Name} couldn't reply: {reply.Error}";
        return string.IsNullOrWhiteSpace(reply?.Content) ? $"{to.Name} sent an empty reply." : reply.Content;
    }

    /// <summary>"/imagine …" — generate directly without asking the LLM.</summary>
    public async Task ImagineAsync(Conversation conv, string prompt, Func<Task> onUpdate, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync();
        var reply = new ChatMessage { Role = ChatRole.Assistant, IsStreaming = true, Model = settings.ImageGen.Backend.ToString() };
        var tc = new ToolCall { Name = "generate_image", ArgumentsJson = new JsonObject { ["prompt"] = prompt }.ToJsonString(), Status = ToolCallStatus.Running };
        reply.ToolCalls.Add(tc);
        conv.Messages.Add(reply);
        await onUpdate();
        try
        {
            var img = await imageGen.GenerateAsync(settings.ImageGen, prompt, null, null, null, ct);
            reply.GeneratedImages.Add(img);
            tc.Status = ToolCallStatus.Done;
            tc.Result = "Image generated and displayed to the user.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = ex.Message;
            reply.Error = $"Image generation failed: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = "Cancelled.";
        }
        reply.IsStreaming = false;
        if (conv.Title == "New chat") conv.Title = "🎨 " + (prompt.Length > 40 ? prompt[..40] + "…" : prompt);
        conv.UpdatedAt = DateTimeOffset.Now;
        await store.SaveAsync(conv);
        await onUpdate();
    }

    /// <summary>
    /// Auto-learn: after a reply, ask the model what (if anything) is worth remembering long-term and save it —
    /// facts about the user to the shared memory, the bot's own working notes to the bot's memory.
    /// </summary>
    private async Task LearnAsync(Conversation conv, Bot? bot, ChatMessage reply, ILlmClient client, string model, Func<Task> onUpdate)
    {
        using var usageScope = UsageScope.Begin("Memory learning", bot?.Id, conv.Id);
        try
        {
            var lastUser = conv.Messages.LastOrDefault(m => m.Role == ChatRole.User && m.FromBotId is null);
            if (lastUser is null || lastUser.Content.Length < 8) return;
            var known = (await memories.ListAsync()).Select(m => m.Text)
                .Concat(bot is null ? [] : (await memories.ListAsync(bot.Id)).Select(m => m.Text))
                .TakeLast(60);
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    "You extract memories for an AI assistant. From the exchange, list at most 3 NEW facts worth remembering " +
                    "for future conversations: lasting preferences, personal details, ongoing projects, people, decisions. " +
                    "Skip small talk, one-off questions and anything already known. One per line, each starting with " +
                    "\"user: \" (a fact about the user)" + (bot is null ? "" : $" or \"note: \" (something {bot.Name} should keep in mind for its work)") +
                    ", or \"about <Full Name>: \" (a fact about a specific person or company in the user's life or work, e.g. " +
                    "\"about Shield AI: interviewing Mitch for Director SE&A\")" +
                    ", or \"thread: <short title> | <what's new>\" (news on something ongoing in the user's life: a job search, a move, a " +
                    "project, a trip, someone's health — e.g. \"thread: Job search | Final-round interview at Shield AI on Friday\")" +
                    ", or \"vibe: \" (something about how to be with the user: their humor, mood or stress right now, how they like " +
                    "to be talked to, a running joke — e.g. \"vibe: dry humor; likes short, blunt answers\"). " +
                    "Reply with exactly NONE if there's nothing worth keeping."),
                LlmMessage.User($"Already known:\n{string.Join("\n", known.Select(k => "- " + k))}\n\n" +
                                $"User said:\n{Clip(lastUser.Content, 2000)}\n\nAssistant replied:\n{Clip(reply.Content, 1500)}"),
            };
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.1, 300, 4096, false), cts.Token))
                sb.Append(c.Content);

            var saved = new List<string>();
            foreach (var raw in sb.ToString().Split('\n'))
            {
                var line = raw.Trim().TrimStart('-', '*', ' ');
                if (line.Length < 6 || line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                // "about Name: fact" goes to the people and company notes.
                if (line.StartsWith("about ", StringComparison.OrdinalIgnoreCase) && line.IndexOf(':') is var colon and > 7)
                {
                    var who = line[6..colon].Trim();
                    var what = line[(colon + 1)..].Trim();
                    if (who.Length is >= 2 and <= 60 && what.Length is >= 4 and <= 300 && !who.Equals("the user", StringComparison.OrdinalIgnoreCase))
                    {
                        await people.NoteAsync(who, what);
                        saved.Add($"{who}: {what}");
                        if (saved.Count(x => !x.StartsWith("💬") && !x.StartsWith("🧵")) >= 3) break;
                    }
                    continue;
                }
                if (line.StartsWith("vibe:", StringComparison.OrdinalIgnoreCase))
                {
                    if (await companion.AddNoteAsync(bot?.Id, line[5..].Trim())) saved.Add("💬 " + line[5..].Trim());
                    continue;
                }
                if (line.StartsWith("thread:", StringComparison.OrdinalIgnoreCase) && line[7..].Split('|', 2) is [var title, var news])
                {
                    if (await companion.UpdateAsync(title, news) is { } t) saved.Add($"🧵 {t.Title}: {news.Trim()}");
                    continue;
                }
                var isNote = line.StartsWith("note:", StringComparison.OrdinalIgnoreCase);
                var isUser = line.StartsWith("user:", StringComparison.OrdinalIgnoreCase);
                if (!isNote && !isUser) continue;
                var fact = line[5..].Trim();
                if (fact.Length < 4 || fact.Length > 300) continue;
                // Bots keep everything in their own memory; facts about the user also go to shared memory if allowed.
                string? scope = bot is null ? null : bot.Id;
                if (await memories.KnowsAsync(fact, scope) || await memories.KnowsAsync(fact, null)) continue;
                await memories.AddAsync(fact, scope);
                if (bot is not null && isUser && bot.Memory.ShareWhatItLearns) await memories.AddAsync(fact, null);
                saved.Add(fact);
                if (saved.Count(x => !x.StartsWith("💬") && !x.StartsWith("🧵")) >= 3) break;
            }
            if (saved.Count == 0) return;
            reply.Learned.AddRange(saved);
            await store.SaveAsync(conv, notify: false);
            await onUpdate();
        }
        catch { /* learning is best-effort */ }
    }

    /// <summary>
    /// Self-written skills: after a reply that took several tool calls, ask the model whether the method is worth
    /// keeping as a skill (or whether a skill it used needs fixing), and save it.
    /// </summary>
    internal async Task ReflectAsync(Conversation conv, Bot? bot, ChatMessage reply, ILlmClient client, string model,
        AppSettings settings, Func<Task> onUpdate)
    {
        using var usageScope = UsageScope.Begin("Skill learning", bot?.Id, conv.Id);
        try
        {
            var lastUser = conv.Messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (lastUser is null) return;
            var all = await skillStore.ListAsync();
            var used = reply.ToolCalls.Where(t => t.Name == "use_skill")
                .Select(t => { try { return JsonNode.Parse(t.ArgumentsJson)?["name"]?.ToString(); } catch { return null; } })
                .OfType<string>().Select(n => all.FirstOrDefault(s => s.Name == Skill.Slug(n.TrimStart('/')))).OfType<Skill>().Distinct().ToList();
            var steps = string.Join("\n", reply.ToolCalls.Select((t, i) =>
                $"{i + 1}. {t.Name}({Clip(t.ArgumentsJson, 200)}) → {t.Status}: {Clip(t.Result ?? "", 200)}"));
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System(
                    "You maintain a library of reusable skills (step-by-step methods) for AI assistants. Look at how a task was " +
                    "just done and decide ONE of:\n" +
                    "- CREATE: the task is a repeatable procedure (multi-step, likely to come up again) and no existing skill covers it.\n" +
                    "- IMPROVE <name>: an existing skill was used or fits, and what happened shows its steps are wrong, missing something, or there's a better way.\n" +
                    "- NONE: one-off, trivial, failed, or already covered well. Prefer NONE when unsure.\n" +
                    "Reply in exactly this format:\nACTION: CREATE | IMPROVE <name> | NONE\nNAME: short-dash-name\nDESCRIPTION: one line, when to use it\n" +
                    "INSTRUCTIONS:\nnumbered general steps with the exact tools/sites, pitfalls found, and the output format — no details specific to this one request."),
                LlmMessage.User(
                    $"Existing skills:\n{(all.Count == 0 ? "(none)" : string.Join("\n", all.Select(s => $"- {s.Name}: {s.Description}")))}\n\n" +
                    (used.Count == 0 ? "" : $"Skills used this time:\n{string.Join("\n\n", used.Select(s => $"/{s.Name}:\n{Clip(s.Instructions, 1500)}"))}\n\n") +
                    $"Request:\n{Clip(lastUser.Content, 1500)}\n\nSteps taken:\n{steps}\n\nFinal answer:\n{Clip(reply.Content, 1200)}"),
            };
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.2, 900, 8192, false), cts.Token))
                sb.Append(c.Content);
            var proposal = SkillProposal.Parse(sb.ToString());
            if (proposal is null) return;

            var by = bot?.Name ?? "Locus";
            if (proposal.Improve is { } target)
            {
                var skill = all.FirstOrDefault(s => s.Name == Skill.Slug(target));
                if (skill is null) return;
                await skillStore.ReviseAsync(skill, proposal.Instructions, proposal.Description, "Learned from a task", by, settings.SkillLearning.NeedsReview);
                reply.SkillNotes.Add($"improved /{skill.Name}");
            }
            else
            {
                var name = Skill.Slug(proposal.Name);
                if (name.Length < 3 || all.Any(s => s.Name == name)) return;
                var skill = new Skill
                {
                    Name = name, Icon = "📒", Description = proposal.Description, Instructions = proposal.Instructions,
                    CreatedBy = bot?.Id ?? "locus", Pending = settings.SkillLearning.NeedsReview,
                };
                await skillStore.SaveAsync(skill);
                reply.SkillNotes.Add($"saved /{skill.Name}");
            }
            await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, $"{by} {reply.SkillNotes[^1]}",
                settings.SkillLearning.NeedsReview ? "Waiting for your OK on the Skills page." : null, bot?.Id, conv.Id));
            await store.SaveAsync(conv, notify: false);
            await onUpdate();
        }
        catch { /* best effort */ }
    }

    private readonly HashSet<string> _titling = [];

    /// <summary>Still "New chat" after a real reply (tries again on the next couple of replies if naming failed).</summary>
    public static bool NeedsTitle(Conversation c) =>
        c.Title == "New chat" && c.Messages.Any(m => m.Role == ChatRole.User)
        && c.Messages.Count(m => m is { Role: ChatRole.Assistant, Notice: null, Error: null } && m.Content.Length > 0) is >= 1 and <= 3;

    /// <summary>Gives a chat a short, specific name from how it started. Returns the title, or null.</summary>
    public async Task<string?> AutoTitleAsync(Conversation conv, Func<Task>? onUpdate = null, CancellationToken ct = default)
    {
        lock (_titling) if (!_titling.Add(conv.Id)) return null;
        using var usageScope = UsageScope.Begin("Titles", null, conv.Id);
        try
        {
            var settings = await settingsStore.GetAsync();
            var lastBot = conv.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.BotId is not null)?.BotId;
            var bot = lastBot is null ? null : await bots.GetAsync(lastBot);
            var client = factory.CreateForBot(settings, bot);
            var model = conv.Messages.LastOrDefault(m => m is { Role: ChatRole.Assistant, Model: not null })?.Model ?? conv.Model;
            if (model is null) return null;
            var title = Title(await AskForTitleAsync(client, model, conv, ct));
            if (title is null) return null;
            if (conv.Title != "New chat") return null; // named meanwhile (by a bot or the user)
            conv.Title = title;
            await store.SaveAsync(conv);
            if (onUpdate is not null) await onUpdate();
            return title;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; } // stays "New chat"; tried again next reply
        finally { lock (_titling) _titling.Remove(conv.Id); }
    }

    /// <summary>Files the chat under a short topic (reusing an existing one when it fits), so chats can be grouped by topic.</summary>
    public async Task<string?> AutoTopicAsync(Conversation conv, Func<Task>? onUpdate = null, CancellationToken ct = default)
    {
        lock (_topicking) if (!_topicking.Add(conv.Id)) return null;
        using var usageScope = UsageScope.Begin("Titles", null, conv.Id);
        try
        {
            var settings = await settingsStore.GetAsync();
            var lastBot = conv.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.BotId is not null)?.BotId;
            var client = factory.CreateForBot(settings, lastBot is null ? null : await bots.GetAsync(lastBot));
            var model = conv.Messages.LastOrDefault(m => m is { Role: ChatRole.Assistant, Model: not null })?.Model ?? conv.Model;
            var first = conv.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content;
            if (model is null || first is null) return null;
            var existing = (await store.ListAsync()).Select(c => c.Topic).OfType<string>()
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(40).ToList();
            var reply = conv.Messages.FirstOrDefault(m => m is { Role: ChatRole.Assistant, Notice: null } && m.Content.Length > 0)?.Content ?? "";
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System("You file chats under topics. Reply with ONLY a broad 1-3 word topic in Title Case " +
                                  "(e.g. \"Job Search\", \"Cooking\", \"Home Server\", \"Health\", \"Coding\"). " +
                                  (existing.Count > 0 ? "Reuse one of these existing topics when it fits: " + string.Join(", ", existing) + ". " : "") +
                                  "No quotes, no emoji, no punctuation."),
                LlmMessage.User($"Chat titled \"{conv.Title}\".\nThe user wrote:\n{Clip(first, 600)}\n\nThe assistant replied:\n{Clip(reply, 400)}\n\nTopic:"),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.2, 1200, 4096, false), cts.Token))
                sb.Append(c.Content);
            var topic = Topic(sb.ToString(), existing);
            if (topic is null || conv.Topic is not null) return null; // set meanwhile by the user
            conv.Topic = topic;
            await store.SaveAsync(conv);
            if (onUpdate is not null) await onUpdate();
            return topic;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        finally { lock (_topicking) _topicking.Remove(conv.Id); }
    }

    private readonly HashSet<string> _topicking = [];

    /// <summary>Cleans a model's topic answer, matching an existing topic's spelling; null when unusable.</summary>
    public static string? Topic(string raw, IReadOnlyList<string> existing)
    {
        var line = Title(raw);
        if (line is null) return null;
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^topic\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (line.Length is 0 or > 32 || line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4) return null;
        return existing.FirstOrDefault(e => e.Equals(line, StringComparison.OrdinalIgnoreCase)) ?? line;
    }

    private static async Task<string> AskForTitleAsync(ILlmClient client, string model, Conversation conv, CancellationToken ct)
    {
        var first = conv.Messages.First(m => m.Role == ChatRole.User).Content;
        var reply = conv.Messages.FirstOrDefault(m => m is { Role: ChatRole.Assistant, Notice: null } && m.Content.Length > 0)?.Content ?? "";
        var prompt = new List<LlmMessage>
        {
            LlmMessage.System("You name chats. Reply with ONLY a specific 2-6 word title in Title Case that says what the chat " +
                              "is about (e.g. \"Director-Level Job Search Detroit\", \"Fixing Blazor Login Redirect\"). No quotes, " +
                              "no emoji, no ending punctuation, never \"New chat\"."),
            LlmMessage.User($"The user wrote:\n{Clip(first, 700)}\n\nThe assistant replied:\n{Clip(reply, 500)}\n\nTitle:"),
        };
        var sb = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // In the background, so it can wait behind other work on a busy model server.
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        // Room for reasoning models, which think before answering even for a few words.
        await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.3, 1200, 4096, false), cts.Token))
            sb.Append(c.Content);
        return sb.ToString();
    }

    /// <summary>Cleans a model's title answer; null when there's nothing usable.</summary>
    public static string? Title(string raw)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"<think>[\s\S]*?(</think>|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^(title|chat title)\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        line = line.Trim().Trim('"', '\'', '`', '*', '#', '.', ' ', '“', '”');
        if (line.Length is 0 or > 70 || line.Equals("New chat", StringComparison.OrdinalIgnoreCase)) return null;
        return line;
    }

    /// <summary>Names chats still called "New chat" (e.g. from before naming worked with reasoning models).</summary>
    public async Task<int> TitleUntitledAsync(int max = 30, CancellationToken ct = default)
    {
        var named = 0;
        if (!(await settingsStore.GetAsync()).AutoTitle) return 0;
        // The chat list shows untitled chats by their first message, so check the files themselves.
        foreach (var (id, _) in store.Files().OrderByDescending(f => f.modified).Take(300))
        {
            if (named >= max || ct.IsCancellationRequested) break;
            var conv = await store.GetAsync(id);
            if (conv is null || conv.Title != "New chat" || conv.Running is not null || !conv.Messages.Any(m => m is { Role: ChatRole.Assistant, Notice: null } && m.Content.Length > 0)) continue;
            if (await AutoTitleAsync(conv, null, ct) is not null) named++;
        }
        return named;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}

/// <summary>
/// Hands parts of a job to temporary helper agents that work in parallel (at most Tools.MaxHelpers at a time across
/// Locus) and return their results. Helpers act as the same bot, can't ask the user (so nothing that needs approval
/// runs), can't start more helpers, and leave no chat behind.
/// </summary>
internal sealed class DelegateTool(ChatService chat, Bot? self, SendOptions opts) : ITool
{
    public const int MaxGoals = 10;

    public string Name => "delegate_task";
    public string DisplayName => "Hand off to helpers";
    public string Icon => "👥";
    public string Description =>
        "Split a job into independent parts and hand them to temporary helper agents that work at the same time and " +
        "return their results (e.g. research 5 companies, check 8 job postings, summarise 4 documents). Each goal must " +
        "stand on its own: include everything the helper needs. Helpers have your tools but can't ask the user anything.";
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["goals"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = $"1–{MaxGoals} independent goals, one per helper" },
            ["shared_context"] = new JsonObject { ["type"] = "string", ["description"] = "Background every helper needs (criteria, format to return)" },
        },
        ["required"] = new JsonArray("goals"),
    };
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var node = args["goals"];
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            node = s.TrimStart().StartsWith('[') ? JsonNode.Parse(s) : new JsonArray(s);
        var goals = (node as JsonArray ?? []).Select(g => g?.ToString().Trim() ?? "").Where(g => g.Length > 0).ToList();
        if (goals.Count == 0) return ToolResult.Error("Give at least one goal.");
        if (goals.Count > MaxGoals) return ToolResult.Error($"At most {MaxGoals} goals at once.");
        var shared = args.Str("shared_context");
        var model = ctx.Settings.ProviderFor(self).Id == ctx.Settings.ActiveProvider.Id ? ctx.Model : opts.Model;

        var results = await Task.WhenAll(goals.Select(g => chat.RunHelperAsync(self, g, shared, opts with { Model = model }, ctx, ct)));
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < goals.Count; i++)
            sb.AppendLine($"### Helper {i + 1}: {(goals[i].Length > 80 ? goals[i][..80] + "…" : goals[i])}").AppendLine(results[i]).AppendLine();
        return new ToolResult(sb.ToString().Trim());
    }
}

/// <summary>Lets a bot send a direct message to another bot and get its reply (asynchronous handoff).</summary>
internal sealed class MessageBotTool(ChatService chat, Bot self, IReadOnlyList<Bot> others, SendOptions opts) : ITool
{
    public string Name => "message_bot";
    public string DisplayName => "Message bot";
    public string Icon => "💬";
    public string Description =>
        "Send a message to another bot on the user's team and get its reply. Use it to delegate specialist work " +
        "or ask a teammate something. Include all context they need — they can't see this chat. To ask several bots " +
        "about independent things, call message_bot for each in the same turn: they work in parallel. Bots: " +
        string.Join(", ", others.Select(b => $"{b.Name} ({b.Title})")) +
        (others.Any(b => b.Versions.Count > 0)
            ? ". Some bots have versions on other models (listed with the team in your instructions): pass version to pick one."
            : "");
    public JsonObject Parameters => others.Any(b => b.Versions.Count > 0)
        ? Schema.Object(
            ("bot", "string", "Exact name of the bot to message", true),
            ("message", "string", "The request, with any context they need", true),
            ("version", "string", "Optional: name of one of the bot's versions, to run it on that model instead of its usual one", false))
        : Schema.Object(
            ("bot", "string", "Exact name of the bot to message", true),
            ("message", "string", "The request, with any context they need", true));
    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => RunAsync(args, ctx, null, null, ct);

    /// <summary>Runs the hand-off, showing the teammate's reply live on <paramref name="call"/> as it works.</summary>
    public async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx, ToolCall? call, Func<Task>? onUpdate, CancellationToken ct)
    {
        var name = (args.Str("bot") ?? "").Trim().TrimStart('@');
        var target = others.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                     ?? others.FirstOrDefault(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && name.Length > 2);
        if (target is null)
            return ToolResult.Error($"No bot named '{name}'. Bots: {string.Join(", ", others.Select(b => b.Name))}");
        var message = args.Str("message");
        if (string.IsNullOrWhiteSpace(message)) return ToolResult.Error("message is required");
        var label = target.Name;
        if (args.Str("version") is { Length: > 0 } versionName && !versionName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            if (target.FindVersion(versionName) is not { } version)
                return ToolResult.Error(target.Versions.Count == 0
                    ? $"{target.Name} has no other versions; leave version out."
                    : $"{target.Name} has no version '{versionName}'. Versions: {string.Join(", ", target.Versions.Select(v => v.Name))}.");
            target = target.WithVersion(version);
            label = $"{target.Name} ({version.Name})";
        }
        // Pass on the chat's model, unless the sender runs on its own server (its model name means nothing elsewhere).
        var model = ctx.Settings.ProviderFor(self).Id == ctx.Settings.ActiveProvider.Id ? ctx.Model : opts.Model;
        var reply = await chat.MessageBotAsync(self, target, message, opts with { Model = model }, ctx.Depth + 1, ct, ctx.TaintedBy,
            ctx.Conversation, call, onUpdate);
        return new ToolResult($"{label} replied:\n\n{reply}");
    }
}
