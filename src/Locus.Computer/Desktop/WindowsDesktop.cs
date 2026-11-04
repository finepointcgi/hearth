using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Locus.Computer.Desktop;

/// <summary>Windows: user32 input and GDI+ screen capture.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktop : IDesktopBackend
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public UIntPtr Extra;
    }

    private const uint LeftDown = 0x02, LeftUp = 0x04, RightDown = 0x08, RightUp = 0x10, MiddleDown = 0x20, MiddleUp = 0x40, Wheel = 0x800;
    private const uint KeyUp = 0x02, Unicode = 0x04;

    public WindowsDesktop() => SetProcessDPIAware();

    public string? Problem() => null;
    public void RequestPermissions() { }

    public (double width, double height) ScreenSize() => (GetSystemMetrics(0), GetSystemMetrics(1));

    public Task<(byte[] bytes, string mime)> CaptureAsync(int maxWidth)
    {
        var (w, h) = ((int)ScreenSize().width, (int)ScreenSize().height);
        using var full = new Bitmap(w, h);
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
        var scale = Math.Min(1.0, maxWidth / (double)w);
        using var small = new Bitmap(full, new Size((int)(w * scale), (int)(h * scale)));
        using var ms = new MemoryStream();
        small.Save(ms, ImageFormat.Jpeg);
        return Task.FromResult((ms.ToArray(), "image/jpeg"));
    }

    public (double x, double y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public void Move(double x, double y) => SetCursorPos((int)x, (int)y);

    public void Click(double x, double y, MouseButton button, int count)
    {
        Move(x, y);
        var (down, up) = button switch { MouseButton.Right => (RightDown, RightUp), MouseButton.Middle => (MiddleDown, MiddleUp), _ => (LeftDown, LeftUp) };
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
        }
    }

    public void Drag(double x1, double y1, double x2, double y2)
    {
        Move(x1, y1);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        for (var i = 1; i <= 12; i++) { Move(x1 + (x2 - x1) * i / 12, y1 + (y2 - y1) * i / 12); Thread.Sleep(15); }
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public void Scroll(double x, double y, int lines)
    {
        Move(x, y);
        mouse_event(Wheel, 0, 0, -lines * 120, UIntPtr.Zero);
    }

    public void TypeText(string text)
    {
        var inputs = text.SelectMany(ch => new[]
        {
            new INPUT { Type = 1, Scan = ch, Flags = Unicode },
            new INPUT { Type = 1, Scan = ch, Flags = Unicode | KeyUp },
        }).ToArray();
        foreach (var chunk in inputs.Chunk(64))
        {
            SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5);
        }
    }

    public void PressKeys(string combo)
    {
        var (mods, key) = DesktopBackends.ParseCombo(combo);
        var vk = VirtualKey(key);
        var modVks = mods.Select(m => m switch { "cmd" => (byte)0x5B, "ctrl" => (byte)0x11, "alt" => (byte)0x12, _ => (byte)0x10 }).ToList();
        foreach (var m in modVks) keybd_event(m, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
        foreach (var m in Enumerable.Reverse(modVks)) keybd_event(m, 0, KeyUp, UIntPtr.Zero);
    }

    private static byte VirtualKey(string key) => key switch
    {
        "enter" => 0x0D, "tab" => 0x09, "escape" => 0x1B, "backspace" => 0x08, "delete" => 0x2E, "space" => 0x20,
        "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28, "home" => 0x24, "end" => 0x23,
        "pageup" => 0x21, "pagedown" => 0x22,
        _ when key.Length > 1 && key[0] == 'f' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 12 => (byte)(0x6F + n),
        _ when key.Length == 1 && char.IsLetterOrDigit(key[0]) => (byte)char.ToUpperInvariant(key[0]),
        _ => throw new ArgumentException($"Unknown key '{key}'."),
    };

    public Task OpenAsync(string appOrUrl)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(appOrUrl) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
