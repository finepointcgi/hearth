using System.Diagnostics;
using System.Globalization;

namespace Hearth.Computer.Desktop;

/// <summary>Drives the X desktop inside a bot's sandbox container with <c>docker exec</c> + xdotool.</summary>
internal sealed class ContainerDesktop(string docker, string container) : IDesktopBackend
{
    private static string I(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
    private (double w, double h)? _size;

    private string[] Exec(params string[] args) => ["exec", "-u", "bot", "-e", "DISPLAY=:1", container, .. args];

    private (int code, string output) Run(params string[] args) =>
        Task.Run(() => DesktopBackends.RunAsync(docker, Exec(args))).GetAwaiter().GetResult();

    private void X(params string[] args)
    {
        var (code, output) = Run(["xdotool", .. args]);
        if (code != 0) throw new InvalidOperationException($"Sandbox input failed: {output.Trim()}");
    }

    public string? Problem() => null;
    public void RequestPermissions() { }

    public (double width, double height) ScreenSize()
    {
        if (_size is { } s) return s;
        var (code, o) = Run("xdotool", "getdisplaygeometry");
        if (code != 0) return (1280, 800);
        var p = o.Trim().Split(' ');
        _size = (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
        return _size.Value;
    }

    public async Task<(byte[] bytes, string mime)> CaptureAsync(int maxWidth)
    {
        var psi = new ProcessStartInfo(docker) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Exec("import", "-window", "root", "-resize", $"{maxWidth}x>", "-quality", "70", "jpg:-")) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, err, p.WaitForExitAsync());
        if (p.ExitCode != 0 || ms.Length == 0) throw new InvalidOperationException($"Sandbox screenshot failed: {err.Result.Trim()}");
        return (ms.ToArray(), "image/jpeg");
    }

    public (double x, double y) CursorPosition() => (640, 400); // no fail-safe corner in a sandbox

    public void Move(double x, double y) => X("mousemove", I(x), I(y));

    public void Click(double x, double y, MouseButton button, int count) =>
        X("mousemove", I(x), I(y), "click", "--repeat", Math.Max(1, count).ToString(),
          button switch { MouseButton.Right => "3", MouseButton.Middle => "2", _ => "1" });

    public void Drag(double x1, double y1, double x2, double y2) =>
        X("mousemove", I(x1), I(y1), "mousedown", "1", "mousemove", "--sync", I(x2), I(y2), "mouseup", "1");

    public void Scroll(double x, double y, int lines) =>
        X("mousemove", I(x), I(y), "click", "--repeat", Math.Abs(lines).ToString(), lines > 0 ? "5" : "4");

    public void TypeText(string text) => X("type", "--delay", "8", "--", text);

    public void PressKeys(string combo)
    {
        var (mods, key) = DesktopBackends.ParseCombo(combo);
        var xkey = key switch
        {
            "enter" => "Return", "escape" => "Escape", "backspace" => "BackSpace", "delete" => "Delete", "tab" => "Tab",
            "space" => "space", "left" => "Left", "right" => "Right", "up" => "Up", "down" => "Down",
            "home" => "Home", "end" => "End", "pageup" => "Prior", "pagedown" => "Next",
            _ when key.StartsWith('f') && key.Length > 1 => key.ToUpperInvariant(),
            _ => key,
        };
        // The sandbox is Linux: treat cmd as ctrl so macOS habits still work.
        var xmods = mods.Select(m => m switch { "cmd" or "ctrl" => "ctrl", "alt" => "alt", _ => "shift" }).Distinct();
        X("key", string.Join("+", xmods.Append(xkey)));
    }

    public async Task OpenAsync(string appOrUrl)
    {
        var target = appOrUrl.Trim();
        string[] cmd = Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? ["browser", target]
            : target.ToLowerInvariant() switch
            {
                "chrome" or "chromium" or "browser" or "google chrome" or "safari" or "firefox" or "web browser" => ["browser"],
                "terminal" or "xterm" or "shell" => ["xterm"],
                _ => [target],
            };
        var (code, output) = await DesktopBackends.RunAsync(docker, ["exec", "-d", "-u", "bot", "-e", "DISPLAY=:1", container, .. cmd]);
        if (code != 0) throw new InvalidOperationException($"Couldn't open {target}: {output.Trim()}");
    }
}
