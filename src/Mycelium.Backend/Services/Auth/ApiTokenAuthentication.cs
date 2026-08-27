using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mycelium.Backend.Services.Auth;

/// <summary>
/// Names and wire format for the long-lived API tokens that let scripts call this app unattended.
///
/// <para><b>Why the token has two halves.</b> A presented credential is
/// <c>myc_&lt;id&gt;.&lt;secret&gt;</c>: a public id and a 256-bit random secret. The split buys two
/// things that a single opaque blob doesn't. First, the database lookup keys on the id, so no query
/// this app runs is a function of the secret and there is no lookup timing to probe — the secret is
/// then compared with <see cref="CryptographicOperations.FixedTimeEquals"/> and nothing else.
/// Second, a rejected token can be *named* in a log line by its id without the secret going anywhere
/// near the log, which is what makes "no token values in logs, ever" survivable when someone has to
/// work out why the cron job started 401ing.</para>
///
/// <para>The <c>myc_</c> prefix is there so the string is recognisable on sight — in a
/// <c>.env</c>, a paste, or a secret scanner — as this app's credential and not, say, a Plex token.
/// The id is hex and the secret is base64url, separated by <c>'.'</c>: base64url's alphabet contains
/// <c>'_'</c> and <c>'-'</c> but never <c>'.'</c>, so the split is unambiguous even though the fixed
/// prefix ends in an underscore.</para>
/// </summary>
public static class ApiTokenDefaults
{
    /// <summary>The authentication scheme name. Composed with the cookie scheme by a policy scheme —
    /// see <see cref="BffAuthentication"/>.</summary>
    public const string AuthenticationScheme = "ApiToken";

    /// <summary>Marks a string as one of ours, for scanners and for whoever finds it in a config file.</summary>
    public const string Prefix = "myc_";

    /// <summary>
    /// The primary header: <c>Authorization: Bearer myc_&lt;id&gt;.&lt;secret&gt;</c>. Standard, so
    /// every HTTP client has a first-class way to send it and well-behaved proxies and log formatters
    /// already know to redact it.
    /// </summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The fallback header, for deployments where <c>Authorization</c> isn't ours to use. This app is
    /// documented as sitting behind a reverse proxy alongside Authentik (see DEPLOYMENT.md), and a
    /// forward-auth outpost in that position may consume or overwrite <c>Authorization</c> for its own
    /// handshake — which would strip the token before the app ever saw it, with a 401 as the only
    /// symptom and nothing in this app's logs to explain it. A second, app-specific header costs four
    /// lines and makes that situation recoverable without re-architecting somebody's proxy.
    /// </summary>
    public const string HeaderName = "X-Mycelium-Token";

    /// <summary>
    /// Reads a presented token from the request, preferring <c>Authorization: Bearer</c>. Returns
    /// false when neither header carries one — which is the ordinary case for every browser request,
    /// and must stay cheap because it is asked on every single request (see the forwarding selector in
    /// <see cref="BffAuthentication"/>).
    /// </summary>
    public static bool TryRead(HttpRequest request, out string token)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = authorization[BearerPrefix.Length..].Trim();
            return token.Length > 0;
        }

        var custom = request.Headers[HeaderName].ToString().Trim();
        token = custom;
        return custom.Length > 0;
    }

    /// <summary>Whether the request is presenting a token at all — the question the scheme selector asks.</summary>
    public static bool HasToken(HttpRequest request) => TryRead(request, out _);

    /// <summary>Mints a fresh id and secret, and the single string the caller is handed.</summary>
    public static (string Id, byte[] SecretHash, string Token) Generate()
    {
        // 8 bytes of id: not a secret, just wide enough that ids don't collide and can't be walked.
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        // 32 bytes of secret — 256 bits from the OS CSPRNG. This is the whole of the security here.
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url.EncodeToString(secretBytes);
        return (id, SHA256.HashData(secretBytes), $"{Prefix}{id}.{secret}");
    }

    /// <summary>
    /// Splits a presented token into its id and the SHA-256 of its secret, or returns false if it
    /// isn't shaped like one of ours. Never throws: a malformed credential is a 401, and letting a
    /// stray base64 character become a 500 would both mislead the caller and put the raw value into an
    /// exception message headed for the log.
    /// </summary>
    public static bool TryParse(string presented, out string id, out byte[] secretHash)
    {
        id = "";
        secretHash = Array.Empty<byte>();

        if (!presented.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = presented[Prefix.Length..];
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1)
        {
            return false;
        }

        var secret = body[(dot + 1)..];
        if (!Base64Url.IsValid(secret))
        {
            return false;
        }

        id = body[..dot];
        secretHash = SHA256.HashData(Base64Url.DecodeFromChars(secret));
        return true;
    }
}

