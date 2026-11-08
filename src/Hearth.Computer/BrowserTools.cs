using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Core.Tools;
using Microsoft.Playwright;

namespace Hearth.Computer;

/// <summary>Browser tools for bots. Reading is free; clicking and typing ask the user unless auto-approved.</summary>
public sealed class ComputerToolProvider(PlaywrightComputer computer, PlatformInfo platform, LoginVault vault) : IToolProvider
{
    public async Task<IReadOnlyList<ITool>> GetToolsAsync(AppSettings settings, CancellationToken ct)
    {
        if (!platform.CanUseComputer || !settings.Computer.Enabled) return [];
        var ask = !settings.Computer.AutoApproveActions;
        var protect = settings.Computer.ProtectSensitiveFields;
        var logins = await vault.ListAsync();
        var loginList = logins.Count == 0 ? "none saved yet"
            : string.Join(", ", logins.Select(l => $"\"{l.Name}\"{(l.Site.Length > 0 ? $" ({l.Site})" : "")}"));
        List<ITool> tools =
        [
            new BrowserTool(computer, "browser_open", "Open web page", "🧭",
                "Open a URL in your browser (the shared bot computer; the user's sign-ins persist there) and return what's on " +
                "the page: title, readable text and numbered interactive elements. Use this for sites that need a login or " +
                "interaction; for simple reading fetch_url is faster.",
                Schema.Object(("url", "string", "Absolute URL", true)), false, OpenAsync),
            new BrowserTool(computer, "browser_snapshot", "Look at page", "👀",
                "Describe the current page again (text + numbered elements), e.g. after waiting for it to load.",
                Schema.Object(), false, (p, a, c) => SnapshotAsync(p)),
            new BrowserTool(computer, "browser_click", "Click", "🖱️",
                "Click an element by its number from the latest page snapshot. Returns the updated page.",
                Schema.Object(("element", "integer", "Element number from the snapshot", true)), ask, ClickAsync),
            new BrowserTool(computer, "browser_type", "Type", "⌨️",
                "Type text into an input by its element number, optionally pressing Enter. To log in, prefer " +
                "browser_fill_login with a saved login so passwords stay out of the chat.",
                Schema.Object(("element", "integer", "Element number", true), ("text", "string", "Text to type", true),
                    ("submit", "boolean", "Press Enter afterwards", false)), ask,
                (p, a, c) => TypeAsync(p, a, c, protect)),
            new BrowserTool(computer, "browser_scroll", "Scroll", "↕️",
                "Scroll the page and return the updated snapshot.",
                Schema.Object(("direction", "string", "down or up", false)), false, ScrollAsync),
            new BrowserTool(computer, "browser_back", "Back", "↩️",
                "Go back to the previous page.", Schema.Object(), false, async (p, a, c) =>
                {
                    await p.GoBackAsync(new() { Timeout = 15000 });
                    return await SnapshotAsync(p);
                }),
            new BrowserTool(computer, "browser_screenshot", "Screenshot", "📸",
                "Take a screenshot of the current page and show it to the user.", Schema.Object(), false, ScreenshotAsync),
        ];
        if (logins.Count > 0)
            tools.Add(new BrowserTool(computer, "browser_fill_login", "Fill saved login", "🔑",
                "Sign in with one of the user's saved logins: fills the username and/or password field (element numbers from " +
                "the snapshot) without revealing the password to you. Saved logins: " + loginList,
                Schema.Object(("login", "string", "Saved login name or site", true),
                    ("username_element", "integer", "Element number of the username/email field (optional)", false),
                    ("password_element", "integer", "Element number of the password field (optional)", false),
                    ("submit", "boolean", "Press Enter after filling", false)), ask,
                (p, a, c) => FillLoginAsync(p, a, vault)));
        return tools;
    }

