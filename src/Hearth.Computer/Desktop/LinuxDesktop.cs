using System.Globalization;
using System.Runtime.Versioning;

namespace Hearth.Computer.Desktop;

/// <summary>Linux (X11): xdotool for input; grim/gnome-screenshot/scrot/import for screenshots; ImageMagick to shrink.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDesktop : IDesktopBackend
{
    private static string I(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

    public string? Problem()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } && Environment.GetEnvironmentVariable("DISPLAY") is null)
            return "Desktop control needs an X11 session (xdotool doesn't work on pure Wayland). Log in with \"GNOME on Xorg\" or similar.";
        if (!DesktopBackends.OnPath("xdotool")) return "Install xdotool: sudo apt install xdotool";
        if (Shooter() is null) return "Install a screenshot tool: sudo apt install scrot (or gnome-screenshot / imagemagick)";
        return null;
    }

    public void RequestPermissions() { }

    private static string? Shooter() =>
        new[] { "scrot", "gnome-screenshot", "import", "grim" }.FirstOrDefault(DesktopBackends.OnPath);

    private static void X(params string[] args) => DesktopBackends.RunAsync("xdotool", args).GetAwaiter().GetResult();

    public (double width, double height) ScreenSize()
    {
        var (_, o) = DesktopBackends.RunAsync("xdotool", "getdisplaygeometry").GetAwaiter().GetResult();
        var p = o.Trim().Split(' ');
        return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
    }

    public async Task<(byte[] bytes, string mime)> CaptureAsync(int maxWidth)
    {
        var dir = Directory.CreateTempSubdirectory("hearth-shot-");
        try
        {
            var png = Path.Combine(dir.FullName, "s.png");
            switch (Shooter())
            {
                case "scrot": await DesktopBackends.RunAsync("scrot", "-o", png); break;
                case "gnome-screenshot": await DesktopBackends.RunAsync("gnome-screenshot", "-f", png); break;
                case "import": await DesktopBackends.RunAsync("import", "-window", "root", png); break;
                default: await DesktopBackends.RunAsync("grim", png); break;
            }
            if (!File.Exists(png)) throw new InvalidOperationException("Screenshot failed.");
            if (DesktopBackends.OnPath("convert"))
            {
                var jpg = Path.Combine(dir.FullName, "s.jpg");
                await DesktopBackends.RunAsync("convert", png, "-resize", $"{maxWidth}x>", "-quality", "70", jpg);
                if (File.Exists(jpg)) return (await File.ReadAllBytesAsync(jpg), "image/jpeg");
            }
            return (await File.ReadAllBytesAsync(png), "image/png");
        }
        finally { dir.Delete(true); }
    }

    public (double x, double y) CursorPosition()
    {
        var (_, o) = DesktopBackends.RunAsync("xdotool", "getmouselocation", "--shell").GetAwaiter().GetResult();
        double Get(string k) => double.Parse(o.Split('\n').First(l => l.StartsWith(k + "="))[(k.Length + 1)..], CultureInfo.InvariantCulture);
        return (Get("X"), Get("Y"));
    }

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
        var xmods = mods.Select(m => m switch { "cmd" => "super", "ctrl" => "ctrl", "alt" => "alt", _ => "shift" });
        X("key", string.Join("+", xmods.Append(xkey)));
    }

    public async Task OpenAsync(string appOrUrl)
    {
        if (Uri.TryCreate(appOrUrl, UriKind.Absolute, out _)) { await DesktopBackends.RunAsync("xdg-open", appOrUrl); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appOrUrl) { UseShellExecute = false });
    }
}
