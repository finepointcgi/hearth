using System.Text.Json.Nodes;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Core.Tools;

namespace Locus.Computer.Desktop;

/// <summary>
/// Computer-use tools: see the screen, click, type, press keys, scroll, drag, open apps, type saved logins.
/// Every action returns a fresh screenshot that vision models see (coordinates are screenshot pixels).
/// </summary>
public sealed class DesktopToolProvider(DesktopController host, SandboxDesktops sandboxes, PlatformInfo platform, LoginVault vault, BotStore bots) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        var c = settings.Computer;
        if (!platform.CanRunProcesses) return [];
        var hostOn = c.DesktopControl && host.Supported;
        var anySandbox = (await bots.ListAsync()).Any(b => b.Abilities is { ComputerUse: true, Desktop: DesktopTarget.Sandbox });
        if (!hostOn && !anySandbox) return [];
        var ask = !c.AutoApproveDesktop;
        var resolver = new DesktopResolver(host, sandboxes, hostOn);
        var logins = await vault.ListAsync();
        const string coords = "x and y are pixel coordinates in the most recent screenshot.";

        List<ITool> tools =
        [
            new DesktopTool(resolver, "desktop_screenshot", "Look at screen", "🖥️",
                "Take a screenshot of the whole screen (the user's computer, or your own sandbox desktop if you have one). Use it first, and whenever you're unsure what's showing. " +
                "You need a vision-capable model to see it.",
                Schema.Object(), false, (d, a, ct) => Task.FromResult("Screenshot taken.")),
            new DesktopTool(resolver, "desktop_click", "Click", "🖱️",
                $"Click on the screen. {coords} Aim for the center of the target.",
                Schema.Object(("x", "number", "X in screenshot pixels", true), ("y", "number", "Y in screenshot pixels", true),
                    ("button", "string", "left (default), right or middle", false),
                    ("double", "boolean", "Double-click", false)), ask,
                async (d, a, ct) =>
                {
                    var (x, y) = await d.ToScreenAsync(a.Num("x") ?? 0, a.Num("y") ?? 0);
                    var button = (a.Str("button") ?? "left").ToLowerInvariant() switch
                    {
                        "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left,
                    };
                    var count = a.Bool("double") ? 2 : 1;
                    await d.ActAsync($"{(count == 2 ? "double-" : "")}{button.ToString().ToLowerInvariant()} click at {x:0},{y:0}",
                        b => b.Click(x, y, button, count));
                    return $"Clicked at ({a.Num("x")}, {a.Num("y")}).";
                }),
            new DesktopTool(resolver, "desktop_type", "Type", "⌨️",
                "Type text at the current keyboard focus (click a field first). Use desktop_key for Enter, Tab or shortcuts.",
                Schema.Object(("text", "string", "Text to type", true)), ask,
                async (d, a, ct) =>
                {
                    var text = a.Str("text") ?? "";
                    await d.ActAsync($"typed {text.Length} characters", b => b.TypeText(text));
                    return $"Typed {text.Length} characters.";
                }),
            new DesktopTool(resolver, "desktop_key", "Press keys", "⌘",
                "Press a key or shortcut, e.g. enter, tab, escape, cmd+c, ctrl+v, cmd+space, alt+tab, cmd+shift+t, down. " +
                $"This computer runs {(OperatingSystem.IsMacOS() ? "macOS (use cmd for shortcuts)" : OperatingSystem.IsWindows() ? "Windows (use ctrl; win is the Windows key)" : "Linux (use ctrl)")}. If you work in your own sandbox desktop, it's Linux: use ctrl.",
                Schema.Object(("keys", "string", "Key combination", true)), ask,
                async (d, a, ct) =>
                {
                    var keys = a.Str("keys") ?? "";
                    await d.ActAsync($"pressed {keys}", b => b.PressKeys(keys));
                    return $"Pressed {keys}.";
                }),
            new DesktopTool(resolver, "desktop_scroll", "Scroll", "↕️",
                $"Scroll with the mouse over a point. {coords}",
                Schema.Object(("x", "number", "X in screenshot pixels", true), ("y", "number", "Y in screenshot pixels", true),
                    ("direction", "string", "down (default) or up", false), ("amount", "integer", "Lines, default 5", false)), ask,
                async (d, a, ct) =>
                {
                    var (x, y) = await d.ToScreenAsync(a.Num("x") ?? 0, a.Num("y") ?? 0);
                    var lines = (int)Math.Clamp(a.Num("amount") ?? 5, 1, 50);
                    if ((a.Str("direction") ?? "down").StartsWith("u", StringComparison.OrdinalIgnoreCase)) lines = -lines;
                    await d.ActAsync($"scrolled {lines} at {x:0},{y:0}", b => b.Scroll(x, y, lines));
                    return $"Scrolled {(lines > 0 ? "down" : "up")} {Math.Abs(lines)} lines.";
                }),
            new DesktopTool(resolver, "desktop_drag", "Drag", "✋",
                $"Drag with the left mouse button from one point to another. {coords}",
                Schema.Object(("from_x", "number", "Start X", true), ("from_y", "number", "Start Y", true),
                    ("to_x", "number", "End X", true), ("to_y", "number", "End Y", true)), ask,
                async (d, a, ct) =>
                {
                    var (x1, y1) = await d.ToScreenAsync(a.Num("from_x") ?? 0, a.Num("from_y") ?? 0);
                    var (x2, y2) = await d.ToScreenAsync(a.Num("to_x") ?? 0, a.Num("to_y") ?? 0);
                    await d.ActAsync($"dragged {x1:0},{y1:0} → {x2:0},{y2:0}", b => b.Drag(x1, y1, x2, y2));
                    return "Dragged.";
                }),
            new DesktopTool(resolver, "desktop_open", "Open app", "🚀",
                "Open an application by name (e.g. \"Safari\", \"Notes\", \"Visual Studio Code\") or a URL in the default browser. " +
                "In a sandbox desktop the apps are \"browser\" (Chromium) and \"terminal\".",
                Schema.Object(("target", "string", "App name or URL", true)), ask,
                async (d, a, ct) =>
                {
                    var target = a.Str("target") ?? "";
                    await d.ActAsync($"opened {target}", b => b.OpenAsync(target).GetAwaiter().GetResult());
                    await Task.Delay(1200, ct);
                    return $"Opened {target}.";
                }),
        ];

        if (logins.Count > 0)
            tools.Add(new DesktopTool(resolver, "desktop_type_login", "Type saved login", "🔑",
                "Type the username or password of one of the user's saved logins at the current focus, without revealing it " +
                "to you. Click the field first. Saved logins: " +
                string.Join(", ", logins.Select(l => $"\"{l.Name}\"{(l.Site.Length > 0 ? $" ({l.Site})" : "")}")),
                Schema.Object(("login", "string", "Saved login name or site", true),
                    ("field", "string", "username or password", true),
                    ("then", "string", "Key to press afterwards: tab, enter or none (default)", false)), ask,
                async (d, a, ct) =>
                {
                    var login = await vault.FindAsync(a.Str("login") ?? "")
                        ?? throw new InvalidOperationException("No saved login with that name.");
                    var isPassword = (a.Str("field") ?? "").StartsWith("p", StringComparison.OrdinalIgnoreCase);
                    var value = isPassword ? vault.RevealPassword(login) : login.Username;
                    var then = (a.Str("then") ?? "none").ToLowerInvariant();
                    await d.ActAsync($"typed the {(isPassword ? "password" : "username")} for {login.Name}", b =>
                    {
                        b.TypeText(value);
                        if (then is "tab" or "enter") b.PressKeys(then);
                    });
                    return $"Typed the {(isPassword ? "password" : "username")} for \"{login.Name}\"{(then is "tab" or "enter" ? $" and pressed {then}" : "")}.";
                }, SandboxAsks: true));
        return tools;
    }
}

