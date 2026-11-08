using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearth.Core.Models;

namespace Hearth.Core.Services.Google;

public enum GoogleAuthMode { None, OAuth, ServiceAccount }

/// <summary>The connected Google account. Secrets are sealed with <see cref="SecretBox"/>.</summary>
public sealed class GoogleConnection
{
    public GoogleAuthMode Mode { get; set; }
    public string? Email { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecretSealed { get; set; }
    public string? RefreshTokenSealed { get; set; }
    public string? ServiceAccountSealed { get; set; }
    /// <summary>Scopes Google actually granted (OAuth) or that were requested (service account).</summary>
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ConnectedAt { get; set; }
}

/// <summary>
/// Google sign-in for Hearth. Two ways to connect:
/// <list type="bullet">
/// <item>Sign in with Google (OAuth "Desktop app" client you create; loopback redirect + PKCE) — bots can use any
/// spreadsheet your account can open.</item>
/// <item>Service account key (JSON) — works everywhere, including phones; share each sheet with its email.</item>
/// </list>
/// </summary>
public sealed class GoogleAuth(IAppPaths paths, IHttpClientFactory http, SettingsStore settingsStore, PlatformInfo platform)
{
    public const string SheetsScope = "https://www.googleapis.com/auth/spreadsheets";
    public const string DriveListScope = "https://www.googleapis.com/auth/drive.metadata.readonly";
    public const string GmailScope = "https://www.googleapis.com/auth/gmail.modify";
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.events";
    /// <summary>Read any Drive file (Docs, PDFs, Word…) and create new ones (drive.file covers only files Hearth made).</summary>
    public const string DriveReadScope = "https://www.googleapis.com/auth/drive.readonly";
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string ContactsScope = "https://www.googleapis.com/auth/contacts.readonly";

    /// <summary>The scopes to ask for, from the switches on the Connections page.</summary>
    public static List<string> ScopesFor(GoogleSettings g, bool serviceAccount = false)
    {
        var s = new List<string>();
        if (g.SheetsEnabled) s.AddRange([SheetsScope, DriveListScope]);
        if (g.GmailEnabled && !serviceAccount) s.Add(GmailScope); // Gmail needs a real user account
        if (g.CalendarEnabled) s.Add(CalendarScope);
        if (g.DocsEnabled) s.AddRange([DriveReadScope, DriveFileScope]);
        if (g.ContactsEnabled && !serviceAccount) s.Add(ContactsScope); // a service account has no contacts
        if (s.Count == 0) s.AddRange([SheetsScope, DriveListScope]);
        return s;
    }

    /// <summary>True when the connected account granted this scope.</summary>
    public async Task<bool> HasScopeAsync(string scope)
    {
        var c = await GetAsync();
        return c.Mode != GoogleAuthMode.None && (c.Scopes.Count == 0 ? scope is SheetsScope or DriveListScope : c.Scopes.Contains(scope));
    }

    private readonly SecretBox _box = new(paths);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GoogleConnection? _conn;
    private (string token, DateTimeOffset expires)? _access;

    private string FilePath => Path.Combine(paths.DataDirectory, "google.json");
    public event Action? Changed;

    private HttpClient Http => http.CreateClient(Llm.LlmClientFactory.HttpClientName);

    public async Task<GoogleConnection> GetAsync() =>
        _conn ??= await Json.ReadAsync<GoogleConnection>(FilePath) ?? new GoogleConnection();

    public async Task<bool> IsConnectedAsync() => (await GetAsync()).Mode != GoogleAuthMode.None;

    private async Task SaveAsync(GoogleConnection c)
    {
        _conn = c;
        _access = null;
        await Json.WriteAtomicAsync(FilePath, c);
        Changed?.Invoke();
    }

    public async Task DisconnectAsync() => await SaveAsync(new GoogleConnection());

    /// <summary>Endpoints (overridable for tests).</summary>
    public async Task<(string sheets, string drive, string token, string auth)> EndpointsAsync()
    {
        var o = (await settingsStore.GetAsync()).Google.ApiBaseOverride?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(o)
            ? ("https://sheets.googleapis.com/v4", "https://www.googleapis.com/drive/v3",
               "https://oauth2.googleapis.com/token", "https://accounts.google.com/o/oauth2/v2/auth")
            : ($"{o}/v4", $"{o}/drive/v3", $"{o}/token", $"{o}/auth");
    }

