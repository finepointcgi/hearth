namespace Hearth.Core.Models;

/// <summary>Supplied by each host so features and hints match the platform.</summary>
/// <param name="WebhookBaseUrl">Where /hooks/{id} is served: "" for the page's own origin (web host),
/// a URL for the desktop listener, null when this platform can't receive webhooks.</param>
/// <param name="CanRunProcesses">Local process tools (Python, stdio MCP servers). False on iOS/Android and,
/// by default, on the web host where it would let any visitor run code on the server.</param>
public sealed record PlatformInfo(
    string Name,
    bool IsMobile,
    bool IsAndroidEmulator = false,
    bool CanRunProcesses = true,
    string? WebhookBaseUrl = null)
{
    /// <summary>"" = same origin as the page (web host); null = webhooks not available on this platform.</summary>
    public bool SupportsWebhooks => WebhookBaseUrl is not null;
}
