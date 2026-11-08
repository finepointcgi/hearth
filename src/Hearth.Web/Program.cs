using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text;
using Hearth.UI;
using Hearth.Core.Llm;
using Hearth.Core.Models;
using Hearth.Core.Services;
using Hearth.Computer;
using Hearth.Web.Components;

// `Hearth.Web --install-browser` downloads Chromium (+ its Linux libraries) for the bot computer and exits.
// Used when building the Docker image.
if (args.Contains("--install-browser"))
    return Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]);

// A restore staged on the Backup page is applied before anything opens the data folder.
BackupService.ApplyPendingRestore(new DefaultAppPaths().DataDirectory);

var builder = WebApplication.CreateBuilder(args);
// HttpClient's request logs include full URLs — Telegram puts the bot token in the URL — so keep them out of logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// Allow larger uploads (documents, images) over the SignalR circuit.
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);
// Python and stdio MCP servers would let anyone who can open the page run code on this server,
// so they're off unless you opt in: "Hearth": { "AllowLocalProcesses": true } in appsettings.json.
// Settings keys are "Hearth:…"; the old "Locus:…" keys (from before the rename) still work.
string? Setting(IConfiguration c, string key) => c["Hearth:" + key] ?? c[LegacyNames.OldName + ":" + key];
var allowProcesses = bool.TryParse(Setting(builder.Configuration, "AllowLocalProcesses"), out var allow) && allow;
var passwordSet = !string.IsNullOrEmpty(Setting(builder.Configuration, "Password"));
// Everything Hearth is, for one account (each account gets its own set; see AccountHosts).
// computerAccess: for someone other than the owner, whether the owner lets them use this computer (asked each time).
void ConfigureAccount(IServiceCollection s, Func<bool>? computerAccess)
{
    s.AddHearth(new PlatformInfo("Web server", IsMobile: false, CanRunProcesses: allowProcesses, WebhookBaseUrl: "") { ComputerAccess = computerAccess });
    s.AddHearthComputer(); // the bots' browser; only usable when AllowLocalProcesses is on
    s.Replace(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IAppRestarter, WebRestarter>());
    s.AddSingleton<IAccessInfo>(sp => new ServerAccessInfo(sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), passwordSet));
}
ConfigureAccount(builder.Services, null);
// The app's Hearth services forward to the signed-in account's own set (the owner's when accounts are off).
Hearth.Web.AccountHosts.ForwardToAccounts(builder.Services);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(new AccountStore(new DefaultAppPaths().DataDirectory));
builder.Services.AddSingleton(sp => new Hearth.Web.AccountHosts(sp, sp.GetRequiredService<AccountStore>(), ConfigureAccount));
builder.Services.AddScoped<Hearth.Web.CurrentAccount>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<Hearth.Web.CurrentAccount>());

var app = builder.Build();
// Behind "tailscale serve" (or another local HTTPS proxy): honour its X-Forwarded-* headers. Only loopback proxies are trusted.
app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
});

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);

// Sign-in. With separate accounts on (Settings → Accounts), each person signs in with their name and password and
// sees only their own Hearth. Otherwise an optional single password (Hearth:Password / Hearth__Password env var),
// strongly recommended whenever Hearth is reachable from other machines. Webhooks keep their own per-routine tokens,
// the API its key, Twilio its signatures; /health stays open.
var password = Setting(app.Configuration, "Password");
var accounts = app.Services.GetRequiredService<AccountStore>();
var cookieValue = string.IsNullOrEmpty(password) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hearth-session:" + password)));
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var open = path.StartsWith("/login") || path == "/health" || path.StartsWith("/hooks/") || path.StartsWith("/v1/") || path.StartsWith("/phone/")
        || path == "/favicon.ico" || path is "/manifest.webmanifest" or "/sw.js" or "/apple-touch-icon.png" || path.StartsWith("/icon-");
    var allowed = open;
    if (accounts.Enabled)
    {
        if (accounts.FromCookie(ctx.Request.Cookies["hearth_user"]) is { } who)
        {
            ctx.User = Hearth.Web.CurrentAccount.Principal(who);
            allowed = true;
        }
    }
    else if (password is { Length: > 0 })
        allowed |= ctx.Request.Cookies.TryGetValue("hearth_auth", out var c)
                   && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes(cookieValue));
    else allowed = true;
    if (allowed) { await next(); return; }
    if (HttpMethods.IsGet(ctx.Request.Method) && !path.StartsWith("/_")) ctx.Response.Redirect("/login");
    else ctx.Response.StatusCode = 401;
});
app.MapGet("/login", (HttpRequest req) => accounts.Enabled || password is { Length: > 0 }
    ? Results.Content(LoginPage(req.Query.ContainsKey("wrong"), accounts.Enabled), "text/html")
    : Results.Redirect("/"));
