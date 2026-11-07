using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Locus.Core.Llm;
using Locus.Core.Models;

namespace Locus.Core.Services;

/// <summary>
/// Brings what the user already taught ChatGPT or Claude into Locus's memory, so it doesn't start from scratch:
/// either pasted text (their saved memories or custom instructions) or a data export (.zip), from which a model
/// picks lasting facts and preferences out of the user's own messages. Nothing is saved without a preview first.
/// </summary>
public sealed class AssistantImport(ILlmClientFactory factory, SettingsStore settingsStore, MemoryStore memories)
{
    /// <summary>What to ask the other assistant, so it lists its memory in a form that pastes cleanly.</summary>
    public const string AskOtherAssistant =
        "List everything you remember about me (saved memories, custom instructions, preferences, people, projects), " +
        "one short fact per line, no headings or commentary.";

    private const int MaxConversations = 80, MaxBatches = 8, BatchChars = 12000, MaxFactChars = 300;

    /// <summary>A conversation's title, when it was last active, and what the user wrote in it.</summary>
    public sealed record ExportChat(string Title, DateTimeOffset When, List<string> UserMessages);

    public sealed record Found(string Source, int Chats, List<string> Facts);

    // ------------------------------------------------------------------ pasted text

    /// <summary>Pasted memories: one fact per line (bullets and numbering stripped), with ones Locus knows left out.</summary>
    public async Task<Found> FromTextAsync(string text)
    {
        var facts = AgentImporter.ParseMemories(text).Select(f => Clip(f, MaxFactChars)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Found("pasted text", 0, await NewOnlyAsync(facts));
    }

    // ------------------------------------------------------------------ data exports

    /// <summary>Reads a ChatGPT or Claude export (.zip, or its conversations.json) and asks a model what's worth remembering.</summary>
    public async Task<Found> FromExportAsync(Stream file, string fileName, Func<string, Task>? progress = null, CancellationToken ct = default)
    {
        progress ??= _ => Task.CompletedTask;
        await progress("Reading the export…");
        var (source, chats) = await ReadExportAsync(file, fileName, ct);
        if (chats.Count == 0) throw new InvalidOperationException("No conversations found. Upload the .zip from ChatGPT or Claude's data export, or its conversations.json.");
        var recent = chats.Where(c => c.UserMessages.Count > 0).OrderByDescending(c => c.When).Take(MaxConversations).ToList();

        var batches = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in recent)
        {
            var block = $"## {Clip(c.Title, 80)} ({c.When:yyyy-MM-dd})\n" + string.Join("\n", c.UserMessages.Take(12).Select(m => "- " + Clip(m.ReplaceLineEndings(" "), 400))) + "\n";
            if (sb.Length + block.Length > BatchChars && sb.Length > 0) { batches.Add(sb.ToString()); sb.Clear(); if (batches.Count == MaxBatches) break; }
            sb.Append(block);
        }
        if (sb.Length > 0 && batches.Count < MaxBatches) batches.Add(sb.ToString());

        var settings = await settingsStore.GetAsync();
        var client = factory.CreateForBot(settings, null);
        var model = await ModelPicker.DefaultForAsync(factory, settings.ProviderFor(null)) ?? throw new InvalidOperationException("No model is available.");
        using var usage = UsageScope.Begin("Import");
        var facts = new List<string>();
        for (var i = 0; i < batches.Count; i++)
        {
            await progress($"Picking out what's worth remembering ({i + 1} of {batches.Count})…");
            facts.AddRange(Facts(await AskAsync(client, model, batches[i], ct)));
        }
        facts = facts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Found(source, recent.Count, await NewOnlyAsync(facts));
    }

    /// <summary>Saves the facts the user ticked as shared memories. Returns how many were new.</summary>
    public async Task<int> SaveAsync(IEnumerable<string> facts)
    {
        var n = 0;
        foreach (var f in facts.Select(f => f.Trim()).Where(f => f.Length >= 4))
        {
            if (await memories.KnowsAsync(f, null)) continue;
            await memories.AddAsync(Clip(f, MaxFactChars));
            n++;
        }
        return n;
    }

    private async Task<List<string>> NewOnlyAsync(List<string> facts)
    {
        var fresh = new List<string>();
        foreach (var f in facts)
            if (!await memories.KnowsAsync(f, null)) fresh.Add(f);
        return fresh;
    }

