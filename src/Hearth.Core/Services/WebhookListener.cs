using System.Net;
using System.Text;

namespace Hearth.Core.Services;

/// <summary>
/// Tiny HTTP listener for hosts without a web server (the desktop app), so webhooks can reach routines:
/// POST http://localhost:{port}/hooks/{routineId}?token=SECRET
/// </summary>
public sealed class WebhookListener(RoutineRunner runner) : IDisposable
{
    private HttpListener? _listener;

    public string? Error { get; private set; }
    public int Port { get; private set; }

    public void Start(int port, bool onNetwork)
    {
        Port = port;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(onNetwork ? $"http://+:{port}/hooks/" : $"http://localhost:{port}/hooks/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }
        catch (Exception ex)
        {
            Error = $"Webhook listener couldn't start on port {port}: {ex.Message}";
            _listener = null;
        }
    }

    private async Task LoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var segments = ctx.Request.Url!.AbsolutePath.Trim('/').Split('/');
            if (ctx.Request.HttpMethod != "POST" || segments.Length != 2)
            {
                await Reply(res, 405, "POST /hooks/{routineId}?token=SECRET");
                return;
            }
            if (ctx.Request.ContentLength64 > 1_000_000) { await Reply(res, 413, "Body too large (1 MB max)."); return; }
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var token = ctx.Request.QueryString["token"] ?? ctx.Request.Headers["X-Hearth-Token"];
            var result = await runner.HandleWebhookAsync(segments[1], token, body, ctx.Request.ContentType,
                ctx.Request.Headers["User-Agent"]);
            await Reply(res, result switch
            {
                RoutineRunner.WebhookResult.Accepted => 202,
                RoutineRunner.WebhookResult.Forbidden => 403,
                RoutineRunner.WebhookResult.Disabled => 409,
                _ => 404,
            }, result.ToString());
        }
        catch (Exception ex)
        {
            try { await Reply(res, 500, ex.Message); } catch { }
        }
    }

    private static async Task Reply(HttpListenerResponse res, int status, string text)
    {
        res.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentType = "text/plain";
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
    }
}