app.MapPost("/login", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    await Task.Delay(400); // slow down guessing
    var cookie = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromDays(30) };
    if (accounts.Enabled)
    {
        if (accounts.Verify(form["name"].ToString(), form["password"].ToString()) is not { } who) return Results.Redirect("/login?wrong=1");
        ctx.Response.Cookies.Append("hearth_user", accounts.CookieFor(who), cookie);
        return Results.Redirect("/");
    }
    if (password is not { Length: > 0 }) return Results.Redirect("/");
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["password"].ToString()), Encoding.UTF8.GetBytes(password)))
        return Results.Redirect("/login?wrong=1");
    ctx.Response.Cookies.Append("hearth_auth", cookieValue, cookie);
    return Results.Redirect("/");
}).DisableAntiforgery();
app.MapGet("/signout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete("hearth_user");
    ctx.Response.Cookies.Delete("hearth_auth");
    return Results.Redirect("/login");
});

app.UseAntiforgery();
app.MapStaticAssets();

// Health check for Docker, uptime monitors and the service scripts.
app.MapGet("/health", async (SettingsStore settings, ILlmClientFactory factory, HealthMonitor health) =>
{
    var s = await settings.GetAsync();
    return Results.Ok(new
    {
        status = "ok",
        modelServer = s.ActiveProvider.Name,
        modelServerUp = health.ModelServerUp,
        lastCheck = health.LastCheck,
    });
});

// OpenAI-compatible API: other apps, scripts and the editor bridge talk to Hearth and its bots.
// Off until turned on in Connections → More, and every call needs the API key.
app.MapGet("/v1/models", async (HttpRequest req, SettingsStore settings, OpenAiApi api) =>
{
    if (!(await settings.GetAsync()).Api.Accepts(req.Headers.Authorization)) return ApiError(401, "Missing or wrong API key.");
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return Results.Json(new
    {
        @object = "list",
        data = (await api.ModelsAsync()).Select(m => new { id = m.id, @object = "model", created, owned_by = "hearth", description = m.description }),
    });
});

app.MapPost("/v1/chat/completions", async (HttpContext ctx, SettingsStore settings, OpenAiApi api) =>
{
    if (!(await settings.GetAsync()).Api.Accepts(ctx.Request.Headers.Authorization)) { await ApiError(401, "Missing or wrong API key.").ExecuteAsync(ctx); return; }
    System.Text.Json.Nodes.JsonObject body;
    try { body = (await System.Text.Json.Nodes.JsonNode.ParseAsync(ctx.Request.Body) as System.Text.Json.Nodes.JsonObject) ?? throw new FormatException(); }
    catch { await ApiError(400, "Send a JSON body.").ExecuteAsync(ctx); return; }
    var model = body["model"]?.ToString() ?? OpenAiApi.PlainModel;
    var messages = (body["messages"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
        .Select(m => new ApiMessage(m["role"]?.ToString() ?? "user", ContentText(m["content"]))).ToList();
    var stream = body["stream"]?.GetValue<bool>() == true;
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    try
    {
        if (!stream)
        {
            var text = await api.CompleteAsync(model, messages, null, ctx.RequestAborted);
            await Results.Json(new
            {
                id, @object = "chat.completion", created, model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
            }).ExecuteAsync(ctx);
            return;
        }
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        async Task Send(object chunk)
        {
            await ctx.Response.WriteAsync("data: " + System.Text.Json.JsonSerializer.Serialize(chunk) + "\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        await Send(new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } } });
        await api.CompleteAsync(model, messages, delta =>
            Send(new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { role = (string?)null, content = delta }, finish_reason = (string?)null } } }),
            ctx.RequestAborted);
        await Send(new { id, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta = new { role = (string?)null, content = (string?)null }, finish_reason = "stop" } } });
        await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
    }
    catch (KeyNotFoundException ex) { if (!ctx.Response.HasStarted) await ApiError(404, ex.Message).ExecuteAsync(ctx); }
    catch (ArgumentException ex) { if (!ctx.Response.HasStarted) await ApiError(400, ex.Message).ExecuteAsync(ctx); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { if (!ctx.Response.HasStarted) await ApiError(500, ex.Message).ExecuteAsync(ctx); }
}).DisableAntiforgery();