    /// <summary>Drive file upload and People (contacts) endpoints (overridable for tests).</summary>
    public async Task<(string upload, string people)> MoreEndpointsAsync()
    {
        var o = (await settingsStore.GetAsync()).Google.ApiBaseOverride?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(o)
            ? ("https://www.googleapis.com/upload/drive/v3", "https://people.googleapis.com/v1")
            : ($"{o}/upload/drive/v3", $"{o}/people/v1");
    }

    public async Task<(string gmail, string calendar)> WorkspaceEndpointsAsync()
    {
        var o = (await settingsStore.GetAsync()).Google.ApiBaseOverride?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(o)
            ? ("https://gmail.googleapis.com/gmail/v1/users/me", "https://www.googleapis.com/calendar/v3")
            : ($"{o}/gmail/v1/users/me", $"{o}/calendar/v3");
    }

    /// <summary>Signs in again with the saved client (e.g. to add Gmail or Calendar access).</summary>
    public async Task<(string url, Task<string> completed)> ReconnectAsync()
    {
        var c = await GetAsync();
        if (c.Mode != GoogleAuthMode.OAuth || c.ClientId is null) throw new InvalidOperationException("Sign in with Google first.");
        return await StartSignInAsync(c.ClientId, _box.Open(c.ClientSecretSealed ?? ""));
    }

