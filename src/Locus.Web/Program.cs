using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text;
using Locus.UI;
using Locus.Core.Llm;
using Locus.Core.Models;
using Locus.Core.Services;
using Locus.Computer;
using Locus.Web.Components;

// `Locus.Web --install-browser` downloads Chromium (+ its Linux libraries) for the bot computer and exits.
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
// so they're off unless you opt in: "Locus": { "AllowLocalProcesses": true } in appsettings.json.
var allowProcesses = builder.Configuration.GetValue<bool>("Locus:AllowLocalProcesses");
builder.Services.AddLocus(new PlatformInfo("Web server", IsMobile: false, CanRunProcesses: allowProcesses, WebhookBaseUrl: ""));
builder.Services.AddLocusComputer(); // the bots' browser; only usable when AllowLocalProcesses is on
builder.Services.Replace(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<IAppRestarter, WebRestarter>());
builder.Services.AddSingleton<IAccessInfo>(sp => new ServerAccessInfo(
    sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), !string.IsNullOrEmpty(builder.Configuration["Locus:Password"])));

var app = builder.Build();
// Behind "tailscale serve" (or another local HTTPS proxy): honour its X-Forwarded-* headers. Only loopback proxies are trusted.
app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
});

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);

// Optional password (Locus:Password / Locus__Password env var). Strongly recommended whenever Locus is reachable
// from other machines, e.g. in Docker. Webhooks keep their own per-routine tokens; /health stays open.
var password = app.Configuration["Locus:Password"];
if (!string.IsNullOrEmpty(password))
{
    var cookieValue = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("locus-session:" + password)));
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/login") || path == "/health" || path.StartsWith("/hooks/") || path == "/favicon.ico"
            || path is "/manifest.webmanifest" or "/sw.js" or "/apple-touch-icon.png" || path.StartsWith("/icon-")
            || (ctx.Request.Cookies.TryGetValue("locus_auth", out var c)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes(cookieValue))))
        {
            await next();
            return;
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && !path.StartsWith("/_")) ctx.Response.Redirect("/login");
        else ctx.Response.StatusCode = 401;
    });
    app.MapGet("/login", (HttpRequest req) => Results.Content(LoginPage(req.Query.ContainsKey("wrong")), "text/html"));
    app.MapPost("/login", async (HttpContext ctx) =>
    {
        var form = await ctx.Request.ReadFormAsync();
        await Task.Delay(400); // slow down guessing
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["password"].ToString()), Encoding.UTF8.GetBytes(password)))
            return Results.Redirect("/login?wrong=1");
        ctx.Response.Cookies.Append("locus_auth", cookieValue, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromDays(30),
        });
        return Results.Redirect("/");
    }).DisableAntiforgery();
}

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

// Webhook triggers for bot routines: POST /hooks/{routineId}?token=SECRET (or X-Locus-Token header).
app.MapPost("/hooks/{routineId}", async (string routineId, HttpRequest req, RoutineRunner runner) =>
{
    if (req.ContentLength > 1_000_000) return Results.StatusCode(413);
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var token = req.Query["token"].FirstOrDefault() ?? req.Headers["X-Locus-Token"].FirstOrDefault();
    return await runner.HandleWebhookAsync(routineId, token, body, req.ContentType, req.Headers.UserAgent.ToString()) switch
    {
        RoutineRunner.WebhookResult.Accepted => Results.Accepted(),
        RoutineRunner.WebhookResult.Forbidden => Results.StatusCode(403),
        RoutineRunner.WebhookResult.Disabled => Results.Conflict("Routine is turned off."),
        _ => Results.NotFound(),
    };
}).DisableAntiforgery();

// Routines, triggers, Telegram/Discord and the health monitor run on the server even when nobody has the page open.
app.Services.GetRequiredService<RoutineRunner>().EnsureStarted();
app.Services.GetRequiredService<HealthMonitor>().EnsureStarted();
app.Services.GetRequiredService<TaskRunner>().EnsureStarted(); // long-running tasks resume after a restart
app.Services.GetRequiredService<BotInbox>().EnsureStarted();
app.Services.GetRequiredService<HeartbeatRunner>().EnsureStarted();
app.Services.GetRequiredService<SkillPorter>().EnsureStarted();
app.Services.GetRequiredService<MemoryCurator>().EnsureStarted();
app.Services.GetRequiredService<ChatHistoryIndex>().EnsureStarted();
app.Services.GetRequiredService<PushNotifier>().EnsureStarted();
app.Services.GetRequiredService<BackupService>().EnsureStarted();
// Chat replies run in the background; ones a restart interrupts resume on the next start.
var chatRuns = app.Services.GetRequiredService<ChatRuns>();
chatRuns.EnsureStarted();
app.Services.GetRequiredService<FollowUpRunner>().EnsureStarted(); // bots coming back to chats on their own
app.Lifetime.ApplicationStopping.Register(chatRuns.PrepareForShutdown);
_ = app.Services.GetRequiredService<Locus.Core.Services.Channels.ChannelHub>().EnsureStartedAsync().ContinueWith(_ => { });

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(LocusRoutes).Assembly);

app.Run();
return 0;

static string LoginPage(bool wrong) => $$"""
    <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Locus — sign in</title>
    <style>
      body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0a0a0a;color:#ececec;font-family:-apple-system,Segoe UI,Roboto,sans-serif}
      form{background:#141414;border:1px solid #2a2a2a;border-radius:16px;padding:2rem;width:min(90vw,320px);display:flex;flex-direction:column;gap:.8rem}
      h1{margin:0;font-size:1.3rem} input{padding:.65rem .8rem;border-radius:10px;border:1px solid #2a2a2a;background:#0a0a0a;color:#ececec;font-size:1rem}
      button{padding:.65rem;border-radius:10px;border:0;background:#f5f5f5;color:#0a0a0a;font-weight:600;font-size:1rem;cursor:pointer}
      .err{color:#ff6b6b;font-size:.9rem}
    </style></head><body>
    <form method="post" action="/login"><h1>◎ Locus</h1>
      {{(wrong ? "<div class=\"err\">Wrong password.</div>" : "")}}
      <input type="password" name="password" placeholder="Password" autofocus autocomplete="current-password">
      <button type="submit">Sign in</button></form></body></html>
    """;

/// <summary>Where this server listens, for the "Phone access" card.</summary>
/// <summary>Stops the server; the background service (launchd/systemd/Docker) starts it again.</summary>
sealed class WebRestarter(IHostApplicationLifetime lifetime) : IAppRestarter
{
    public bool CanRestart => Environment.GetEnvironmentVariable("LOCUS_SERVICE") == "1" || File.Exists("/.dockerenv");
    public void Restart() => _ = Task.Delay(500).ContinueWith(_ => lifetime.StopApplication());
}

sealed class ServerAccessInfo(Microsoft.AspNetCore.Hosting.Server.IServer server, bool passwordSet) : IAccessInfo
{
    public IReadOnlyList<string> ListenUrls =>
        server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.ToList() ?? [];
    public bool PasswordSet => passwordSet;
}