// Webhook triggers for bot routines: POST /hooks/{routineId}?token=SECRET (or X-Hearth-Token header).
app.MapPost("/hooks/{routineId}", async (string routineId, HttpRequest req, RoutineRunner runner) =>
{
    if (req.ContentLength > 1_000_000) return Results.StatusCode(413);
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var token = req.Query["token"].FirstOrDefault() ?? req.Headers["X-Hearth-Token"].FirstOrDefault();
    return await runner.HandleWebhookAsync(routineId, token, body, req.ContentType, req.Headers.UserAgent.ToString()) switch
    {
        RoutineRunner.WebhookResult.Accepted => Results.Accepted(),
        RoutineRunner.WebhookResult.Forbidden => Results.StatusCode(403),
        RoutineRunner.WebhookResult.Disabled => Results.Conflict("Routine is turned off."),
        _ => Results.NotFound(),
    };
}).DisableAntiforgery();

// Phone calls (Twilio ConversationRelay). Twilio signs every request with the account's auth token; unsigned or
// wrongly signed requests get 403.
app.UseWebSockets();
static async Task<Dictionary<string, string>> TwilioForm(HttpRequest req) =>
    req.HasFormContentType ? (await req.ReadFormAsync()).ToDictionary(f => f.Key, f => f.Value.ToString()) : [];
app.MapPost(Hearth.Core.Services.Phone.PhoneService.IncomingPath, async (HttpRequest req, Hearth.Core.Services.Phone.PhoneService phone) =>
{
    var form = await TwilioForm(req);
    if (!await phone.IsFromTwilioAsync(req.Path, req.QueryString.Value ?? "", form, req.Headers["X-Twilio-Signature"])) return Results.StatusCode(403);
    return Results.Content(await phone.IncomingAsync(form), "text/xml");
}).DisableAntiforgery();
app.MapPost(Hearth.Core.Services.Phone.PhoneService.StatusPath, async (HttpRequest req, Hearth.Core.Services.Phone.PhoneService phone) =>
{
    var form = await TwilioForm(req);
    if (!await phone.IsFromTwilioAsync(req.Path, req.QueryString.Value ?? "", form, req.Headers["X-Twilio-Signature"])) return Results.StatusCode(403);
    await phone.StatusAsync(form);
    return Results.NoContent();
}).DisableAntiforgery();
app.MapPost(Hearth.Core.Services.Phone.PhoneService.EndedPath, async (HttpRequest req, Hearth.Core.Services.Phone.PhoneService phone) =>
{
    var form = await TwilioForm(req);
    if (!await phone.IsFromTwilioAsync(req.Path, req.QueryString.Value ?? "", form, req.Headers["X-Twilio-Signature"])) return Results.StatusCode(403);
    return Results.Content(Hearth.Core.Services.Phone.Twilio.Empty, "text/xml"); // relay finished: hang up
}).DisableAntiforgery();
app.Map(Hearth.Core.Services.Phone.PhoneService.RelayPath, async (HttpContext ctx, Hearth.Core.Services.Phone.PhoneService phone) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (!await phone.IsRelayFromTwilioAsync(ctx.Request.QueryString.Value ?? "", ctx.Request.Headers["X-Twilio-Signature"]))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[16 * 1024];
    async Task<string?> Receive(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 1_000_000) return null;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
    Task Send(string text, CancellationToken ct) => socket.State == System.Net.WebSockets.WebSocketState.Open
        ? socket.SendAsync(Encoding.UTF8.GetBytes(text), System.Net.WebSockets.WebSocketMessageType.Text, true, ct)
        : Task.CompletedTask;
    try { await phone.RunRelayAsync(Receive, Send, ctx.RequestAborted); }
    catch (System.Net.WebSockets.WebSocketException) { }
    if (socket.State == System.Net.WebSockets.WebSocketState.Open)
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
});