    // ------------------------------------------------------------------ access tokens

    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_access is { } a && a.expires > DateTimeOffset.UtcNow.AddMinutes(1)) return a.token;
            var c = await GetAsync();
            var (_, _, tokenUrl, _) = await EndpointsAsync();
            JsonObject res = c.Mode switch
            {
                GoogleAuthMode.OAuth => await PostTokenAsync(tokenUrl, new()
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _box.Open(c.RefreshTokenSealed ?? ""),
                    ["client_id"] = c.ClientId ?? "",
                    ["client_secret"] = _box.Open(c.ClientSecretSealed ?? ""),
                }, ct),
                GoogleAuthMode.ServiceAccount => await PostTokenAsync(tokenUrl, new()
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = ServiceAccountJwt(_box.Open(c.ServiceAccountSealed ?? ""), tokenUrl, c.Scopes),
                }, ct),
                _ => throw new InvalidOperationException("Google isn't connected. Connect it on the Connections page."),
            };
            var token = res["access_token"]?.ToString() ?? throw new InvalidOperationException("Google returned no access token.");
            var expires = DateTimeOffset.UtcNow.AddSeconds(res["expires_in"]?.GetValue<int>() ?? 3600);
            _access = (token, expires);
            return token;
        }
        finally { _lock.Release(); }
    }

    private async Task<JsonObject> PostTokenAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = TryParse(body);
            var msg = err?["error_description"]?.ToString() ?? err?["error"]?.ToString() ?? body;
            if (msg.Contains("invalid_grant") || err?["error"]?.ToString() == "invalid_grant")
                msg = "Google sign-in expired or was revoked — reconnect on the Connections page.";
            throw new InvalidOperationException($"Google sign-in failed: {msg}");
        }
        return TryParse(body) ?? throw new InvalidOperationException("Unexpected token response from Google.");
    }

    private static JsonObject? TryParse(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; } catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ service account

    /// <summary>Checks a service-account key works, then stores it encrypted. Returns the account's email.</summary>
    public async Task<string> ConnectServiceAccountAsync(string keyJson, CancellationToken ct = default)
    {
        var key = TryParse(keyJson) ?? throw new FormatException("That isn't a valid JSON key file.");
        if (key["type"]?.ToString() != "service_account" || key["private_key"] is null || key["client_email"] is null)
            throw new FormatException("That JSON isn't a service-account key (Google Cloud → IAM → Service accounts → Keys → Add key → JSON).");
        var email = key["client_email"]!.ToString();

        var (_, _, tokenUrl, _) = await EndpointsAsync();
        var scopes = ScopesFor((await settingsStore.GetAsync()).Google, serviceAccount: true);
        await PostTokenAsync(tokenUrl, new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = ServiceAccountJwt(keyJson, tokenUrl, scopes),
        }, ct);

        await SaveAsync(new GoogleConnection
        {
            Mode = GoogleAuthMode.ServiceAccount,
            Email = email,
            ServiceAccountSealed = _box.Seal(keyJson),
            Scopes = scopes,
            ConnectedAt = DateTimeOffset.Now,
        });
        return email;
    }

    private static string ServiceAccountJwt(string keyJson, string tokenUrl, IReadOnlyList<string> scopes)
    {
        var key = JsonNode.Parse(keyJson)!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = key["client_email"]!.ToString(),
            ["scope"] = string.Join(' ', scopes.Count > 0 ? scopes : [SheetsScope, DriveListScope]),
            ["aud"] = key["token_uri"]?.ToString() is { Length: > 0 } aud && tokenUrl.StartsWith("https://oauth2.googleapis.com") ? aud : tokenUrl,
            ["iat"] = now,
            ["exp"] = now + 3600,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key["private_key"]!.ToString());
        var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(sig)}";
    }

    // ------------------------------------------------------------------ sign in with Google (OAuth loopback + PKCE)

    /// <summary>
    /// Starts "Sign in with Google". Returns the URL to open; <paramref name="completed"/> finishes when the browser
    /// comes back (or fails). The redirect goes to 127.0.0.1, so the browser must be on the same computer as Hearth.
    /// </summary>
    public async Task<(string url, Task<string> completed)> StartSignInAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("Enter the OAuth client ID and secret first.");
        var (_, drive, tokenUrl, authUrl) = await EndpointsAsync();
        var scopes = ScopesFor((await settingsStore.GetAsync()).Google);

        var (listener, address) = LoopbackHttp.Listen();
        var redirect = address + "/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = $"{authUrl}?client_id={Uri.EscapeDataString(clientId.Trim())}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&response_type=code&scope={Uri.EscapeDataString(string.Join(' ', scopes))}&include_granted_scopes=true" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}&access_type=offline&prompt=consent";

        var completed = Task.Run(async () =>
        {
            try
            {
                var ctxTask = listener.GetContextAsync();
                if (await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5))) != ctxTask)
                    throw new TimeoutException("Sign-in timed out. Try again.");
                var ctx = await ctxTask;
                var q = ctx.Request.QueryString;
                var ok = q["state"] == state && q["code"] is { Length: > 0 };
                await Reply(ctx, ok
                    ? "<h2>✅ Hearth is connected to Google.</h2><p>You can close this tab.</p>"
                    : $"<h2>Sign-in didn't complete</h2><p>{WebUtility.HtmlEncode(q["error"] ?? "unknown error")}</p>");
                if (!ok) throw new InvalidOperationException($"Google sign-in failed: {q["error"] ?? "state mismatch"}");

                var tokens = await PostTokenAsync(tokenUrl, new()
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = q["code"]!,
                    ["redirect_uri"] = redirect,
                    ["client_id"] = clientId.Trim(),
                    ["client_secret"] = clientSecret.Trim(),
                    ["code_verifier"] = verifier,
                }, CancellationToken.None);
                var refresh = tokens["refresh_token"]?.ToString()
                    ?? throw new InvalidOperationException("Google didn't return a refresh token. Remove Hearth at myaccount.google.com/permissions and try again.");

                var email = await WhoAmIAsync(drive, tokens["access_token"]!.ToString());
                await SaveAsync(new GoogleConnection
                {
                    Mode = GoogleAuthMode.OAuth,
                    Email = email,
                    ClientId = clientId.Trim(),
                    ClientSecretSealed = _box.Seal(clientSecret.Trim()),
                    RefreshTokenSealed = _box.Seal(refresh),
                    Scopes = tokens["scope"]?.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? scopes,
                    ConnectedAt = DateTimeOffset.Now,
                });
                return email ?? "your Google account";
            }
            finally
            {
                LoopbackHttp.Close(listener);
            }
        });

        if (platform.CanRunProcesses) TryOpenBrowser(url);
        return (url, completed);
    }

    private async Task<string?> WhoAmIAsync(string driveBase, string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{driveBase}/about?fields=user(emailAddress)");
            req.Headers.Authorization = new("Bearer", accessToken);
            using var res = await Http.SendAsync(req);
            return (await res.Content.ReadFromJsonAsync<JsonObject>())?["user"]?["emailAddress"]?.ToString();
        }
        catch { return null; }
    }

    private static async Task Reply(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes($"<html><body style='font-family:sans-serif;padding:2rem'>{html}</body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux()) Process.Start("xdg-open", url);
        }
        catch { /* the UI shows the link too */ }
    }
}
