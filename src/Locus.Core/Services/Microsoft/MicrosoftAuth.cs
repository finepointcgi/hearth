using System.Text.Json.Nodes;
using Locus.Core.Llm;

namespace Locus.Core.Services.Microsoft;

/// <summary>The connected Microsoft account (microsoft.json; the refresh token is sealed with <see cref="SecretBox"/>).</summary>
public sealed class MicrosoftConnection
{
    /// <summary>Application (client) ID of the user's own app registration in Microsoft Entra.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>"common" (work and personal accounts), "consumers", "organizations" or a tenant id.</summary>
    public string Tenant { get; set; } = "common";
    public string? Account { get; set; }
    public string? RefreshTokenSealed { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public bool Connected => !string.IsNullOrEmpty(RefreshTokenSealed);
}

/// <summary>A sign-in waiting for the user to enter <see cref="UserCode"/> at <see cref="VerificationUri"/>.</summary>
public sealed record MicrosoftSignIn(string UserCode, string VerificationUri, string Message, Task<string> Completed);

/// <summary>
/// Signs in to Microsoft 365 with the device code flow (the user enters a short code at microsoft.com/devicelogin, so
/// nothing has to be reachable from the internet) and keeps an access token fresh for Microsoft Graph.
/// </summary>
public sealed class MicrosoftAuth(IAppPaths paths, IHttpClientFactory http)
{
    /// <summary>What Locus asks for: mail, calendar, files (OneDrive, Word, Excel), To Do and the profile.</summary>
    public const string Scopes = "offline_access User.Read Mail.ReadWrite Mail.Send Calendars.ReadWrite Files.ReadWrite Tasks.ReadWrite";

    private readonly SecretBox _box = new(paths);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MicrosoftConnection? _cache;
    private (string token, DateTimeOffset expires)? _access;
    private string FilePath => Path.Combine(paths.DataDirectory, "microsoft.json");
    private HttpClient Client => http.CreateClient(LlmClientFactory.HttpClientName);

    /// <summary>For tests: the sign-in service and Graph addresses.</summary>
    public string LoginBase { get; set; } = "https://login.microsoftonline.com";
    public string GraphBase { get; set; } = "https://graph.microsoft.com/v1.0";
    /// <summary>For tests: how long to wait between checks (Microsoft says how often; this is the floor).</summary>
    public TimeSpan MinPoll { get; set; } = TimeSpan.FromSeconds(1);

    public event Action? Changed;

    public async Task<MicrosoftConnection> GetAsync()
    {
        await _lock.WaitAsync();
        try { return _cache ??= await Json.ReadAsync<MicrosoftConnection>(FilePath) ?? new(); }
        finally { _lock.Release(); }
    }

    public async Task<bool> IsConnectedAsync() => (await GetAsync()).Connected;

    private async Task SaveAsync(MicrosoftConnection c)
    {
        await _lock.WaitAsync();
        try { await Json.WriteAtomicAsync(FilePath, c); _cache = c; }
        finally { _lock.Release(); }
        Changed?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        var c = await GetAsync();
        _access = null;
        await SaveAsync(new MicrosoftConnection { ClientId = c.ClientId, Tenant = c.Tenant });
    }

    private string Endpoint(string tenant, string path) => $"{LoginBase}/{Uri.EscapeDataString(string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim())}/oauth2/v2.0/{path}";

    private static string Error(JsonNode? n, string body) =>
        n?["error_description"]?.ToString().Split('\n')[0].Split(" Trace ID")[0] ?? n?["error"]?.ToString() ?? body;

