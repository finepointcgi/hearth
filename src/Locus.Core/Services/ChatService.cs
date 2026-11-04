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
    MemoryCurator curator)
{
    private const int MaxToolResultChars = 16000;
    private const int MaxBotCallDepth = 2;

    /// <summary>Entry point from the chat UI: replies to the last user message in any kind of conversation.</summary>
    public async Task GenerateAsync(Conversation conv, SendOptions opts, Func<Task> onUpdate, CancellationToken ct)
    {
        if (conv.BotIds.Count == 0)
        {
            await RunAgentAsync(conv, null, opts, onUpdate, ct);
            return;
        }

        var members = await bots.GetManyAsync(conv.BotIds);
        if (members.Count == 0) throw new InvalidOperationException("The bots in this chat were deleted.");
        if (members.Count == 1)
        {
            await RunAgentAsync(conv, members[0], opts, onUpdate, ct);
            return;
        }
        await RunGroupTurnAsync(conv, members, opts, onUpdate, ct);
    }

    // ---------------------------------------------------------------- group chats

    private async Task RunGroupTurnAsync(Conversation conv, List<Bot> members, SendOptions opts, Func<Task> onUpdate, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync();
        var lastUser = conv.Messages.Last(m => m.Role == ChatRole.User);
        var queue = new List<Bot>(Mentions(lastUser.Content, members, includeEveryone: true));
        if (queue.Count == 0) queue.AddRange(await RouteAsync(conv, members, lastUser.Content, opts.Model, ct));

        var repliesPerBot = new Dictionary<string, int>();
        var hops = 0;
        while (queue.Count > 0 && hops < Math.Max(1, settings.MaxBotHops) && !ct.IsCancellationRequested)
        {
            var bot = queue[0];
            queue.RemoveAt(0);
            repliesPerBot[bot.Id] = repliesPerBot.GetValueOrDefault(bot.Id) + 1;
            hops++;

            var reply = await RunAgentAsync(conv, bot, opts, onUpdate, ct, autoTitle: false);
            if (reply is null || reply.Error is not null) break;

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

    /// <summary>No @mention: ask the model which member(s) should answer.</summary>
    private async Task<List<Bot>> RouteAsync(Conversation conv, List<Bot> members, string message, string model, CancellationToken ct)
    {
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
                LlmMessage.System("You route messages in a group chat of AI bots. Reply with ONLY the names of the bots that " +
                    "should answer, comma-separated. Usually pick exactly one: the best fit for the request. Pick the " +
                    "coordinator for broad or multi-part requests. Pick several only if the user clearly addresses several."),
                LlmMessage.User($"Bots:\n{roster}\n\nRecent messages:\n{recent}\n\nNew message from the user:\n{Clip(message, 800)}\n\nWho should answer?"),
            };
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
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
        CancellationToken ct, bool autoTitle = true, int depth = 0, bool unattended = false, IReadOnlyList<ITool>? extraTools = null, int? historyWindow = null)
    {
        var settings = await settingsStore.GetAsync();
        var client = factory.CreateForBot(settings, bot);
        // A bot on its own server uses its model, or that server's default — not the model picked for the chat.
        var ownServer = settings.ProviderFor(bot).Id != settings.ActiveProvider.Id;
        var model = !string.IsNullOrWhiteSpace(bot?.Model) ? bot.Model
            : ownServer ? await ModelPicker.DefaultForAsync(factory, settings.ProviderFor(bot)) ?? opts.Model
            : opts.Model;
        var history = conv.Messages.ToList();
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User)
            ?? throw new InvalidOperationException("No user message to reply to.");
        var allBots = bot is null ? [] : await bots.ListAsync();
        var members = conv.BotIds.Select(id => allBots.FirstOrDefault(b => b.Id == id)).OfType<Bot>().ToList();

        var reply = new ChatMessage { Role = ChatRole.Assistant, Model = model, IsStreaming = true, BotId = bot?.Id };
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
            available = available.Where(t => !PermissionCatalog.IsBlocked(t, settings)).ToList();
            if (extraTools is not null) available.AddRange(extraTools);
            var byName = available.ToDictionary(t => t.Name);

            var system = await BuildSystemPromptAsync(conv, bot, members, allBots, settings, byName, opts, unattended);

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
            if (byName.ContainsKey("save_skill"))
                system.AppendLine().AppendLine(
                    "Learn as you go: when you finish a multi-step task you'd likely be asked to repeat, save the method with " +
                    "save_skill. When a skill's steps were wrong or you found a better way, fix it with improve_skill.");

            var baseMessages = new List<LlmMessage> { LlmMessage.System(system.ToString()) };
            // Long-running tasks pass a window so their history doesn't outgrow the model's context.
            var window = historyWindow is > 0 && history.Count > historyWindow ? history.TakeLast(historyWindow.Value).ToList() : history;
            baseMessages.AddRange(ToLlmMessages(window, bot, allBots, skills));

            var ctx = new ToolContext
            {
                Conversation = conv, Reply = reply, Settings = settings, Client = client, Model = model,
                Bot = bot, Depth = depth, Unattended = unattended,
            };
            var definitions = available.Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList();
            var content = new StringBuilder();
            var thinking = new StringBuilder();
            var maxRounds = Math.Max(1, settings.Tools.MaxRounds);

            for (var round = 0; round < maxRounds; round++)
            {
                var messages = baseMessages.Concat(ToolRounds(reply)).ToList();
                // Last round: withhold tools so the model has to answer.
                var offer = definitions.Count > 0 && round < maxRounds - 1 ? definitions : null;
                var request = new LlmRequest(model, messages, settings.Temperature, settings.MaxTokens,
                    settings.ContextLength, opts.Think, offer);

                if (content.Length > 0 && !content.ToString().EndsWith("\n\n")) content.Append("\n\n");
                List<LlmToolCall> calls = [];
                string? providerState = null;
                await foreach (var chunk in client.StreamChatAsync(request, ct))
                {
                    if (!string.IsNullOrEmpty(chunk.Content)) content.Append(chunk.Content);
                    if (!string.IsNullOrEmpty(chunk.Thinking)) thinking.Append(chunk.Thinking);
                    if (chunk.Stats is not null) reply.Stats = chunk.Stats;
                    if (chunk.ToolCalls is not null) calls.AddRange(chunk.ToolCalls);
                    if (chunk.Notice is not null) reply.Notice = chunk.Notice;
                    if (chunk.ProviderState is not null) providerState = chunk.ProviderState;
                    reply.Content = StripSelfPrefix(content.ToString(), bot).Trim();
                    reply.Thinking = thinking.Length > 0 ? thinking.ToString().Trim() : null;
                    await onUpdate();
                }

                if (calls.Count == 0) break;

                var batch = calls.Select(call => new ToolCall
                {
                    Id = call.Id, Name = call.Name, ArgumentsJson = call.ArgumentsJson, Round = round,
                    ProviderState = call == calls[0] ? providerState : null,
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
        }

        // Helpers count too: delegated bots often do the real multi-step work.
        var reflect = reply.Error is null && reply.Content.Length > 0 && settings.SkillLearning.Enabled
                      && (bot is null || bot.Abilities.Skills) && reply.SkillNotes.Count == 0
                      && reply.ToolCalls.Count(t => t.Status == ToolCallStatus.Done) >= Math.Max(2, settings.SkillLearning.MinToolCalls);
        if (reflect)
            _ = Task.Run(() => ReflectAsync(conv, bot, reply, client, model, settings, onUpdate));

        var learn = reply.Error is null && reply.Content.Length > 0 && depth == 0 && settings.MemoryEnabled
                    && (bot is null ? settings.MemoryAutoLearn : bot.Memory is { Enabled: true, AutoLearn: true });
        if (learn)
            _ = Task.Run(() => LearnAsync(conv, bot, reply, client, model, onUpdate));

        if (autoTitle && settings.AutoTitle && reply.Error is null && conv.Title == "New chat"
            && conv.Messages.Count(m => m.Role == ChatRole.Assistant) == 1)
        {
            _ = Task.Run(() => AutoTitleAsync(conv, client, model, onUpdate));
        }
        return reply;
    }

    private async Task<StringBuilder> BuildSystemPromptAsync(Conversation conv, Bot? bot, List<Bot> members, List<Bot> allBots,
        AppSettings settings, Dictionary<string, ITool> byName, SendOptions opts, bool unattended)
    {
        var date = DateTime.Now.ToString("dddd, MMMM d, yyyy h:mm tt");
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

        if (settings.MemoryEnabled)
        {
            var max = Math.Max(5, bot?.Memory.MaxInPrompt ?? 60);
            var hidden = 0;
            if (bot is null || bot.Memory.UseSharedMemory)
            {
                if ((await curator.ProfileAsync()).Text is { Length: > 0 } profile)
                    system.AppendLine().AppendLine().AppendLine("Who the user is (their profile):").AppendLine(profile);
                var shared = await memories.ListAsync();
                hidden += Math.Max(0, shared.Count - max);
                if (shared.Count > 0)
                {
                    system.AppendLine().AppendLine().AppendLine("What you remember about the user (id: fact):");
                    foreach (var m in shared.OrderByDescending(m => m.CreatedAt).Take(max).Reverse())
                        system.AppendLine($"- {m.Id}: {m.Text}");
                }
            }
            if (bot is { Memory.Enabled: true } && await memories.ListAsync(bot.Id) is { Count: > 0 } own)
            {
                hidden += Math.Max(0, own.Count - max);
                system.AppendLine().AppendLine($"Your own notes as {bot.Name} (id: note):");
                foreach (var m in own.OrderByDescending(m => m.CreatedAt).Take(max).Reverse())
                    system.AppendLine($"- {m.Id}: {m.Text}");
            }
            if (hidden > 0 && byName.ContainsKey("recall_memory"))
                system.AppendLine($"({hidden} older memories aren't listed; search them with recall_memory.)");
        }

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
            if (bot.IsCoordinator)
                system.Append("\nAs the coordinator: split the request, @mention the specialists who should do each part, " +
                              "and when their results are in, give the user one combined answer.");
        }
        else if (bot is not null && byName.ContainsKey("message_bot"))
        {
            system.AppendLine().AppendLine().AppendLine("Other bots on the team you can contact with the message_bot tool:");
            foreach (var b in allBots.Where(b => b.Id != bot.Id))
                system.AppendLine($"- {b.Name}: {b.Title}");
            if (bot.IsCoordinator)
                system.Append("As the coordinator, delegate specialist work to them with message_bot and combine the results. Independent requests to different bots can go out in the same turn so they work in parallel.");
        }

        if (byName.Count > 0)
        {
            system.AppendLine().AppendLine()
                .Append("You can call tools. Use them whenever they make the answer more accurate: calculate for arithmetic, ")
                .Append("get_current_datetime for anything time-related, web tools for current or niche facts, ")
                .Append("search_documents for the user's own files. Don't invent tool results. After using tools, answer naturally.");
            if (byName.ContainsKey("save_memory"))
                system.Append(" When the user shares a lasting personal fact or preference, save it with save_memory.");
        }

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
    public static bool CanRunInParallel(IReadOnlyList<ToolCall> batch)
    {
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
            tc.Result = $"Unknown tool '{tc.Name}'. Available tools are listed in the tools array.";
            return;
        }

        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(tc.ArgumentsJson) ? "{}" : tc.ArgumentsJson) as JsonObject ?? []; }
        catch (System.Text.Json.JsonException)
        {
            tc.Status = ToolCallStatus.Error;
            tc.Result = "Arguments were not valid JSON.";
            return;
        }

        if (PermissionCatalog.NeedsApproval(tool, ctx.Settings, ctx))
        {
            if (ctx.Unattended || ctx.Depth > 0)
            {
                tc.Status = ToolCallStatus.Denied;
                tc.Result = "This tool needs the user's approval and they aren't available right now. Continue without it, " +
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
        try
        {
            var result = await tool.ExecuteAsync(args, ctx, ct);
            tc.Result = Tools.Schema.Truncate(result.Text, MaxToolResultChars);
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

    /// <summary>Short, readable summary of a tool call for the action history.</summary>
    private static string Audit(ToolCall tc)
    {
        var args = tc.ArgumentsJson.Length > 220 ? tc.ArgumentsJson[..220] + "…" : tc.ArgumentsJson;
        var result = (tc.Result ?? "").Replace('\n', ' ');
        return $"{args} → {(result.Length > 160 ? result[..160] + "…" : result)}";
    }

    /// <summary>Replays a reply's tool rounds as assistant tool_calls + tool results.</summary>
    private static IEnumerable<LlmMessage> ToolRounds(ChatMessage m)
    {
        // Only the newest screenshot is sent back to the model; older ones would just eat the context window.
        var lastImage = m.ToolCalls.LastOrDefault(t => t.Image is not null);
        foreach (var round in m.ToolCalls.GroupBy(t => t.Round).OrderBy(g => g.Key))
        {
            yield return new LlmMessage
            {
                Role = "assistant",
                ToolCalls = round.Select(t => new LlmToolCall(t.Id, t.Name, t.ArgumentsJson)).ToList(),
                ProviderState = round.First().ProviderState,
            };
            foreach (var t in round)
                yield return new LlmMessage { Role = "tool", Content = t.Result ?? "", ToolCallId = t.Id, ToolName = t.Name };
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
        IReadOnlyList<Skill> skills)
    {
        foreach (var m in history)
        {
            if (m.Error is not null && m.Content.Length == 0) continue;
            if (m.IsStreaming) continue;
            if (m.Role == ChatRole.User)
            {
                var text = Skill.Match(m.Content, skills) is { } hit ? hit.skill.Expand(hit.rest) : m.Content;
                if (!string.IsNullOrEmpty(m.EventPayload))
                    text = $"{text}\n\n<event>\n{m.EventPayload}\n</event>";
                if (!string.IsNullOrEmpty(m.AttachmentText))
                    text = $"{text}\n\n<attached_documents>\n{m.AttachmentText}\n</attached_documents>";
                if (m.FromBotId is not null)
                    text = $"[Message from {allBots.FirstOrDefault(b => b.Id == m.FromBotId)?.Name ?? "another bot"}]: {text}";
                else if (m.RoutineName is not null)
                    text = $"[Scheduled routine \"{m.RoutineName}\"]: {text}";
                yield return LlmMessage.User(text, m.Images);
            }
            else if (m.Role == ChatRole.Assistant)
            {
                var mine = self is null || m.BotId == self.Id;
                if (mine)
                {
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

    private static string StripSelfPrefix(string text, Bot? bot)
    {
        if (bot is null) return text;
        var t = text.TrimStart();
        foreach (var p in new[] { $"[{bot.Name}]:", $"{bot.Name}:", $"**{bot.Name}:**", $"**{bot.Name}**:" })
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return t[p.Length..];
        return text;
    }

    private static string Speaker(ChatMessage m, List<Bot> members) =>
        m.Role == ChatRole.User ? "User" : members.FirstOrDefault(b => b.Id == m.BotId)?.Name ?? "Bot";

    /// <summary>Delivers a message from one bot to another (in the recipient's own chat) and returns the reply.</summary>
    internal async Task<string> MessageBotAsync(Bot from, Bot to, string message, SendOptions opts, int depth, CancellationToken ct)
    {
        var conv = await store.GetOrCreateDirectAsync(to);
        await activity.AddAsync(new Activity(DateTimeOffset.Now, ActivityKind.Message, $"{from.Name} → {to.Name}",
            message.Length > 200 ? message[..200] + "…" : message, to.Id, conv.Id));
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = message, FromBotId = from.Id });
        conv.Unread = true;
        var reply = await RunAgentAsync(conv, to, opts, () => Task.CompletedTask, ct, autoTitle: false, depth: depth);
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
                    ". Reply with exactly NONE if there's nothing worth keeping."),
                LlmMessage.User($"Already known:\n{string.Join("\n", known.Select(k => "- " + k))}\n\n" +
                                $"User said:\n{Clip(lastUser.Content, 2000)}\n\nAssistant replied:\n{Clip(reply.Content, 1500)}"),
            };
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.1, 200, 4096, false), cts.Token))
                sb.Append(c.Content);

            var saved = new List<string>();
            foreach (var raw in sb.ToString().Split('\n'))
            {
                var line = raw.Trim().TrimStart('-', '*', ' ');
                if (line.Length < 6 || line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
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
                if (saved.Count == 3) break;
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

    private async Task AutoTitleAsync(Conversation conv, ILlmClient client, string model, Func<Task> onUpdate)
    {
        try
        {
            var first = conv.Messages.First(m => m.Role == ChatRole.User).Content;
            var prompt = new List<LlmMessage>
            {
                LlmMessage.System("You write short chat titles. Reply with ONLY a 2-6 word title, no quotes, no punctuation at the end."),
                LlmMessage.User($"Title for a chat that starts with:\n\n{Clip(first, 600)}"),
            };
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var c in client.StreamChatAsync(new LlmRequest(model, prompt, 0.3, 32, 2048, false), cts.Token))
                sb.Append(c.Content);
            var title = sb.ToString().Trim().Trim('"', '\'', '.', '*', '#').Split('\n')[0].Trim();
            if (title.Length is > 0 and < 80)
            {
                conv.Title = title;
                await store.SaveAsync(conv);
                await onUpdate();
            }
        }
        catch
        {
            conv.Title = Clip(conv.Messages.First(m => m.Role == ChatRole.User).Content, 40);
            await store.SaveAsync(conv);
        }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
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
        string.Join(", ", others.Select(b => $"{b.Name} ({b.Title})"));
    public JsonObject Parameters => Schema.Object(
        ("bot", "string", "Exact name of the bot to message", true),
        ("message", "string", "The request, with any context they need", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var name = (args.Str("bot") ?? "").Trim().TrimStart('@');
        var target = others.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                     ?? others.FirstOrDefault(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && name.Length > 2);
        if (target is null)
            return ToolResult.Error($"No bot named '{name}'. Bots: {string.Join(", ", others.Select(b => b.Name))}");
        var message = args.Str("message");
        if (string.IsNullOrWhiteSpace(message)) return ToolResult.Error("message is required");
        // Pass on the chat's model, unless the sender runs on its own server (its model name means nothing elsewhere).
        var model = ctx.Settings.ProviderFor(self).Id == ctx.Settings.ActiveProvider.Id ? ctx.Model : opts.Model;
        var reply = await chat.MessageBotAsync(self, target, message, opts with { Model = model }, ctx.Depth + 1, ct);
        return new ToolResult($"{target.Name} replied:\n\n{reply}");
    }
}