    private static async Task<ToolResult> FillLoginAsync(IPage page, JsonObject args, LoginVault vault)
    {
        var login = await vault.FindAsync(args.Str("login") ?? "");
        if (login is null) return ToolResult.Error("No saved login with that name. Ask the user to add it on the Computer page.");
        var user = (int?)args.Num("username_element");
        var pass = (int?)args.Num("password_element");
        if (user is null && pass is null) return ToolResult.Error("Give username_element and/or password_element.");
        ILocator? last = null;
        if (user is not null)
        {
            last = page.Locator($"[data-hearth-id='{user}']").First;
            await last.FillAsync(login.Username, new() { Timeout = 10000 });
        }
        if (pass is not null)
        {
            last = page.Locator($"[data-hearth-id='{pass}']").First;
            await last.FillAsync(vault.RevealPassword(login), new() { Timeout = 10000 });
        }
        if (args.Bool("submit") && last is not null)
        {
            await last.PressAsync("Enter");
            await SettleAsync(page);
        }
        var snapshot = await SnapshotAsync(page);
        return snapshot with { Text = $"Filled {(user is not null ? "username" : "")}{(user is not null && pass is not null ? " and " : "")}" +
                                      $"{(pass is not null ? "password" : "")} for \"{login.Name}\".\n\n{snapshot.Text}" };
    }

    // ---------------------------------------------------------------- actions