    private static async Task<string> AskAsync(ILlmClient client, string model, string chats, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        var sb = new StringBuilder();
        await foreach (var c in client.StreamChatAsync(new LlmRequest(model, [
            LlmMessage.System(
                "Below is what a user wrote to another AI assistant, grouped by conversation. Pick out LASTING facts worth remembering about " +
                "them: who they are, their work and projects, people in their life, preferences (including how they like answers), routines " +
                "and goals. Skip one-off tasks and requests (\"wants help planning X\"), questions about general topics, and anything sensitive like passwords, account numbers or " +
                "health details they didn't present as lasting. Write each as a short third-person fact (\"Works as a 3D artist.\"), one " +
                "per line, no numbering or commentary. If nothing is worth keeping, reply NONE."),
            LlmMessage.User(chats)], 0.1, 1200, 16384, false), cts.Token))
            sb.Append(c.Content);
        return sb.ToString();
    }

    public static List<string> Facts(string answer) =>
        answer.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Select(l => l.Length > 2 && char.IsDigit(l[0]) && l.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4 ? l[(l.IndexOf(". ", StringComparison.Ordinal) + 2)..] : l)
            .Where(l => l.Length is >= 6 and <= MaxFactChars && !l.Equals("NONE", StringComparison.OrdinalIgnoreCase) && !l.EndsWith(':'))
            .ToList();

    /// <summary>The conversations in an export: ChatGPT's (mapping of message nodes) or Claude's (chat_messages).</summary>
    public static async Task<(string source, List<ExportChat> chats)> ReadExportAsync(Stream file, string fileName, CancellationToken ct = default)
    {
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return await ReadConversationsAsync(file, ct);
        // ZipArchive needs a seekable stream; browser uploads aren't.
        Stream seekable = file;
        string? temp = null;
        if (!file.CanSeek)
        {
            temp = Path.Combine(Path.GetTempPath(), "locus-import-" + Guid.NewGuid().ToString("N") + ".zip");
            await using (var t = File.Create(temp)) await file.CopyToAsync(t, ct);
            seekable = File.OpenRead(temp);
        }
        try
        {
            using var zip = new ZipArchive(seekable, ZipArchiveMode.Read);
            var entry = zip.Entries.Where(e => e.Name.Equals("conversations.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Length).FirstOrDefault()
                ?? throw new InvalidOperationException("This .zip has no conversations.json. Is it a ChatGPT or Claude data export?");
            await using var s = entry.Open();
            return await ReadConversationsAsync(s, ct);
        }
        finally
        {
            if (temp is not null) { await seekable.DisposeAsync(); try { File.Delete(temp); } catch { } }
        }
    }

    private static async Task<(string source, List<ExportChat> chats)> ReadConversationsAsync(Stream s, CancellationToken ct)
    {
        var chats = new List<ExportChat>();
        string source = "an export";
        await foreach (var c in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(s, cancellationToken: ct))
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            if (c.TryGetProperty("mapping", out var mapping) && mapping.ValueKind == JsonValueKind.Object)
            {
                source = "ChatGPT";
                chats.Add(ChatGpt(c, mapping));
            }
            else if (c.TryGetProperty("chat_messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                source = "Claude";
                chats.Add(Claude(c, messages));
            }
        }
        return (source, chats);
    }

    private static ExportChat ChatGpt(JsonElement c, JsonElement mapping)
    {
        var said = new List<(double at, string text)>();
        foreach (var node in mapping.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("author", out var a) || Str(a, "role") != "user") continue;
            if (!m.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
            var text = string.Join("\n", parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString())).Trim();
            if (text.Length > 0) said.Add((Num(m, "create_time"), text));
        }
        var when = Num(c, "update_time") is > 0 and var u ? u : Num(c, "create_time");
        return new ExportChat(Str(c, "title") ?? "", when > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(when * 1000)) : DateTimeOffset.MinValue,
            said.OrderBy(x => x.at).Select(x => x.text).ToList());
    }

    private static ExportChat Claude(JsonElement c, JsonElement messages)
    {
        var said = new List<string>();
        foreach (var m in messages.EnumerateArray())
        {
            if (Str(m, "sender") != "human") continue;
            var text = Str(m, "text");
            if (string.IsNullOrWhiteSpace(text) && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                text = string.Join("\n", content.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text")));
            if (!string.IsNullOrWhiteSpace(text)) said.Add(text.Trim());
        }
        var when = DateTimeOffset.TryParse(Str(c, "updated_at") ?? Str(c, "created_at"), out var w) ? w : DateTimeOffset.MinValue;
        return new ExportChat(Str(c, "name") ?? "", when, said);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}
