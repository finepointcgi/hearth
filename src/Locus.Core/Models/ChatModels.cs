using System.Text.Json.Serialization;

namespace Locus.Core.Models;

public enum ChatRole { System, User, Assistant }

public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Reasoning text from "thinking" models (e.g. &lt;think&gt; blocks or a separate reasoning field).</summary>
    public string? Thinking { get; set; }
    /// <summary>Base64-encoded images (no data: prefix) for vision models.</summary>
    public List<ImageAttachment> Images { get; set; } = [];
    public List<SearchResult> Sources { get; set; } = [];
    /// <summary>Tools the assistant called while producing this reply (all rounds).</summary>
    public List<ToolCall> ToolCalls { get; set; } = [];
    /// <summary>Images produced by tools (image generation, MCP) during this reply.</summary>
    public List<ImageAttachment> GeneratedImages { get; set; } = [];
    /// <summary>Names of documents the user attached to this message.</summary>
    public List<string> AttachedFiles { get; set; } = [];
    /// <summary>What the answer check corrected before the reply was shown (null when nothing needed fixing).</summary>
    public string? Checked { get; set; }
    /// <summary>Extracted text of attached documents; sent to the model but not shown in the bubble.</summary>
    public string? AttachmentText { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public LlmStats? Stats { get; set; }
    /// <summary>For assistant messages in bot chats: which bot wrote it.</summary>
    public string? BotId { get; set; }
    /// <summary>For user-role messages sent by another bot (bot-to-bot messages) or a routine.</summary>
    public string? FromBotId { get; set; }
    public string? RoutineName { get; set; }
    /// <summary>Data from the event that triggered a routine (webhook body, file, feed item…).</summary>
    public string? EventPayload { get; set; }
    /// <summary>The message carries content from somewhere untrusted (a web page attached with @): runs are guarded.</summary>
    public string? UntrustedSource { get; set; }
    [JsonIgnore] public bool IsStreaming { get; set; }
    [JsonIgnore] public string? Error { get; set; }
    /// <summary>Memories saved automatically after this reply (shown under it).</summary>
    public List<string> Learned { get; set; } = [];
    /// <summary>Skills this reply created or improved ("saved /x", "improved /y").</summary>
    public List<string> SkillNotes { get; set; } = [];
    /// <summary>Everything this reply taught Locus (memories, people notes, skills…), each with what's needed to undo it.
    /// Shown under the reply. Older replies only have <see cref="Learned"/> and <see cref="SkillNotes"/>.</summary>
    public List<LearnedItem> LearnedItems { get; set; } = [];
    /// <summary>How this reply was produced: each model round and tool call with its time and tokens.</summary>
    public List<TraceStep> Trace { get; set; } = [];
    /// <summary>Informational note, e.g. "this model doesn't support tools".</summary>
    public string? Notice { get; set; }
    /// <summary>A team turn this message is part of: the quick reply, the work behind the answer, and the answer share it.</summary>
    public string? TeamTurn { get; set; }
    /// <summary>The work behind a team's answer (hand-outs, specialists' results, reviews and fixes): folded under the
    /// answer, left out of later turns' history and not sent to chat apps.</summary>
    public bool Behind { get; set; }
    /// <summary>The lead's quick "on it" reply, sent before the team starts working.</summary>
    public bool Ack { get; set; }
}

public sealed record ImageAttachment(string FileName, string MimeType, string Base64)
{
    public string DataUrl => $"data:{MimeType};base64,{Base64}";
}

public enum ToolCallStatus { Pending, AwaitingApproval, Running, Done, Error, Denied }

public sealed class ToolCall
{
    public string Id { get; set; } = "call_" + Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string? Result { get; set; }
    /// <summary>Why approval was asked for when it normally isn't (e.g. the run read untrusted content).</summary>
    public string? ApprovalReason { get; set; }
    public long DurationMs { get; set; }
    /// <summary>The change this call made can be undone from its card (for a day; see UndoLog).</summary>
    public bool Undoable { get; set; }
    public ToolCallStatus Status { get; set; }
    public int Round { get; set; }
    /// <summary>
    /// What the model wrote in this round before its calls (set on the round's first call): work in progress, shown
    /// with the steps rather than in the answer, and replayed to the model so it knows what it already said.
    /// </summary>
    public string? Preface { get; set; }
    /// <summary>Provider data for the turn that made this call (set on the round's first call), replayed verbatim.</summary>
    public string? ProviderState { get; set; }
    /// <summary>
    /// When set, the model is sent only this many characters of <see cref="Result"/>. Once a result is shortened it
    /// stays shortened, so the start of the prompt doesn't change from step to step and the model server can reuse it.
    /// </summary>
    public int? SentChars { get; set; }
    /// <summary>
    /// For message_bot: the teammate's reply (with its own steps and sources), shown in the chat as that bot's message
    /// instead of a raw tool result. Updated live while the teammate works.
    /// </summary>
    public ChatMessage? Handoff { get; set; }
    [JsonIgnore] public TaskCompletionSource<bool>? Approval { get; set; }
    /// <summary>While waiting for approval: remembers "always allow this tool for this bot" (null when not offered).</summary>
    [JsonIgnore] public Func<Task>? AlwaysAllow { get; set; }
    /// <summary>Who "always allow" applies to, for the button label.</summary>
    [JsonIgnore] public string? AlwaysAllowFor { get; set; }
    /// <summary>Screenshot returned by the tool: shown in its card and sent to vision models (latest only).</summary>
    [JsonIgnore] public ImageAttachment? Image { get; set; }
}