    private static async Task<ToolResult> OpenAsync(IPage page, JsonObject args, ToolContext ctx)
    {
        if (!Uri.TryCreate(args.Str("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ToolResult.Error("A valid http(s) URL is required.");
        await page.GotoAsync(uri.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await SettleAsync(page);
        return await SnapshotAsync(page);
    }

    private static async Task<ToolResult> ClickAsync(IPage page, JsonObject args, ToolContext ctx)
    {
        var el = await ElementAsync(page, args);
        if (el is null) return ToolResult.Error("No element with that number — take a new browser_snapshot.");
        await el.ClickAsync(new() { Timeout = 10000 });
        await SettleAsync(page);
        return await SnapshotAsync(page);
    }

    private static async Task<ToolResult> TypeAsync(IPage page, JsonObject args, ToolContext ctx, bool protect)
    {
        var el = await ElementAsync(page, args);
        if (el is null) return ToolResult.Error("No element with that number — take a new browser_snapshot.");
        if (protect)
        {
            var type = (await el.GetAttributeAsync("type") ?? "").ToLowerInvariant();
            var auto = (await el.GetAttributeAsync("autocomplete") ?? "").ToLowerInvariant();
            if (type == "password" || auto.Contains("password") || auto.StartsWith("cc-"))
                return ToolResult.Error("That's a password or payment field and the user has \"Protect password and payment " +
                                        "fields\" on. Use browser_fill_login with a saved login, or ask the user.");
        }
        await el.FillAsync(args.Str("text") ?? "", new() { Timeout = 10000 });
        if (args.Bool("submit"))
        {
            await el.PressAsync("Enter");
            await SettleAsync(page);
        }
        return await SnapshotAsync(page);
    }

    private static async Task<ToolResult> ScrollAsync(IPage page, JsonObject args, ToolContext ctx)
    {
        var up = (args.Str("direction") ?? "down").StartsWith("u", StringComparison.OrdinalIgnoreCase);
        await page.Mouse.WheelAsync(0, up ? -700 : 700);
        await page.WaitForTimeoutAsync(400);
        return await SnapshotAsync(page);
    }

    private static async Task<ToolResult> ScreenshotAsync(IPage page, JsonObject args, ToolContext ctx)
    {
        var png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 75 });
        return new ToolResult($"Screenshot of {page.Url} shown to the user.",
            Images: [new ImageAttachment("screenshot.jpg", "image/jpeg", Convert.ToBase64String(png))]);
    }

    private static async Task<ILocator?> ElementAsync(IPage page, JsonObject args)
    {
        var n = (int?)args.Num("element");
        if (n is null) return null;
        var loc = page.Locator($"[data-hearth-id='{n}']");
        return await loc.CountAsync() > 0 ? loc.First : null;
    }

    private static async Task SettleAsync(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }); }
        catch (TimeoutException) { /* busy pages never go idle; carry on */ }
    }

    // ---------------------------------------------------------------- page snapshot

    private const string SnapshotJs = """
        () => {
          document.querySelectorAll('[data-hearth-id]').forEach(e => e.removeAttribute('data-hearth-id'));
          const sel = 'a[href],button,input,textarea,select,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=option],[contenteditable=true]';
          const out = []; let id = 0;
          for (const e of document.querySelectorAll(sel)) {
            const r = e.getBoundingClientRect();
            if (r.width < 2 || r.height < 2 || r.bottom < -50 || r.top > innerHeight * 2.5) continue;
            const st = getComputedStyle(e);
            if (st.visibility === 'hidden' || st.display === 'none' || e.disabled) continue;
            if (e.type === 'hidden') continue;
            id++; e.setAttribute('data-hearth-id', id);
            const label = (e.getAttribute('aria-label') || e.innerText || e.value || e.getAttribute('placeholder') ||
                           e.getAttribute('title') || e.getAttribute('alt') || e.getAttribute('name') || '').trim().replace(/\s+/g, ' ').slice(0, 90);
            out.push({ id, tag: e.tagName.toLowerCase(), type: e.getAttribute('type') || e.getAttribute('role') || '',
                       label, href: e.tagName === 'A' ? e.getAttribute('href') : null,
                       value: (e.tagName === 'INPUT' && e.type !== 'password') || e.tagName === 'TEXTAREA' ? (e.value || '').slice(0, 60) : null });
            if (id >= 160) break;
          }
          return { title: document.title, url: location.href, text: (document.body?.innerText || '').slice(0, 7000),
                   scroll: Math.round(scrollY), height: document.documentElement.scrollHeight, view: innerHeight, elements: out };
        }
        """;

    private static async Task<ToolResult> SnapshotAsync(IPage page)
    {
        var json = await page.EvaluateAsync<JsonElement>(SnapshotJs);
        var sb = new StringBuilder()
            .AppendLine($"Title: {json.GetProperty("title").GetString()}")
            .AppendLine($"URL: {json.GetProperty("url").GetString()}")
            .AppendLine($"Scroll: {json.GetProperty("scroll").GetInt32()} / {json.GetProperty("height").GetInt32()} px")
            .AppendLine()
            .AppendLine("Visible text:")
            .AppendLine(json.GetProperty("text").GetString())
            .AppendLine()
            .AppendLine("Interactive elements (use the number with browser_click / browser_type):");
        foreach (var e in json.GetProperty("elements").EnumerateArray())
        {
            var kind = e.GetProperty("tag").GetString() + (e.GetProperty("type").GetString() is { Length: > 0 } t ? $":{t}" : "");
            sb.Append($"[{e.GetProperty("id").GetInt32()}] {kind} \"{e.GetProperty("label").GetString()}\"");
            if (e.GetProperty("href").GetString() is { Length: > 0 } href) sb.Append($" → {href}");
            if (e.GetProperty("value").ValueKind == JsonValueKind.String && e.GetProperty("value").GetString() is { Length: > 0 } v)
                sb.Append($" (value: {v})");
            sb.AppendLine();
        }
        return new ToolResult(Schema.Truncate(sb.ToString(), 16000));
    }
}

internal sealed class BrowserTool(
    PlaywrightComputer computer,
    string name, string displayName, string icon, string description, JsonObject parameters, bool requiresApproval,
    Func<IPage, JsonObject, ToolContext, Task<ToolResult>> action) : ITool
{
    public string Name => name;
    public string DisplayName => displayName;
    public string Icon => icon;
    public string Description => description;
    public JsonObject Parameters => parameters;
    public bool RequiresApproval => requiresApproval;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var page = await computer.PageForAsync(ctx.Bot?.Id ?? Bot.PlainId, ctx.Bot?.BrowserProfileKey ?? "shared");
        try { return await action(page, args, ctx); }
        catch (PlaywrightException ex) { return ToolResult.Error($"Browser: {ex.Message.Split('\n')[0]}"); }
        catch (TimeoutException ex) { return ToolResult.Error($"Browser timed out: {ex.Message.Split('\n')[0]}"); }
        finally { await computer.NoteActivityAsync(page); }
    }
}
