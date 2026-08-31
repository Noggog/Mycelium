using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Mycelium.Backend.Services.Background;
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
    /// <summary>
    /// The default scheme: a policy scheme that dispatches to the cookie or the API-token scheme
    /// depending on what the request carries. Never authenticates anything itself.
    /// </summary>
    public const string SelectorScheme = "Mycelium";

    /// <summary>
    /// "A human, at a browser, right now." The cookie scheme and nothing else.
    ///
    /// <para>Used to gate the token-management endpoints. An API token can therefore call the whole
    /// API as its user but cannot mint another token or revoke one — so a leaked token is a credential
    /// with a fixed blast radius and a fixed lifetime, not a foothold that can issue itself fresh
    /// credentials and delete the trail. Rotation stays a thing a person does, which for a credential
    /// measured in months is the right cadence anyway.</para>
    /// </summary>
    public const string InteractiveUserPolicy = "InteractiveUser";

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
                // The default is a *policy* scheme, not a real one: it looks at the request and hands
                // off to either the cookie scheme or the API-token scheme (see AddPolicyScheme below).
                // Doing it here rather than per-route is what lets every existing
                // RequireAuthorization() and RequireAuthorization("DevUser") keep working verbatim
                // while gaining a second way to authenticate — the alternative, naming both schemes on
                // forty-odd endpoints, is forty-odd chances to miss one.
                options.DefaultScheme = SelectorScheme;
                // Sign-in and sign-out must name a real scheme. Left to default they would inherit
                // DefaultScheme — the policy scheme above — which can do neither, and the OIDC
                // callback would fail at the moment it tried to issue the session cookie.
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
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
            // Picks the credential the request actually presented. A request carrying an API token
            // header is never treated as a browser session and vice versa, so the two paths can't
            // shadow each other: a script with a bad token gets a token 401 rather than silently
            // falling back to an anonymous cookie identity, and a browser is never asked for a bearer.
            // Forwarding covers challenge and forbid as well as authenticate, so the 401 a caller sees
            // is written by the handler for the credential they used.
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
            {
                options.ForwardDefaultSelector = context =>
                    ApiTokenDefaults.HasToken(context.Request)
                        ? ApiTokenDefaults.AuthenticationScheme
                        : CookieAuthenticationDefaults.AuthenticationScheme;
            })
            // Long-lived tokens for unattended scripts. A second authentication scheme rather than
            // middleware, so HttpContext.User, [Authorize] and every policy mean the same thing however
            // the caller authenticated. See ApiTokenAuthenticationHandler.
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenDefaults.AuthenticationScheme, _ => { })
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
                    var username = principal!.FindFirst("preferred_username")?.Value;
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<IUserRepo>();
                    var isNewUser = await users.UpsertOnLogin(new AppUser(
                        Subject: subject,
                        Username: username,
                        Email: principal.FindFirst("email")?.Value,
                        DisplayName: principal.FindFirst("name")?.Value,
                        FirstSeenAt: now,
                        LastLoginAt: now));

                    if (isNewUser)
                    {
                        // The one bit of per-user setup that has to happen before the account is
                        // usable rather than on the next nightly pass: without a seeded "_disliked"
                        // mood, Plex has no tag id for it and the Deep Frontier this user builds today
                        // would silently be the un-excluded one (see MoodTagSeeder). Queued, never
                        // awaited — a login must not wait on Plex, or fail because Plex did.
                        ctx.HttpContext.RequestServices
                            .GetRequiredService<ArtistFollowUpService>()
                            .QueueMoodSeed(username);
                    }
                };
            });

        // Who may use the in-app dev panel and its endpoints — comma-separated preferred_usernames
        // from DEV_USERNAMES (empty = nobody). Registered so /auth/me can flag the current user and
        // the "DevUser" policy can gate the dev routes server-side (a UI-only gate wouldn't protect
        // the destructive tag-maintenance endpoints).
        var devUsers = new DevUsers(Environment.GetEnvironmentVariable("DEV_USERNAMES"));
        builder.Services.AddSingleton(devUsers);

        // Minting and checking the API tokens. Registered here rather than in MainModule's assembly
        // scan because the scan only covers Services.Singletons; its own dependencies (IApiTokenRepo,
        // IUserRepo) come from Autofac, which is the same container this collection is folded into.
        builder.Services.AddSingleton<ApiTokenService>();

        builder.Services.AddAuthorization(options =>
        {
            // Both halves have to agree: the user is listed in DEV_USERNAMES *and* the credential in
            // hand is allowed dev scope. Checking only the first — which is all this did before tokens
            // existed, and was correct then — would hand every dev user's automation token the ability
            // to wipe the library's mood tags. See DevUsers.AllowsDevTools.
            options.AddPolicy("DevUser", policy =>
                policy.RequireAssertion(ctx => devUsers.AllowsDevTools(ctx.User)));

            // Naming the cookie scheme makes the policy re-authenticate against it alone, so a request
            // holding only an API token is unauthenticated *for these endpoints* and gets a 401 —
            // regardless of what the selector scheme decided for the request as a whole.
            options.AddPolicy(InteractiveUserPolicy, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());
        });
    }

    /// <summary>The OIDC subject ("sub") of the current user, or null if unauthenticated.</summary>
    public static string? GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value;
}
