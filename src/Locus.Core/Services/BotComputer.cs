namespace Locus.Core.Services;

/// <summary>One thing the user did while teaching a skill.</summary>
public sealed record RecordedStep(DateTimeOffset Time, string Kind, string Target, string? Value, string Url, bool Sensitive = false)
{
    public string Describe() => Kind switch
    {
        "navigate" => $"Go to {Url}",
        "click" => $"Click {Target}",
        "type" => Sensitive ? $"Type the password into {Target}" : $"Type \"{Value}\" into {Target}",
        "select" => $"Choose \"{Value}\" in {Target}",
        "key" => $"Press {Value} in {Target}",
        _ => $"{Kind} {Target} {Value}",
    };
}

public sealed record ComputerStatus(
    bool Available,
    bool Running,
    bool Headless,
    string? Url,
    string? Title,
    string? ActiveOwner,
    string? Error,
    string? UnavailableReason,
    IReadOnlyList<string>? RunningProfiles = null);

/// <summary>
/// The bots' shared computer: one persistent browser profile (cookies and sign-ins are shared by every bot)
/// plus a workspace folder. Implemented with Playwright on desktop/web hosts; unavailable on mobile.
/// </summary>
public interface IBotComputer
{
    ComputerStatus Status { get; }
    string WorkspaceDirectory { get; }
    event Action? Changed;
    /// <summary>
    /// Starts (or restarts in the other mode) a browser profile. headless=false opens a window for signing in.
    /// <paramref name="profile"/> is "shared" or a bot id (bots with their own browser).
    /// </summary>
    Task StartAsync(bool headless, string profile = "shared");
    Task StopAsync();
    /// <summary>PNG of the page a bot used most recently, or null when the browser isn't running.</summary>
    Task<byte[]?> ScreenshotAsync();
    /// <summary>Show-and-learn: opens a visible browser and records what the user does in it.</summary>
    Task StartRecordingAsync(string profile, string? startUrl);
    Task<List<RecordedStep>> StopRecordingAsync();
    bool IsRecording { get; }
    IReadOnlyList<RecordedStep> RecordedSteps { get; }

    /// <summary>Deletes a profile's cookies and sign-ins ("shared" or a bot id).</summary>
    Task ResetProfileAsync(string profile);
    /// <summary>Downloads Playwright's Chromium (user-initiated from the Computer page).</summary>
    Task<string> InstallBrowserAsync();
}

/// <summary>Used where there's no browser automation (iOS, Android, web host without local access).</summary>
public sealed class NoBotComputer(IAppPaths paths) : IBotComputer
{
    public ComputerStatus Status { get; } = new(false, false, true, null, null, null, null,
        "The bot computer's browser is available in the desktop app (and the web host with local access enabled).");
    public string WorkspaceDirectory { get; } = Path.Combine(paths.DataDirectory, "workspace");
    public event Action? Changed { add { } remove { } }
    public Task StartAsync(bool headless, string profile = "shared") => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<byte[]?> ScreenshotAsync() => Task.FromResult<byte[]?>(null);
    public Task<string> InstallBrowserAsync() => Task.FromResult(Status.UnavailableReason!);
    public Task ResetProfileAsync(string profile) => Task.CompletedTask;
    public Task StartRecordingAsync(string profile, string? startUrl) => throw new PlatformNotSupportedException(Status.UnavailableReason);
    public Task<List<RecordedStep>> StopRecordingAsync() => Task.FromResult(new List<RecordedStep>());
    public bool IsRecording => false;
    public IReadOnlyList<RecordedStep> RecordedSteps => [];
}
