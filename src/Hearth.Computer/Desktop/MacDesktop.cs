using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hearth.Computer.Desktop;

/// <summary>macOS: CoreGraphics events for input, screencapture + sips for screenshots.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDesktop : IDesktopBackend
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGSize { public double W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public CGPoint Origin; public CGSize Size; }

    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(IntPtr e);
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint p, uint button);
    [DllImport(CG)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    // UniChar* is UTF-16; pass ushort[] so .NET doesn't marshal it as an ANSI string.
    [DllImport(CG)] private static extern void CGEventKeyboardSetUnicodeString(IntPtr e, nuint length, ushort[] chars);
    [DllImport(CG)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint count, int w1, int w2, int w3);
    [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr e, ulong flags);
    [DllImport(CG)] private static extern void CGEventSetIntegerValueField(IntPtr e, uint field, long value);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, IntPtr e);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CF)] private static extern void CFRelease(IntPtr p);
    [DllImport(AS)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool AXIsProcessTrusted();

    private const uint HidTap = 0;
    private const uint LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, LeftDragged = 6, OtherDown = 25, OtherUp = 26;
    private const uint ClickStateField = 1;
    private const ulong FlagShift = 0x20000, FlagCtrl = 0x40000, FlagAlt = 0x80000, FlagCmd = 0x100000;

    public string? Problem()
    {
        var missing = new List<string>();
        if (!CGPreflightScreenCaptureAccess()) missing.Add("Screen Recording");
        if (!AXIsProcessTrusted()) missing.Add("Accessibility");
        return missing.Count == 0 ? null
            : $"macOS needs {string.Join(" and ", missing)} permission for the app running Hearth (System Settings → Privacy & " +
              "Security). Click \"Request permissions\", allow it, then restart Hearth.";
    }

    public void RequestPermissions()
    {
        if (!CGPreflightScreenCaptureAccess()) CGRequestScreenCaptureAccess();
        if (!AXIsProcessTrusted())
            System.Diagnostics.Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    }

    public (double width, double height) ScreenSize()
    {
        var r = CGDisplayBounds(CGMainDisplayID());
        return (r.Size.W, r.Size.H);
    }

    public async Task<(byte[] bytes, string mime)> CaptureAsync(int maxWidth)
    {
        var dir = Directory.CreateTempSubdirectory("hearth-shot-");
        try
        {
            var png = Path.Combine(dir.FullName, "s.png");
            var jpg = Path.Combine(dir.FullName, "s.jpg");
            var (code, output) = await DesktopBackends.RunAsync("/usr/sbin/screencapture", "-x", "-m", "-t", "png", png);
            if (code != 0 || !File.Exists(png)) throw new InvalidOperationException($"screencapture failed: {output}");
            await DesktopBackends.RunAsync("/usr/bin/sips", "-Z", maxWidth.ToString(), "-s", "format", "jpeg",
                "-s", "formatOptions", "70", png, "--out", jpg);
            return File.Exists(jpg) ? (await File.ReadAllBytesAsync(jpg), "image/jpeg") : (await File.ReadAllBytesAsync(png), "image/png");
        }
        finally { dir.Delete(true); }
    }

    public (double x, double y) CursorPosition()
    {
        var e = CGEventCreate(IntPtr.Zero);
        try { var p = CGEventGetLocation(e); return (p.X, p.Y); }
        finally { CFRelease(e); }
    }

    private static void Post(IntPtr e) { CGEventPost(HidTap, e); CFRelease(e); }

    private static void Mouse(uint type, double x, double y, uint button, long clickState = 1)
    {
        var e = CGEventCreateMouseEvent(IntPtr.Zero, type, new CGPoint { X = x, Y = y }, button);
        if (type is LeftDown or LeftUp or RightDown or RightUp or OtherDown or OtherUp)
            CGEventSetIntegerValueField(e, ClickStateField, clickState);
        Post(e);
    }

    public void Move(double x, double y) => Mouse(Moved, x, y, 0);

    public void Click(double x, double y, MouseButton button, int count)
    {
        Move(x, y);
        Thread.Sleep(40);
        var (down, up, b) = button switch
        {
            MouseButton.Right => (RightDown, RightUp, 1u),
            MouseButton.Middle => (OtherDown, OtherUp, 2u),
            _ => (LeftDown, LeftUp, 0u),
        };
        for (var i = 1; i <= Math.Max(1, count); i++)
        {
            Mouse(down, x, y, b, i);
            Mouse(up, x, y, b, i);
            Thread.Sleep(30);
        }
    }

    public void Drag(double x1, double y1, double x2, double y2)
    {
        Move(x1, y1);
        Mouse(LeftDown, x1, y1, 0);
        for (var i = 1; i <= 12; i++)
        {
            Mouse(LeftDragged, x1 + (x2 - x1) * i / 12, y1 + (y2 - y1) * i / 12, 0);
            Thread.Sleep(15);
        }
        Mouse(LeftUp, x2, y2, 0);
    }

    public void Scroll(double x, double y, int lines)
    {
        Move(x, y);
        // macOS: negative wheel values scroll down.
        Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1, 1, -lines, 0, 0));
    }

    public void TypeText(string text)
    {
        Thread.Sleep(150); // let focus settle after the previous action
        foreach (var chunk in text.Chunk(16))
        {
            foreach (var down in new[] { true, false })
            {
                var e = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
                CGEventSetFlags(e, 0); // never let a stale modifier turn letters into shortcuts
                CGEventKeyboardSetUnicodeString(e, (nuint)chunk.Length, Array.ConvertAll(chunk, c => (ushort)c));
                Post(e);
            }
            Thread.Sleep(8);
        }
    }

    public void PressKeys(string combo)
    {
        var (mods, key) = DesktopBackends.ParseCombo(combo);
        if (!KeyCodes.TryGetValue(key, out var code)) throw new ArgumentException($"Unknown key '{key}'.");
        // Press real modifier keys (not just flags) so the system's modifier state is released afterwards.
        var modKeys = mods.Select(m => m switch
        {
            "cmd" => (key: (ushort)55, flag: FlagCmd), "ctrl" => ((ushort)59, FlagCtrl),
            "alt" => ((ushort)58, FlagAlt), _ => ((ushort)56, FlagShift),
        }).ToList();
        ulong flags = 0;
        foreach (var (mk, mf) in modKeys)
        {
            flags |= mf;
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, mk, true);
            CGEventSetFlags(e, flags);
            Post(e);
        }
        foreach (var down in new[] { true, false })
        {
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
            CGEventSetFlags(e, flags);
            Post(e);
        }
        foreach (var (mk, mf) in Enumerable.Reverse(modKeys))
        {
            flags &= ~mf;
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, mk, false);
            CGEventSetFlags(e, flags);
            Post(e);
        }
    }

    public async Task OpenAsync(string appOrUrl)
    {
        var isUrl = Uri.TryCreate(appOrUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        var (code, output) = isUrl
            ? await DesktopBackends.RunAsync("/usr/bin/open", appOrUrl)
            : await DesktopBackends.RunAsync("/usr/bin/open", "-a", appOrUrl);
        if (code != 0) throw new InvalidOperationException(output.Trim());
    }

    private static readonly Dictionary<string, ushort> KeyCodes = new()
    {
        ["a"] = 0, ["s"] = 1, ["d"] = 2, ["f"] = 3, ["h"] = 4, ["g"] = 5, ["z"] = 6, ["x"] = 7, ["c"] = 8, ["v"] = 9,
        ["b"] = 11, ["q"] = 12, ["w"] = 13, ["e"] = 14, ["r"] = 15, ["y"] = 16, ["t"] = 17, ["1"] = 18, ["2"] = 19,
        ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["="] = 24, ["9"] = 25, ["7"] = 26, ["-"] = 27, ["8"] = 28,
        ["0"] = 29, ["]"] = 30, ["o"] = 31, ["u"] = 32, ["["] = 33, ["i"] = 34, ["p"] = 35, ["l"] = 37, ["j"] = 38,
        ["'"] = 39, ["k"] = 40, [";"] = 41, ["\\"] = 42, [","] = 43, ["/"] = 44, ["n"] = 45, ["m"] = 46, ["."] = 47,
        ["`"] = 50, ["enter"] = 36, ["tab"] = 48, ["space"] = 49, ["backspace"] = 51, ["escape"] = 53,
        ["delete"] = 117, ["home"] = 115, ["end"] = 119, ["pageup"] = 116, ["pagedown"] = 121,
        ["left"] = 123, ["right"] = 124, ["down"] = 125, ["up"] = 126,
        ["f1"] = 122, ["f2"] = 120, ["f3"] = 99, ["f4"] = 118, ["f5"] = 96, ["f6"] = 97, ["f7"] = 98, ["f8"] = 100,
        ["f9"] = 101, ["f10"] = 109, ["f11"] = 103, ["f12"] = 111,
    };
}
