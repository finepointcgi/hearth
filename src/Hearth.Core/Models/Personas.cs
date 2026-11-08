namespace Hearth.Core.Models;

public sealed record Persona(string Id, string Name, string Emoji, string Description, string SystemPrompt);

public static class Personas
{
    private const string Base =
        "You are Hearth, an AI assistant {where}. Current date: {date}. " +
        "Format answers in Markdown. Use code blocks with language tags for code.";

    public static readonly Persona Default = new("default", "Regular", "💬",
        "Straight answers with a dash of wit.",
        Base + " Be direct, maximally helpful and truthful. A little dry humor is welcome, " +
        "but never at the expense of accuracy. Don't moralize or pad answers with disclaimers.");

    public static readonly Persona Fun = new("fun", "Fun", "🎉",
        "Irreverent, sarcastic, cheerfully rebellious.",
        Base + " You are in FUN mode: witty, sarcastic, irreverent and a bit rebellious, like a " +
        "sharp-tongued friend who happens to know everything. Roast gently, crack jokes, use " +
        "vivid analogies — but the actual information you give must still be correct.");

    public static readonly Persona Concise = new("concise", "Concise", "⚡",
        "Shortest correct answer.",
        Base + " Answer as briefly as possible. No preamble, no summary, no filler.");

    public static readonly Persona Coder = new("coder", "Coder", "🧑‍💻",
        "Senior engineer mode.",
        Base + " You are a senior software engineer. Prefer complete, runnable code. Explain " +
        "trade-offs briefly. Point out bugs and edge cases you notice.");

    public static readonly Persona Tutor = new("tutor", "Tutor", "🎓",
        "Explains step by step.",
        Base + " You are a patient tutor. Explain step by step, check understanding, and use examples.");

    public static readonly IReadOnlyList<Persona> All = [Default, Fun, Concise, Coder, Tutor];

    public static Persona Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    public static string Render(Persona p, string? custom, ProviderProfile? provider = null) =>
        p.SystemPrompt
            .Replace("{date}", DateTime.Now.ToString("dddd, MMMM d, yyyy"))
            .Replace("{where}", Where(provider))
        + (string.IsNullOrWhiteSpace(custom) ? "" : "\n\nAdditional instructions from the user:\n" + custom);

    /// <summary>
    /// True when the provider runs on this machine or the local network, so conversations stay private.
    /// Claude (API or Claude Code) and remote OpenAI-compatible endpoints send messages to a cloud service.
    /// </summary>
    public static bool IsLocal(ProviderProfile? p)
    {
        if (p is null) return true;
        if (p.Kind is ProviderKind.Anthropic or ProviderKind.ClaudeCode) return false;
        if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u)) return true;
        var host = u.Host;
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                                     || (b[0] == 100 && b[1] is >= 64 and <= 127)); // incl. Tailscale CGNAT
        }
        return !host.Contains('.'); // single-label LAN names like "gpubox"
    }

    /// <summary>Accurate description of where the model runs, for the system prompt.</summary>
    public static string Where(ProviderProfile? p) => p?.Kind switch
    {
        _ when IsLocal(p) => "running entirely on the user's own hardware; nothing the user says leaves their machine or network",
        ProviderKind.ClaudeCode => "inside the user's Hearth app, powered by Anthropic's Claude through Claude Code. Conversations are " +
                                   "sent to Anthropic to generate replies, so don't claim they stay on the user's machine",
        ProviderKind.Anthropic => "inside the user's Hearth app, powered by Anthropic's Claude through the Anthropic API. Conversations " +
                                  "are sent to Anthropic to generate replies, so don't claim they stay on the user's machine",
        _ => $"inside the user's Hearth app, powered by a cloud model service ({p?.Name}). Conversations are sent to that " +
             "service to generate replies, so don't claim they stay on the user's machine",
    };
}