public sealed record SearchResult(string Title, string Url, string Snippet);

public sealed record LlmStats(int? PromptTokens, int? CompletionTokens, double? TokensPerSecond);

public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New chat";
    public string PersonaId { get; set; } = Personas.Default.Id;
    public string? Model { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public bool Pinned { get; set; }
    /// <summary>Empty = plain Locus chat; one id = direct chat with a bot; 2–6 = group chat.</summary>
    public List<string> BotIds { get; set; } = [];
    /// <summary>Set when a bot posts here on its own (routines, bot-to-bot messages).</summary>
    public bool Unread { get; set; }
    /// <summary>Set while a reply is being worked on, so a restart can pick it up again.</summary>
    public PendingRun? Running { get; set; }
    /// <summary>Summary of the older part of a long chat (sent instead of those messages to fit the model).</summary>
    public string? Summary { get; set; }
    /// <summary>The last message the summary covers.</summary>
    public string? SummaryThroughId { get; set; }
    /// <summary>What the chat is about (a short label, set automatically after the first reply or by the user); used to group chats.</summary>
    public string? Topic { get; set; }
    /// <summary>Set when this chat is a code session working in a folder (Claude Code-style).</summary>
    public CodeSession? Code { get; set; }
    /// <summary>
    /// Set on the behind-the-scenes chat a bot uses when another bot pulls it in with message_bot: the chat it was
    /// pulled into. These chats aren't listed (the exchange shows in that chat instead).
    /// </summary>
    public string? HandoffFrom { get; set; }
    /// <summary>The bot's checklist for a long task in this chat (update_plan), shown above the chat.</summary>
    public List<CodeTodo> Plan { get; set; } = [];
}

/// <summary>How a code session handles changes: ask for each one, apply file edits without asking, or only plan.</summary>
public enum CodePermissionMode { Ask, AcceptEdits, Plan }

/// <summary>A chat bound to a project folder: the agent explores, edits and runs commands there.</summary>
public sealed class CodeSession
{
    public string Directory { get; set; } = "";
    public CodePermissionMode Mode { get; set; } = CodePermissionMode.Ask;
    /// <summary>The agent's own task list for the current job (todo_write).</summary>
    public List<CodeTodo> Todos { get; set; } = [];

    /// <summary>Folder name shown in the UI and used as the chat's project.</summary>
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Directory.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : Directory;
}

public enum CodeTodoStatus { Pending, InProgress, Completed }

public sealed record CodeTodo(string Content, CodeTodoStatus Status);

/// <summary>A reply in progress: what it was started with, so it can be resumed after a restart.</summary>
public sealed record PendingRun(string Model, bool Think, bool DeepSearch, bool UseTools, DateTimeOffset StartedAt, int Resumes = 0);

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt, bool Pinned,
    IReadOnlyList<string> BotIds, bool Unread, string? Topic = null, string? Project = null, DateTimeOffset? CreatedAt = null);

public sealed record ModelCapabilities(bool Tools, bool Vision, bool Thinking, bool Embedding);

public sealed record ModelInfo(string Id, string? Family = null, long? SizeBytes = null, bool? SupportsVision = null)
{
    public string Display => SizeBytes is > 0 ? $"{Id} ({SizeBytes.Value / 1_000_000_000.0:0.0} GB)" : Id;
}

/// <summary>One step of a reply: a model round ("model") or a tool call ("tool").</summary>
public sealed record TraceStep(string Kind, string Name, long Ms, int? InputTokens = null, int? OutputTokens = null);

/// <summary>One thing a reply taught Locus, and how to take it back.</summary>
public sealed class LearnedItem
{
    public const string Memory = "memory", Person = "person", Vibe = "vibe", Thread = "thread", NewSkill = "skill", ImprovedSkill = "skill-improved";

    /// <summary><see cref="Memory"/>, <see cref="Person"/>, <see cref="Vibe"/>, <see cref="Thread"/>, <see cref="NewSkill"/> or <see cref="ImprovedSkill"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>What was learned, as shown ("Prefers short answers", "Shield AI: interview Friday", "/weekly-report").</summary>
    public string Text { get; set; } = "";
    /// <summary>The memory, person, relationship note, thread or skill it went into.</summary>
    public string? Ref { get; set; }
    /// <summary>Memory: the bot whose memory it's in (null = shared). Vibe: the bot whose note it is.</summary>
    public string? Scope { get; set; }
    /// <summary>Memory: the id of a shared copy also saved. Person and thread: the exact note text added.</summary>
    public string? Extra { get; set; }
    public bool Undone { get; set; }

    public string Icon => Kind switch { Memory => "🧠", Person => "👤", Vibe => "💬", Thread => "🧵", _ => "📒" };
}