/// <summary>Picks the screen a call drives: the bot's sandbox, or this computer.</summary>
internal sealed class DesktopResolver(DesktopController host, SandboxDesktops sandboxes, bool hostOn)
{
    public static bool InSandbox(ToolContext ctx) => ctx.Bot?.Abilities.Desktop == DesktopTarget.Sandbox;

    public async Task<DesktopController> ForAsync(ToolContext ctx, CancellationToken ct)
    {
        if (InSandbox(ctx)) return await sandboxes.ControllerAsync(ctx.Bot!.Id, ct);
        if (!hostOn) throw new InvalidOperationException("Control of this computer is switched off (Computer page). Only bots with their own sandbox can use a desktop right now.");
        if (host.Problem is { } problem) throw new InvalidOperationException(problem);
        return host;
    }
}

internal sealed class DesktopTool(
    DesktopResolver resolver, string name, string displayName, string icon, string description,
    JsonObject parameters, bool requiresApproval, Func<DesktopController, JsonObject, CancellationToken, Task<string>> action,
    bool SandboxAsks = false) : ITool, IContextualApproval
{
    public string Name => name;
    public string DisplayName => displayName;
    public string Icon => icon;
    public string Description => description;
    public JsonObject Parameters => parameters;
    public bool RequiresApproval => requiresApproval;

    /// <summary>A bot's own sandbox can't touch your files or screen, so its actions don't need your OK.</summary>
    public bool RequiresApprovalFor(ToolContext ctx) => DesktopResolver.InSandbox(ctx) ? requiresApproval && SandboxAsks : requiresApproval;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var d = await resolver.ForAsync(ctx, ct);
            var text = await action(d, args, ct);
            var shot = await d.ScreenshotAsync();
            return new ToolResult($"{text} Screen now attached as {shot.FileName}.", ModelImage: shot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}
