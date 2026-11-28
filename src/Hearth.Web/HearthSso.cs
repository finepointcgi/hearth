using System.Net;
using Hearth.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Hearth.Web;

/// <summary>
/// Single sign-on through any OpenID Connect provider (Keycloak, Entra ID, Okta, Authentik…), configured under
/// <c>Hearth:Oidc</c> (env <c>Hearth__Oidc__Authority</c>, <c>__ClientId</c>, <c>__ClientSecret</c>, <c>__OwnerUser</c>…).
/// Each person who signs in gets their own Hearth account (Settings → Accounts), linked to their identity, so they see only
/// their own bots, chats and memories. The owner (<c>OwnerUser</c>, the provider's user name) keeps the existing data: while
/// accounts are off, their first sign-in turns accounts on. Other people only get accounts without computer access.
/// </summary>
public sealed record HearthSso(string Authority, string ClientId, string? ClientSecret, string DisplayName, string? OwnerUser, bool AutoCreateAccounts, bool RequireHttpsMetadata)
{
    private const string TempScheme = "hearth-sso";
    private const string OidcScheme = "hearth-oidc";
    public const string StartPath = "/login/sso";

    /// <summary>The configured sign-on, or null when <c>Hearth:Oidc:Authority</c> isn't set.</summary>
    public static HearthSso? FromConfiguration(IConfiguration configuration)
    {
        var s = configuration.GetSection("Hearth:Oidc");
        var authority = s["Authority"]?.Trim();
        if (string.IsNullOrEmpty(authority)) return null;
        var clientId = s["ClientId"]?.Trim();
        if (string.IsNullOrEmpty(clientId)) throw new InvalidOperationException("Hearth:Oidc:ClientId is required when Hearth:Oidc:Authority is set.");
        return new HearthSso(authority, clientId, NullIfEmpty(s["ClientSecret"]), NullIfEmpty(s["DisplayName"]) ?? "single sign-on", NullIfEmpty(s["OwnerUser"]),
            !string.Equals(s["AutoCreateAccounts"], "false", StringComparison.OrdinalIgnoreCase),
            !string.Equals(s["RequireHttpsMetadata"], "false", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public void AddTo(IServiceCollection services)
    {
        services.AddAuthentication(o => o.DefaultSignInScheme = TempScheme)
            // Holds the provider's answer for the moment between its callback and Hearth's own account cookie.
            .AddCookie(TempScheme, o =>
            {
                o.Cookie.Name = "hearth_sso";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddOpenIdConnect(OidcScheme, o =>
            {
                o.Authority = Authority;
                o.ClientId = ClientId;
                o.ClientSecret = ClientSecret;
                o.SignInScheme = TempScheme;
                o.CallbackPath = StartPath + "/callback";   // under /login, which the sign-in gate leaves open
                o.ResponseType = "code";
                o.UsePkce = true;
                o.RequireHttpsMetadata = RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email" }) o.Scope.Add(scope);
            });
    }

    public void MapEndpoints(WebApplication app, AccountStore accounts)
    {
        app.MapGet(StartPath, () => Results.Challenge(new AuthenticationProperties { RedirectUri = StartPath + "/done" }, [OidcScheme]));
        app.MapGet(StartPath + "/done", async (HttpContext ctx) =>
        {
            var result = await ctx.AuthenticateAsync(TempScheme);
            await ctx.SignOutAsync(TempScheme);
            if (!result.Succeeded || result.Principal is not { } user) return Results.Redirect("/login");
            var subject = user.FindFirst("sub")?.Value;
            var issuer = user.FindFirst("iss")?.Value ?? Authority;
            var name = user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value ?? user.FindFirst("name")?.Value;
            if (subject is null || name is null) return Problem("The identity provider didn't say who you are (no sub or user name claim).");

            var (account, problem) = await accounts.SignInExternalAsync($"{issuer}|{subject}", name, OwnerUser, AutoCreateAccounts);
            if (account is null) return Problem(problem ?? "You can't sign in to this Hearth.");
            ctx.Response.Cookies.Append("hearth_user", accounts.CookieFor(account),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromDays(30) });
            return Results.Redirect("/");
        });
    }

    /// <summary>The button on the sign-in page.</summary>
    public string LoginButton() =>
        $"<a class=\"sso\" href=\"{StartPath}\">Sign in with {WebUtility.HtmlEncode(DisplayName)}</a>";

    private static IResult Problem(string message) => Results.Content(
        $"<html><body style='font-family:sans-serif;padding:2rem;background:#0a0a0a;color:#ececec'><h2>Couldn't sign you in</h2><p>{WebUtility.HtmlEncode(message)}</p><p><a style='color:#ececec' href='/login'>Back</a></p></body></html>",
        "text/html", statusCode: StatusCodes.Status403Forbidden);
}
