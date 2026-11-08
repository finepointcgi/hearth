using System.Security.Cryptography;
using System.Text;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Settings for Hearth's OpenAI-compatible API (for other apps, scripts and the editor bridge).</summary>
public sealed class ApiSettings
{
    public bool Enabled { get; set; }
    /// <summary>Bearer key callers must send (encrypted at rest).</summary>
    public string? Key { get; set; }

    public static string NewKey() => "hearth-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public bool Accepts(string? authorization)
    {
        if (!Enabled || string.IsNullOrEmpty(Key) || authorization is null) return false;
        var given = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : authorization.Trim();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Key));
    }
}

public sealed record ApiMessage(string Role, string Content);

/// <summary>
/// Lets other apps talk to Hearth and its bots the way they'd talk to OpenAI: model "hearth" is plain Hearth,
/// "hearth/&lt;bot&gt;" is that bot (with its instructions, memory, skills and tools). Each request is a fresh,
/// unattended run — nothing that needs approval runs — and leaves no chat behind.
/// </summary>
public sealed class OpenAiApi(ChatService chat, BotStore bots, ConversationStore store, ModelPicker models)
{
    public const string PlainModel = "hearth";
    /// <summary>The model name from before the rename, still accepted.</summary>
    private const string OldPlainModel = Bot.PlainId;

    public async Task<List<(string id, string description)>> ModelsAsync()
    {
        var list = new List<(string, string)> { (PlainModel, "Hearth") };
        foreach (var b in await bots.ListAsync()) list.Add((ModelId(b), $"{b.Name} — {b.Title}"));
        return list;
    }

    public static string ModelId(Bot b) => $"{PlainModel}/{Bot.SlugName(b.Name)}";

    /// <summary>Runs a completion. <paramref name="onDelta"/> receives new text as it streams. Returns the answer.</summary>
    public async Task<string> CompleteAsync(string model, IReadOnlyList<ApiMessage> messages, Func<string, Task>? onDelta, CancellationToken ct)
    {
        Bot? bot = null;
        if (model.StartsWith(OldPlainModel, StringComparison.OrdinalIgnoreCase)) model = PlainModel + model[OldPlainModel.Length..]; // from before the rename
        if (!string.Equals(model, PlainModel, StringComparison.OrdinalIgnoreCase))
        {
            var slug = model.StartsWith(PlainModel + "/", StringComparison.OrdinalIgnoreCase) ? model[(PlainModel.Length + 1)..] : model;
            bot = (await bots.ListAsync()).FirstOrDefault(b => Bot.SlugName(b.Name).Equals(slug, StringComparison.OrdinalIgnoreCase))
                  ?? throw new KeyNotFoundException($"No model '{model}'. Use 'hearth' or one of the bots (GET /v1/models).");
        }
        if (!messages.Any(m => m.Role == "user")) throw new ArgumentException("Send at least one user message.");
        var llmModel = await models.PickAsync(bot) ?? throw new InvalidOperationException("No model server is available.");

        var conv = new Conversation { Title = "API", BotIds = bot is null ? [] : [bot.Id] };
        var system = string.Join("\n\n", messages.Where(m => m.Role is "system" or "developer").Select(m => m.Content.Trim()).Where(c => c.Length > 0));
        var firstUser = true;
        foreach (var m in messages.Where(m => m.Role is "user" or "assistant"))
        {
            if (m.Role == "assistant") { conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = m.Content, BotId = bot?.Id }); continue; }
            var text = firstUser && system.Length > 0 ? $"[Instructions from the app you're being used through]\n{system}\n\n{m.Content}" : m.Content;
            conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text, RoutineName = "API" });
            firstUser = false;
        }

        var sent = 0;
        try
        {
            var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(llmModel, false, false, true), async () =>
            {
                if (onDelta is null) return;
                var current = conv.Messages.LastOrDefault(x => x is { Role: ChatRole.Assistant, IsStreaming: true })?.Content;
                if (current is null || current.Length <= sent) return;
                var delta = current[sent..];
                sent = current.Length;
                await onDelta(delta);
            }, ct, autoTitle: false, unattended: true, learn: false);
            if (reply?.Error is { } error) throw new InvalidOperationException(error);
            var final = reply?.Content ?? "";
            if (onDelta is not null && final.Length > sent) await onDelta(final[sent..]);
            return final;
        }
        finally { try { await store.DeleteAsync(conv.Id); } catch { } }
    }
}
