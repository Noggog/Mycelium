using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Auth;

/// <summary>
/// Backend-for-frontend (BFF) authentication: the backend runs the OIDC authorization-code flow
/// against the IdP and issues an HttpOnly session cookie; the SPA never sees tokens. Configured for
/// the dev topology where the browser reaches these endpoints through the Vite proxy at the public
/// origin, so the callback (and cookie) land on the SPA origin regardless of the backend's port.
/// </summary>
public static class BffAuthentication
{
    public static void AddBffAuthentication(this WebApplicationBuilder builder)
    {
        // Issuer URL + client credentials of the OIDC provider (Authentik). Required for login;
        // supplied via env (local.secrets.env). When unset the app still runs — only login fails —
        // so non-auth features stay usable in local dev without an IdP configured.
        var authority = Environment.GetEnvironmentVariable("OIDC_AUTHORITY") ?? "";
        var clientId = Environment.GetEnvironmentVariable("OIDC_CLIENT_ID") ?? "";
        var clientSecret = Environment.GetEnvironmentVariable("OIDC_CLIENT_SECRET") ?? "";
        var publicOrigin = (Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")
                            ?? "http://localhost:5173").TrimEnd('/');

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Challenge falls through to the cookie scheme, which answers 401 (see
                // OnRedirectToLogin below). Deliberately NOT the OIDC scheme: this is an API, and an
                // OIDC challenge writes a correlation and a nonce cookie before redirecting to the
                // IdP. A fetch() can never finish that login, so every such response left two dead
                // cookies on the origin — a page polling an endpoint while signed out planted them
                // faster than they expired, until the request header outgrew Kestrel's 32KB limit
                // and the whole site answered 431 to that browser.
                //
                // Login is started deliberately, by the browser navigating to /auth/login, which
                // names the OIDC scheme itself. Nothing else should ever start one.
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "myc.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                // This is an API: don't 302 to a login page, answer with status codes the SPA reads.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                // Use a GET (query) callback instead of the default form_post. The IdP is on a
                // different site from the SPA, and SameSite=Lax cookies are sent on cross-site
                // top-level GET navigations but NOT on cross-site POSTs — form_post would drop the
                // correlation/nonce cookies and fail login. Code stays protected by PKCE.
                options.ResponseMode = "query";
                options.RequireHttpsMetadata = false; // dev: Keycloak runs over http
                options.SaveTokens = true;             // keep id_token for sign-out hint
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;      // keep raw claim types ("sub", "preferred_username", ...)
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                options.TokenValidationParameters.NameClaimType = "preferred_username";

                // Dev over HTTP: the callback is a same-site top-level GET (localhost:8080 ->
                // localhost:5173 — port doesn't affect "site"), so Lax cookies are sent and we
                // sidestep the SameSite=None+Secure requirement that breaks on plain http.
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // Challenge happens at /auth/login but the callback is /signin-oidc; scope these to
                // "/" so the cookies set during the challenge are sent back on the callback.
                options.NonceCookie.Path = "/";
                options.CorrelationCookie.Path = "/";

                // Force the browser-facing redirect URI so the callback returns through the Vite
                // proxy onto the SPA origin (where the auth cookie must live), independent of the
                // backend's dynamic internal host/port.
                options.Events.OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.RedirectUri = publicOrigin + options.CallbackPath;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = ctx =>
                {
                    ctx.ProtocolMessage.PostLogoutRedirectUri = publicOrigin + "/";
                    return Task.CompletedTask;
                };

                // A completed login makes every pending correlation/nonce cookie moot — including
                // any left by an abandoned attempt. Clearing them here keeps a browser from carrying
                // dead ones around until they lapse, so a session that accumulated some recovers by
                // signing in rather than by the user clearing site data.
                options.Events.OnTicketReceived = ctx =>
                {
                    foreach (var name in ctx.HttpContext.Request.Cookies.Keys)
                    {
                        if (name.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)
                            || name.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal))
                        {
                            ctx.Response.Cookies.Delete(name, new CookieOptions { Path = "/" });
                        }
                    }

                    return Task.CompletedTask;
                };

                // Mirror the IdP identity into our user store on every login.
                options.Events.OnTokenValidated = async ctx =>
                {
                    var principal = ctx.Principal;
                    var subject = principal?.FindFirst("sub")?.Value;
                    if (subject == null) return;

                    var now = DateTimeOffset.UtcNow;
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<IUserRepo>();
                    await users.UpsertOnLogin(new AppUser(
                        Subject: subject,
                        Username: principal!.FindFirst("preferred_username")?.Value,
                        Email: principal.FindFirst("email")?.Value,
                        DisplayName: principal.FindFirst("name")?.Value,
                        FirstSeenAt: now,
                        LastLoginAt: now));
                };
            });

        // Who may use the in-app dev panel and its endpoints — comma-separated preferred_usernames
        // from DEV_USERNAMES (empty = nobody). Registered so /auth/me can flag the current user and
        // the "DevUser" policy can gate the dev routes server-side (a UI-only gate wouldn't protect
        // the destructive tag-maintenance endpoints).
        var devUsers = new DevUsers(Environment.GetEnvironmentVariable("DEV_USERNAMES"));
        builder.Services.AddSingleton(devUsers);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("DevUser", policy => policy.RequireAssertion(ctx => devUsers.Includes(ctx.User))));
    }

    /// <summary>The OIDC subject ("sub") of the current user, or null if unauthenticated.</summary>
    public static string? GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value;
}
