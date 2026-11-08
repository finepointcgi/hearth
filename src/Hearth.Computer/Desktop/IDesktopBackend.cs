namespace Hearth.Computer.Desktop;

public enum MouseButton { Left, Right, Middle }

/// <summary>OS-level screen capture and input. Coordinates are in screen points (logical pixels).</summary>
public interface IDesktopBackend
{
    /// <summary>Null when ready; otherwise what the user must do (permissions, missing tools).</summary>
    string? Problem();
    /// <summary>Asks the OS for screen-recording / accessibility permission where that's possible.</summary>
    void RequestPermissions();
    (double width, double height) ScreenSize();
    /// <summary>Full-screen capture, downscaled to at most <paramref name="maxWidth"/> wide, as JPEG or PNG bytes.</summary>
    Task<(byte[] bytes, string mime)> CaptureAsync(int maxWidth);
    (double x, double y) CursorPosition();
    void Move(double x, double y);
    void Click(double x, double y, MouseButton button, int count);
    void Drag(double x1, double y1, double x2, double y2);
    /// <summary>Positive = down.</summary>
    void Scroll(double x, double y, int lines);
    void TypeText(string text);
    /// <summary>A key combination like "cmd+shift+t", "ctrl+c", "enter", "alt+tab".</summary>
    void PressKeys(string combo);
    Task OpenAsync(string appOrUrl);
}

public static class DesktopBackends
{
    public static IDesktopBackend? ForCurrentOs() =>
        OperatingSystem.IsMacOS() ? new MacDesktop()
        : OperatingSystem.IsWindows() ? new WindowsDesktop()
        : OperatingSystem.IsLinux() ? new LinuxDesktop()
        : null;

    /// <summary>Splits "cmd+shift+t" into (modifiers, key).</summary>
    public static (List<string> mods, string key) ParseCombo(string combo)
    {
        var parts = combo.ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) throw new ArgumentException("No key given.");
        if (combo.EndsWith("++")) { parts.RemoveAt(parts.Count - 1); parts.Add("+"); }
        var key = parts[^1];
        var mods = parts.Take(parts.Count - 1).Select(m => m switch
        {
            "command" or "meta" or "super" or "win" or "windows" or "cmd" => "cmd",
            "control" or "ctl" or "ctrl" => "ctrl",
            "option" or "opt" or "alt" => "alt",
            "shift" => "shift",
            _ => throw new ArgumentException($"Unknown modifier '{m}'."),
        }).ToList();
        key = key switch
        {
            "return" => "enter", "esc" => "escape", "del" => "delete", "bksp" => "backspace", "spacebar" => "space",
            "arrowleft" => "left", "arrowright" => "right", "arrowup" => "up", "arrowdown" => "down",
            "pgup" => "pageup", "pgdn" => "pagedown", _ => key,
        };
        return (mods, key);
    }

    internal static async Task<(int code, string output)> RunAsync(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        // ConfigureAwait(false): backends call this synchronously, sometimes from the UI thread.
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(o, e).ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return (p.ExitCode, o.Result + e.Result);
    }

    internal static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"])
            .Any(d => File.Exists(Path.Combine(d, exe)));
}

/// <summary>Reads width/height from PNG or JPEG bytes.</summary>
public static class ImageInfo
{
    public static (int w, int h) Size(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P')
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        }
        throw new FormatException("Unknown image format.");
    }
}