// Routines, triggers, chat apps and the health monitor run on the server even when nobody has the page open —
// for every account.
var hosts = app.Services.GetRequiredService<Hearth.Web.AccountHosts>();
hosts.StartAll();
app.Lifetime.ApplicationStopping.Register(hosts.PrepareForShutdown);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(HearthRoutes).Assembly);

app.Run();
return 0;

static IResult ApiError(int status, string message) =>
    Results.Json(new { error = new { message, type = status == 401 ? "authentication_error" : "invalid_request_error" } }, statusCode: status);

// OpenAI messages carry text as a string or as parts ([{type:"text", text}], images are skipped).
static string ContentText(System.Text.Json.Nodes.JsonNode? content) => content switch
{
    null => "",
    System.Text.Json.Nodes.JsonArray parts => string.Join("\n", parts.OfType<System.Text.Json.Nodes.JsonObject>()
        .Where(p => p["type"]?.ToString() is "text" or "input_text").Select(p => p["text"]?.ToString() ?? "")),
    _ => content.ToString(),
};

static string LoginPage(bool wrong, bool withName) => $$"""
    <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Hearth — sign in</title>
    <style>
      body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0a0a0a;color:#ececec;font-family:-apple-system,Segoe UI,Roboto,sans-serif}
      form{background:#141414;border:1px solid #2a2a2a;border-radius:16px;padding:2rem;width:min(90vw,320px);display:flex;flex-direction:column;gap:.8rem}
      h1{margin:0;font-size:1.3rem} input{padding:.65rem .8rem;border-radius:10px;border:1px solid #2a2a2a;background:#0a0a0a;color:#ececec;font-size:1rem}
      button{padding:.65rem;border-radius:10px;border:0;background:#f5f5f5;color:#0a0a0a;font-weight:600;font-size:1rem;cursor:pointer}
      .err{color:#ff6b6b;font-size:.9rem}
    </style></head><body>
    <form method="post" action="/login"><h1>◎ Hearth</h1>
      {{(wrong ? $"<div class=\"err\">Wrong {(withName ? "name or password" : "password")}.</div>" : "")}}
      {{(withName ? "<input name=\"name\" placeholder=\"Your name\" autofocus autocomplete=\"username\" autocapitalize=\"none\">" : "")}}
      <input type="password" name="password" placeholder="Password" {{(withName ? "" : "autofocus")}} autocomplete="current-password">
      <button type="submit">Sign in</button></form></body></html>
    """;

/// <summary>Where this server listens, for the "Phone access" card.</summary>
/// <summary>Stops the server; the background service (launchd/systemd/Docker) starts it again.</summary>
sealed class WebRestarter(IHostApplicationLifetime lifetime) : IAppRestarter
{
    public bool CanRestart => LegacyNames.Env("SERVICE") == "1" || File.Exists("/.dockerenv");
    public void Restart() => _ = Task.Delay(500).ContinueWith(_ => lifetime.StopApplication());
}

sealed class ServerAccessInfo(Microsoft.AspNetCore.Hosting.Server.IServer server, bool passwordSet) : IAccessInfo
{
    public IReadOnlyList<string> ListenUrls =>
        server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.ToList() ?? [];
    public bool PasswordSet => passwordSet;
}
