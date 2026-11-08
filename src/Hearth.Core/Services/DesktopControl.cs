using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Computer use: lets bots see the screen and drive the mouse/keyboard (desktop hosts only).</summary>
public interface IDesktopControl
{
    bool Supported { get; }
    /// <summary>Null when ready; otherwise what the user needs to fix (OS permissions, missing tools).</summary>
    string? Problem { get; }
    void RequestPermissions();
    Task<ImageAttachment> ScreenshotAsync();
    /// <summary>Last action a bot took, for the Computer page.</summary>
    string? LastAction { get; }
    event Action? Changed;
}

public sealed record SandboxInfo(bool Running, string? ViewUrl, string? Status);

/// <summary>Private Linux desktops in Docker, one per bot (desktop hosts with Docker only).</summary>
public interface ISandboxDesktops
{
    /// <summary>Null when Docker is ready; otherwise what to install or start.</summary>
    Task<string?> ProblemAsync();
    Task<SandboxInfo> InfoAsync(string botId);
    /// <summary>Builds the image if needed and starts the bot's desktop; returns its live view.</summary>
    Task<SandboxInfo> StartAsync(string botId, CancellationToken ct = default);
    Task StopAsync(string botId);
    /// <summary>Deletes the desktop and its saved files/sign-ins.</summary>
    Task ResetAsync(string botId);
    Task<ImageAttachment> ScreenshotAsync(string botId);
    event Action? Changed;
}

public sealed class NoSandboxDesktops : ISandboxDesktops
{
    private const string Reason = "Sandboxed desktops need the desktop app or web server on a computer with Docker.";
    public Task<string?> ProblemAsync() => Task.FromResult<string?>(Reason);
    public Task<SandboxInfo> InfoAsync(string botId) => Task.FromResult(new SandboxInfo(false, null, Reason));
    public Task<SandboxInfo> StartAsync(string botId, CancellationToken ct = default) => throw new PlatformNotSupportedException(Reason);
    public Task StopAsync(string botId) => Task.CompletedTask;
    public Task ResetAsync(string botId) => Task.CompletedTask;
    public Task<ImageAttachment> ScreenshotAsync(string botId) => throw new PlatformNotSupportedException(Reason);
    public event Action? Changed { add { } remove { } }
}

public sealed class NoDesktopControl : IDesktopControl
{
    public bool Supported => false;
    public string? Problem => "Computer use is available in the desktop app on macOS, Windows and Linux.";
    public void RequestPermissions() { }
    public Task<ImageAttachment> ScreenshotAsync() => throw new PlatformNotSupportedException(Problem);
    public string? LastAction => null;
    public event Action? Changed { add { } remove { } }
}
