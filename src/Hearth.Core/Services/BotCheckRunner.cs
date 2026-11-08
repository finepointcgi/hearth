using System.Diagnostics;
using System.Text;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Runs a bot's checks: asks each question in a throwaway chat and judges the answer.</summary>
public sealed class BotCheckRunner(ChatService chat, ConversationStore conversations, ModelPicker models, SettingsStore settingsStore,
    ILlmClientFactory factory)
{
    public async Task<BotCheckResult> RunAsync(Bot bot, BotCheck check, CancellationToken ct = default)
    {
        var model = await models.PickAsync(bot) ?? throw new InvalidOperationException("No model is available.");
        var conv = new Conversation { Title = $"Check: {bot.Name}", BotIds = [bot.Id] };
        conv.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = check.Question, RoutineName = "Bot check" });
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await chat.RunAgentAsync(conv, bot, new SendOptions(model, false, false, true), () => Task.CompletedTask, ct,
                autoTitle: false, unattended: true, learn: false);
            sw.Stop();
            if (reply is null || reply.Error is not null)
                return new(false, "The bot didn't answer: " + (reply?.Error ?? "no reply"), "", DateTimeOffset.Now, model, sw.ElapsedMilliseconds);
            var answer = reply.Content;
            var missing = (check.MustInclude ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => !answer.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                return new(false, $"Missing: {string.Join(", ", missing)}", answer, DateTimeOffset.Now, model, sw.ElapsedMilliseconds);
            if (string.IsNullOrWhiteSpace(check.Expect))
                return new(true, "Answered (no expectation to judge against).", answer, DateTimeOffset.Now, model, sw.ElapsedMilliseconds);
            var (pass, reason) = await JudgeAsync(check, answer, reply.ToolCalls, ct);
            return new(pass, reason, answer, DateTimeOffset.Now, model, sw.ElapsedMilliseconds);
        }
        finally
        {
            await conversations.DeleteAsync(conv.Id); // throwaway
        }
    }

    private async Task<(bool pass, string reason)> JudgeAsync(BotCheck check, string answer, List<ToolCall> tools, CancellationToken ct)
    {
        using var scope = UsageScope.Begin("Bot check");
        var settings = await settingsStore.GetAsync();
        var model = await models.PickAsync(null) ?? throw new InvalidOperationException("No model is available to judge.");
        var used = tools.Count == 0 ? "none" : string.Join(", ", tools.Select(t => t.Name).Distinct());
        var prompt = new List<LlmMessage>
        {
            LlmMessage.System("You grade an AI assistant's answer against what the user expects. Be fair: judge whether the answer does what is expected, " +
                "not its style. Reply with exactly one line: PASS: <short reason> or FAIL: <short reason>."),
            LlmMessage.User($"Question:\n{check.Question}\n\nA good answer:\n{check.Expect}\n\nTools the assistant used: {used}\n\nThe answer:\n{(answer.Length > 6000 ? answer[..6000] + "…" : answer)}"),
        };
        var sb = new StringBuilder();
        await foreach (var c in factory.CreateForChat(settings).StreamChatAsync(new LlmRequest(model, prompt, 0, 200, 8192, false), ct))
            sb.Append(c.Content);
        var text = sb.ToString().Trim();
        var line = text.Split('\n').Select(l => l.Trim().TrimStart('*')).FirstOrDefault(l => l.StartsWith("PASS", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)) ?? text;
        var pass = line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        var reason = line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim() : line;
        return (pass, reason.Length == 0 ? (pass ? "Looks right." : "Doesn't match the expectation.") : reason);
    }
}
