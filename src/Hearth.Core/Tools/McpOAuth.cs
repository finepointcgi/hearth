using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Hearth.Core.Models;
using Hearth.Core.Services;
using ModelContextProtocol.Authentication;

namespace Hearth.Core.Tools;

/// <summary>
/// MCP authorization for HTTP servers that sign people in through their own identity provider (Keel PLM, for example).
/// The server's 401 points the MCP client at its identity provider; the client then runs authorization code + PKCE with a
/// loopback redirect: Hearth opens the sign-in page in the browser and catches the redirect on 127.0.0.1. Tokens are kept
/// in memory per server, so a sign-in lasts until Hearth restarts (no refresh tokens are written to disk).
/// </summary>
public static class McpOAuth
{
    private static readonly ConcurrentDictionary<string, ITokenCache> Caches = new();

    /// <summary>How long someone has to finish signing in.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>OAuth options for <paramref name="server"/>, or null when it doesn't use OAuth.</summary>
    public static ClientOAuthOptions? OptionsFor(McpServerConfig server, PlatformInfo platform, Action<string>? openBrowser = null)
    {
        if (server.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(server.OAuthClientId)) return null;
        if (!platform.CanRunProcesses)
            throw new PlatformNotSupportedException("Signing in to this server needs the Hearth desktop app: the sign-in page opens in a browser on the same computer.");

        // Reserve a port for the redirect now (the client needs the redirect address up front), listen on it when the sign-in starts.
        var (probe, address) = LoopbackHttp.Listen();
        LoopbackHttp.Close(probe);
        var redirect = new Uri(address + "/callback/");

        return new ClientOAuthOptions
        {
            ClientId = server.OAuthClientId.Trim(),
            RedirectUri = redirect,
            TokenCache = Caches.GetOrAdd(server.Id, _ => new MemoryTokenCache()),
            AuthorizationCallbackHandler = (context, ct) => WaitForRedirectAsync(context.AuthorizationUri, redirect, openBrowser ?? OpenBrowser, ct),
        };
    }

    /// <summary>Forgets a server's sign-in (after removing it, or to sign in as someone else).</summary>
    public static void Forget(string serverId) => Caches.TryRemove(serverId, out _);

    internal static async Task<AuthorizationResult> WaitForRedirectAsync(Uri authorizationUri, Uri redirect, Action<string> openBrowser, CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        listener.Start();
        try
        {
            openBrowser(authorizationUri.ToString());
            var contextTask = listener.GetContextAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SignInTimeout);
            if (await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token)) != contextTask)
                throw new TimeoutException("Sign-in wasn't finished in time. Connect again to retry.");
            var context = await contextTask;
            var q = context.Request.QueryString;
            var ok = q["code"] is { Length: > 0 };
            await ReplyAsync(context, ok
                ? "<h2>Hearth is signed in.</h2><p>You can close this tab.</p>"
                : $"<h2>Sign-in didn't complete</h2><p>{WebUtility.HtmlEncode(q["error_description"] ?? q["error"] ?? "unknown error")}</p>");
            if (!ok) throw new InvalidOperationException("Sign-in failed: " + (q["error_description"] ?? q["error"] ?? "no code returned"));
            return new AuthorizationResult { Code = q["code"], State = q["state"], Iss = q["iss"] };
        }
        finally
        {
            LoopbackHttp.Close(listener);
        }
    }

    private static async Task ReplyAsync(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif;padding:2rem'>{html}</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux()) Process.Start("xdg-open", url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>Keeps one server's tokens in memory for the life of the process.</summary>
internal sealed class MemoryTokenCache : ITokenCache
{
    private TokenContainer? _tokens;
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_tokens);
    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) { _tokens = tokens; return ValueTask.CompletedTask; }
}

/// <summary>Shows header values without their secrets, and keeps the stored secret when a masked value comes back unchanged.</summary>
public static class McpHeaderMask
{
    private const string Dots = "••••••";

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var space = value.IndexOf(' ');
        var (scheme, secret) = space > 0 && space < 12 ? (value[..(space + 1)], value[(space + 1)..]) : ("", value);
        return secret.Length <= 8 ? scheme + Dots : scheme + Dots + secret[^4..];
    }

    /// <summary>Headers as "Name: value" lines with every value masked.</summary>
    public static string Show(IReadOnlyDictionary<string, string> headers) =>
        string.Join('\n', headers.Select(h => $"{h.Key}: {Mask(h.Value)}"));

    /// <summary>Parses edited lines; a value that is still the masked form of the old value keeps the old value.</summary>
    public static Dictionary<string, string> Parse(string text, IReadOnlyDictionary<string, string> previous)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            result[name] = previous.TryGetValue(name, out var old) && value == Mask(old) ? old : value;
        }
        return result;
    }
}
