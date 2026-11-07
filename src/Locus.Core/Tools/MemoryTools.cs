using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;

namespace Locus.Core.Tools;

public sealed class SaveMemoryTool(MemoryStore store) : ITool
{
    public string Name => "save_memory";
    public string DisplayName => "Remember";
    public string Icon => "🧠";
    public string Description =>
        "Save a durable fact about the user (preferences, projects, people, setup) so you remember it in future chats. " +
        "Only save things the user would want remembered; keep each fact short and self-contained.";
    public JsonObject Parameters => Schema.Object(
        ("fact", "string", "e.g. 'Prefers C# and Blazor for UI work'", true),
        ("about_user", "boolean", "Bots only: true to share this fact with all bots (a fact about the user), false for your own notes", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var fact = args.Str("fact");
        if (string.IsNullOrWhiteSpace(fact)) return ToolResult.Error("fact is required");
        // Bots keep their own memory; facts about the user are also shared when the bot is allowed to share.
        var aboutUser = args.Bool("about_user");
        var shared = ctx.Bot is null || (aboutUser && ctx.Bot.Memory.ShareWhatItLearns);
        var scope = shared ? null : ctx.Bot!.Id;
        if (await store.KnowsAsync(fact, scope)) return new ToolResult("Already remembered.");
        var m = await store.AddAsync(fact, scope);
        lock (ctx.Reply.LearnedItems) ctx.Reply.LearnedItems.Add(new LearnedItem { Kind = LearnedItem.Memory, Text = m.Text, Ref = m.Id, Scope = scope });
        return new ToolResult($"Saved memory {m.Id}: {m.Text}");
    }
}

public sealed class DeleteMemoryTool(MemoryStore store) : ITool
{
    public string Name => "delete_memory";
    public string DisplayName => "Forget";
    public string Icon => "🧽";
    public string Description => "Delete a saved memory by id when the user asks you to forget it or it is no longer true.";
    public JsonObject Parameters => Schema.Object(("id", "string", "Memory id shown in the system prompt", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) =>
        await store.DeleteAsync(args.Str("id") ?? "") || (ctx.Bot is not null && await store.DeleteAsync(args.Str("id") ?? "", ctx.Bot.Id))
            ? new ToolResult("Memory deleted.")
            : ToolResult.Error("No memory with that id.");
}

public sealed class RecallMemoryTool(MemoryStore store) : ITool
{
    public string Name => "recall_memory";
    public string DisplayName => "Recall";
    public string Icon => "🔍";
    public string Description =>
        "Search everything you've remembered (including older memories not listed in your prompt) by keywords.";
    public JsonObject Parameters => Schema.Object(("query", "string", "Keywords, e.g. 'birthday sister' or 'project deadline'", true));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var q = args.Str("query") ?? "";
        var hits = new List<(string where, Memory m)>();
        if (ctx.Bot is not null) hits.AddRange((await store.SearchAsync(q, ctx.Bot.Id, 15)).Select(m => ("your notes", m)));
        if (ctx.Bot is null || ctx.Bot.Memory.UseSharedMemory)
            hits.AddRange((await store.SearchAsync(q, null, 15)).Select(m => ("about the user", m)));
        return hits.Count == 0
            ? new ToolResult("Nothing remembered about that.")
            : new ToolResult(string.Join("\n", hits.Select(h => $"- [{h.where}] {h.m.Id}: {h.m.Text} ({h.m.CreatedAt:yyyy-MM-dd})")));
    }
}

/// <summary>Searches past conversations (like Hermes's session search).</summary>
public sealed class SearchPastChatsTool(ChatHistoryIndex index) : ITool
{
    public string Name => "search_past_chats";
    public string DisplayName => "Search past chats";
    public string Icon => "🗂️";
    public string Description =>
        "Search earlier conversations with the user by keywords, e.g. when they refer to something discussed before " +
        "(\"what did we decide about…\", \"like last time\"). Returns the best-matching chats with an excerpt; " +
        "use read_past_chat to read more of one.";
    public JsonObject Parameters => Schema.Object(
        ("query", "string", "Distinctive keywords, e.g. 'newsletter pricing decision'", true),
        ("days", "integer", "Only search the last N days (optional)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var days = args.Num("days") is { } d && d > 0 ? (int?)d : null;
        var hits = await index.SearchAsync(args.Str("query") ?? "", ctx.Bot?.Id, 6, ctx.Conversation.Id, days);
        return hits.Count == 0
            ? new ToolResult("No earlier chats match that. Try other keywords.")
            : new ToolResult(string.Join("\n\n", hits.Select(h => $"[{h.ConversationId}] \"{h.Title}\" — {h.When:yyyy-MM-dd}\n{h.Excerpt}")));
    }
}

/// <summary>Reads the part of a past conversation around what matched.</summary>
public sealed class ReadPastChatTool(ConversationStore store) : ITool
{
    public string Name => "read_past_chat";
    public string DisplayName => "Read past chat";
    public string Icon => "📜";
    public string Description => "Read an earlier conversation found with search_past_chats (by its id), optionally around a keyword.";
    public JsonObject Parameters => Schema.Object(
        ("id", "string", "Conversation id from search_past_chats", true),
        ("around", "string", "Keyword to center on (optional; default: the end of the chat)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var c = await store.GetAsync((args.Str("id") ?? "").Trim('[', ']', ' '));
        if (c is null || (ctx.Bot is not null && !c.BotIds.Contains(ctx.Bot.Id))) return ToolResult.Error("No chat with that id that you can read.");
        var msgs = c.Messages.Where(m => m.Content.Length > 0).ToList();
        var terms = ChatHistoryIndex.Tokens(args.Str("around") ?? "");
        var center = terms.Count == 0 ? msgs.Count - 1
            : msgs.FindIndex(m => terms.Any(t => m.Content.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (center < 0) center = msgs.Count - 1;
        var sb = new System.Text.StringBuilder($"\"{c.Title}\" ({c.UpdatedAt:yyyy-MM-dd})\n");
        foreach (var m in msgs.Skip(Math.Max(0, center - 4)).Take(9))
        {
            var who = m.Role == ChatRole.User ? "User" : "Assistant";
            var text = m.Content.Length > 900 ? m.Content[..900] + "…" : m.Content;
            sb.Append($"\n{who} ({m.CreatedAt:MMM d}): {text}\n");
            if (sb.Length > 6000) break;
        }
        return new ToolResult(sb.ToString());
    }
}