/// <summary>
/// The claims an API-token identity carries beyond the ordinary user ones, and the questions the rest
/// of the app asks about them.
///
/// <para>A token principal is deliberately <em>almost</em> indistinguishable from a cookie one: it
/// carries <c>sub</c> and <c>preferred_username</c> exactly as the OIDC login does, because everything
/// downstream reads those and none of it should have to care how the caller signed in. The one place
/// the difference has to be visible is privilege — see <see cref="AllowsDev"/>.</para>
/// </summary>
public static class ApiTokenClaims
{
    /// <summary>The token's public id. Its presence is also what marks a principal as token-authenticated.</summary>
    public const string TokenId = "myc:api_token_id";

    /// <summary>Present, with value "true", only on a token minted with dev scope explicitly asked for.</summary>
    public const string DevScope = "myc:api_token_dev";

    /// <summary>Whether this principal signed in with an API token rather than an interactive session.</summary>
    public static bool IsApiToken(ClaimsPrincipal principal) =>
        principal.FindFirst(TokenId) is not null;

    /// <summary>
    /// Whether this principal may reach the dev endpoints, <em>as far as the credential is concerned</em>.
    /// Says nothing about whether the user is in <c>DEV_USERNAMES</c> — that is
    /// <see cref="DevUsers.Includes"/>'s job, and both must agree (see <see cref="DevUsers.AllowsDevTools"/>).
    ///
    /// <para>An interactive session always passes: a dev user at a browser is exactly who the dev panel
    /// is for. A token passes only if it was minted with dev scope. Being in <c>DEV_USERNAMES</c> is
    /// not enough on its own, and that is the entire point: the dev routes include wiping every
    /// <c>_liked</c>/<c>_disliked</c> tag off the whole Plex library, and a maintainer's ordinary
    /// automation token — sitting in a cron job, on a laptop, in a CI secret — must not be able to do
    /// that just because its owner could do it by hand.</para>
    /// </summary>
    public static bool AllowsDev(ClaimsPrincipal principal) =>
        !IsApiToken(principal) || principal.HasClaim(DevScope, "true");
}

/// <summary>
/// Authenticates a request presenting an API token. Deliberately thin: every decision lives in
/// <see cref="ApiTokenService"/>, which is where it can be tested without an HTTP stack.
///
/// <para>This is a real authentication scheme rather than a piece of middleware, so that
/// <c>[Authorize]</c>/<c>RequireAuthorization()</c>, the <c>DevUser</c> policy, and
/// <c>HttpContext.User</c> all mean the same thing whichever credential arrived. Middleware that
/// assigned <c>HttpContext.User</c> by hand would work today and quietly diverge the first time a
/// policy named a scheme.</para>
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiTokenService _tokens;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiTokenDefaults.TryRead(Request, out var presented))
        {
            // No credential offered. NoResult, not Fail: "didn't try" is not "tried and was wrong",
            // and only the latter is worth a log line.
            return AuthenticateResult.NoResult();
        }

        var verdict = await _tokens.Verify(presented);
        return verdict.Principal is { } principal
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
            // The reason is for us, not the caller: it never reaches the response, which is a bare 401.
            // Telling an unauthenticated client whether its token was unknown, revoked or merely
            // expired is free reconnaissance.
            : AuthenticateResult.Fail($"API token rejected: {verdict.Failure}");
    }

    /// <summary>
    /// A rejected token gets a plain 401 — never a redirect, and never the 500 that an unhandled parse
    /// failure would produce. <c>WWW-Authenticate</c> so the answer is a well-formed one; there is no
    /// login page to send an unattended script to.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"{ApiTokenDefaults.Prefix.TrimEnd('_')}\"";
        return Task.CompletedTask;
    }

    /// <summary>Authenticated but not allowed here (the usual case: a token without dev scope on a dev
    /// route). 403, so the caller can tell "wrong credential" from "not enough credential".</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
