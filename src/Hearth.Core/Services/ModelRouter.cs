using System.Text.RegularExpressions;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>
/// Automatic model choice (Settings → Models → Automatic model choice): each message goes to the fast model
/// (local, free, private) or the strong one, decided on the spot from the message itself, with no extra model call.
/// </summary>
public static partial class ModelRouter
{
    /// <summary>Whether this message needs the strong model, and why (shown in the reply's details).</summary>
    public static (bool strong, string reason) Decide(string message, int attachments, bool codeSession, bool researchBot)
    {
        var text = message.Trim();
        if (codeSession) return (true, "code session");
        if (attachments > 0) return (true, "has attachments");
        if (text.Contains("```")) return (true, "contains code");
        if (text.Length > 700) return (true, "long request");
        if (Hard().Match(text) is { Success: true } m) return (true, $"\"{m.Value.ToLowerInvariant()}\" needs careful work");
        if (text.Count(c => c == '?') >= 3 || Steps().Matches(text).Count >= 3) return (true, "several parts");
        if (researchBot && text.Length > 60) return (true, "research or engineering bot");
        return (false, text.Length < 160 ? "short message" : "everyday request");
    }

    /// <summary>The provider and model for a message, or null to keep the chat's own choice.</summary>
    public static (ProviderProfile provider, string model, string reason)? Pick(AppSettings settings, Bot? bot, string message, int attachments,
        bool codeSession) => Pick(settings, bot, message, attachments, codeSession, routine: null, hint: null, pictures: 0);

    /// <summary>
    /// The provider and model for a turn, or null to keep the chat's own choice. Besides the message itself this weighs
    /// what the turn is for: heartbeats and a task's wake checks go to the fast model, planning and getting a task
    /// unstuck to the strong one (<paramref name="hint"/>), and a message with a picture to the vision model when one is set.
    /// </summary>
    public static (ProviderProfile provider, string model, string reason)? Pick(AppSettings settings, Bot? bot, string message, int attachments,
        bool codeSession, string? routine, RouteHint? hint, int pictures)
    {
        var r = settings.ModelRouting;
        if (!r.Enabled || bot is { Model.Length: > 0 } || bot?.ProviderId is not null) return null; // pinned bots keep theirs
        if (pictures > 0 && Vision(settings) is { } eyes) return (eyes.provider, eyes.model, "vision model: has a picture");
        var (strong, reason) = hint is not null ? (hint.Strong, hint.Reason)
            : routine == "Heartbeat" ? (false, "heartbeat check")
            : Decide(message, attachments, codeSession, bot is not null && ResearchRole().IsMatch(bot.Title + " " + bot.Name));
        var providerId = strong ? r.StrongProviderId : r.FastProviderId;
        var model = strong ? r.StrongModel : r.FastModel;
        var provider = settings.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null || string.IsNullOrWhiteSpace(model)) return null;
        return (provider, model, $"{(strong ? "strong" : "fast")} model: {reason}");
    }

    /// <summary>
    /// The vision model to switch to once a screenshot comes back in the middle of a reply, or null (routing off, no
    /// vision model set, or the bot is pinned to its own model).
    /// </summary>
    public static (ProviderProfile provider, string model)? VisionFor(AppSettings settings, Bot? bot) =>
        !settings.ModelRouting.Enabled || bot is { Model.Length: > 0 } || bot?.ProviderId is not null ? null : Vision(settings);

    private static (ProviderProfile provider, string model)? Vision(AppSettings settings)
    {
        var r = settings.ModelRouting;
        if (string.IsNullOrWhiteSpace(r.VisionModel) || settings.Providers.FirstOrDefault(p => p.Id == r.VisionProviderId) is not { } p) return null;
        return (p, r.VisionModel.Trim());
    }

    [GeneratedRegex(@"\b(research|analy[sz]e|analysis|compare|comparison|evaluate|assess|strategy|strategic|plan (out|my|the|a)|write (a|an|the|me a) (report|proposal|plan|brief|letter|cover letter|essay|article|document|memo)|draft (a|an|the) (letter|proposal|report|cover letter)|debug|refactor|implement|architecture|design (a|an|the)|prove|derive|negotiat|tailor|résumé|resume|cover letter|interview prep|due diligence|pros and cons|trade-?offs?|step[- ]by[- ]step|in depth|deep dive|thorough)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hard();

    [GeneratedRegex(@"(^|\n)\s*(\d+[.)]|[-*•])\s+")] private static partial Regex Steps();
    [GeneratedRegex(@"research|engineer|develop|analyst|coder|scientist", RegexOptions.IgnoreCase)] private static partial Regex ResearchRole();
}