    /// <summary>Starts a sign-in: returns the code to show the user; <see cref="MicrosoftSignIn.Completed"/> finishes when they've entered it.</summary>
    public async Task<MicrosoftSignIn> StartSignInAsync(string clientId, string tenant, CancellationToken ct = default)
    {
        clientId = clientId.Trim();
        if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("The Application (client) ID is a GUID like 1a2b3c4d-…; copy it from your app registration's Overview page.");
        using var res = await Client.PostAsync(Endpoint(tenant, "devicecode"),
            new FormUrlEncodedContent([new("client_id", clientId), new("scope", Scopes)]), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var n = JsonNode.Parse(body);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft sign-in: {Error(n, body)}");
        var deviceCode = n!["device_code"]!.ToString();
        var interval = TimeSpan.FromSeconds(n["interval"]?.GetValue<int>() ?? 5);
        var expires = DateTimeOffset.Now.AddSeconds(n["expires_in"]?.GetValue<int>() ?? 900);
        var completed = PollAsync(clientId, tenant, deviceCode, interval < MinPoll ? MinPoll : interval, expires, ct);
        return new MicrosoftSignIn(n["user_code"]!.ToString(), n["verification_uri"]?.ToString() ?? "https://microsoft.com/devicelogin",
            n["message"]?.ToString() ?? "", completed);
    }

    private async Task<string> PollAsync(string clientId, string tenant, string deviceCode, TimeSpan interval, DateTimeOffset expires, CancellationToken ct)
    {
        while (DateTimeOffset.Now < expires)
        {
            await Task.Delay(interval, ct);
            using var res = await Client.PostAsync(Endpoint(tenant, "token"), new FormUrlEncodedContent(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), new("client_id", clientId), new("device_code", deviceCode),
            ]), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var n = JsonNode.Parse(body);
            switch (n?["error"]?.ToString())
            {
                case "authorization_pending": continue;
                case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                case "authorization_declined": throw new InvalidOperationException("Sign-in was declined.");
                case "expired_token": throw new InvalidOperationException("The code expired. Start again.");
                case { } other: throw new InvalidOperationException($"Microsoft sign-in: {Error(n, other)}");
            }
            await StoreTokensAsync(n!, clientId, tenant);
            var account = await AccountNameAsync(ct);
            var c = await GetAsync();
            c.Account = account;
            await SaveAsync(c);
            return account;
        }
        throw new InvalidOperationException("The code expired. Start again.");
    }

    private async Task StoreTokensAsync(JsonNode n, string clientId, string tenant)
    {
        _access = (n["access_token"]!.ToString(), DateTimeOffset.Now.AddSeconds((n["expires_in"]?.GetValue<int>() ?? 3600) - 120));
        var c = await GetAsync();
        var refresh = n["refresh_token"]?.ToString();
        await SaveAsync(new MicrosoftConnection
        {
            ClientId = clientId, Tenant = tenant, Account = c.Account,
            // Microsoft rotates refresh tokens: keep the newest.
            RefreshTokenSealed = string.IsNullOrEmpty(refresh) ? c.RefreshTokenSealed : _box.Seal(refresh),
            ConnectedAt = c.ConnectedAt ?? DateTimeOffset.Now,
        });
    }

    private async Task<string> AccountNameAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GraphBase + "/me?$select=userPrincipalName,mail,displayName");
            req.Headers.Authorization = new("Bearer", await AccessTokenAsync(ct));
            using var res = await Client.SendAsync(req, ct);
            var n = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            return n?["mail"]?.ToString() ?? n?["userPrincipalName"]?.ToString() ?? n?["displayName"]?.ToString() ?? "your account";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "your account"; }
    }

    /// <summary>A current access token for Graph, refreshed when needed.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        if (_access is { } a && a.expires > DateTimeOffset.Now) return a.token;
        var c = await GetAsync();
        if (!c.Connected) throw new InvalidOperationException("Microsoft 365 isn't connected (Connections → Microsoft).");
        using var res = await Client.PostAsync(Endpoint(c.Tenant, "token"), new FormUrlEncodedContent(
        [
            new("grant_type", "refresh_token"), new("client_id", c.ClientId), new("refresh_token", _box.Open(c.RefreshTokenSealed!)), new("scope", Scopes),
        ]), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var n = JsonNode.Parse(body);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft sign-in expired or was revoked; reconnect on the Connections page. ({Error(n, body)})");
        await StoreTokensAsync(n!, c.ClientId, c.Tenant);
        return _access!.Value.token;
    }
}
