using Hearth.Core.Models;
using Hearth.Core.Services;

namespace Hearth.Computer.Desktop;

/// <summary>
/// Bridges the OS backend and the tools: takes downscaled screenshots and maps the model's image-pixel
/// coordinates back to screen points. Fail-safe: actions refuse to run while the user's mouse is parked in the
/// top-left corner of the screen.
/// </summary>
public sealed class DesktopController : IDesktopControl
{
    public const int ShotWidth = 1280;
    private readonly IDesktopBackend? _backend;
    private readonly bool _failSafe;

    public DesktopController() : this(DesktopBackends.ForCurrentOs(), failSafe: true) { }

    internal DesktopController(IDesktopBackend? backend, bool failSafe)
    {
        _backend = backend;
        _failSafe = failSafe;
    }
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _imgW, _imgH;

    public bool Supported => _backend is not null;
    public string? Problem => _backend is null ? "Computer use isn't supported on this operating system." : _backend.Problem();
    public string? LastAction { get; private set; }
    public event Action? Changed;

    public void RequestPermissions() => _backend?.RequestPermissions();

    private IDesktopBackend Backend => _backend ?? throw new PlatformNotSupportedException(Problem);

    public async Task<ImageAttachment> ScreenshotAsync()
    {
        var (bytes, mime) = await Backend.CaptureAsync(ShotWidth);
        (_imgW, _imgH) = ImageInfo.Size(bytes);
        return new ImageAttachment($"a {_imgW}×{_imgH} px screenshot", mime, Convert.ToBase64String(bytes));
    }

    /// <summary>Converts screenshot pixel coordinates to screen points.</summary>
    public async Task<(double x, double y)> ToScreenAsync(double ix, double iy)
    {
        if (_imgW == 0) await ScreenshotAsync();
        var (sw, sh) = Backend.ScreenSize();
        return (Math.Clamp(ix, 0, _imgW) * sw / _imgW, Math.Clamp(iy, 0, _imgH) * sh / _imgH);
    }

    /// <summary>Runs one input action (serialized), then waits for the UI to react.</summary>
    public async Task ActAsync(string description, Action<IDesktopBackend> action)
    {
        await _lock.WaitAsync();
        try
        {
            var (cx, cy) = _failSafe ? Backend.CursorPosition() : (100, 100);
            if (cx < 4 && cy < 4)
                throw new InvalidOperationException(
                    "Paused: the mouse is in the top-left corner of the screen (the computer-use fail-safe). " +
                    "Ask the user whether to continue.");
            await Task.Run(() => action(Backend));
            LastAction = $"{DateTime.Now:HH:mm:ss} {description}";
            Changed?.Invoke();
            await Task.Delay(700);
        }
        finally { _lock.Release(); }
    }
}
