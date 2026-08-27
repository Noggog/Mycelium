using System.Security.Claims;
using System.Security.Cryptography;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Auth;

/// <summary>Why a presented token wasn't accepted. Kept out of the HTTP response on purpose — see
/// <see cref="ApiTokenAuthenticationHandler.HandleAuthenticateAsync"/>.</summary>
public enum ApiTokenFailure
{
    /// <summary>Not shaped like one of ours at all — a stale cookie value, a Plex token, a typo.</summary>
    Malformed,

    /// <summary>Well-formed, but no such id. Either invented, or minted on another deployment.</summary>
    Unknown,

    /// <summary>The id exists but the secret doesn't match it.</summary>
    BadSecret,

    Revoked,
    Expired,

    /// <summary>The account it was minted for is no longer in the user store.</summary>
    NoSuchUser,

    /// <summary>The account has no <c>preferred_username</c> — see <see cref="ApiTokenService.Verify"/>.</summary>
    NoUsername,
}

/// <summary>The outcome of checking a presented token: a principal, or a reason there isn't one.</summary>
/// <param name="TokenId">The public id, when the credential was at least well-formed enough to have
/// one. Safe to log; the secret never is.</param>
public record ApiTokenVerdict(ClaimsPrincipal? Principal, ApiTokenFailure? Failure, string? TokenId);

/// <summary>What a caller is handed when a token is created. The only time <see cref="Token"/> exists.</summary>
public record ApiTokenMinted(
    string Id,
    string Token,
    string Name,
    string Subject,
    bool DevScope,
    DateTimeOffset? ExpiresAt);

/// <summary>A token as listed back — everything except the one field that matters to an attacker.</summary>
public record ApiTokenSummary(
    string Id,
    string Name,
    bool DevScope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>Result of a mint attempt: the token, or why it was refused.</summary>
public record ApiTokenMintResult(ApiTokenMinted? Minted, string? Error);

/// <summary>
/// Long-lived API tokens: minting them, and checking the ones that come back.
///
/// <para><b>Why this exists.</b> Every <c>/api</c> route sits behind a session cookie, so the scripts
/// that drive this app authenticated by pasting a cookie copied out of browser devtools. That cookie
/// lapses, and when it does the run dies with a 401 that only a human at a browser can fix — which is
/// the single thing standing between the acquisition workflow and running unattended. A token is the
/// same identity with a lifetime an operator chooses rather than one a session hands them.</para>
///
/// <para><b>It is the user, not a service account.</b> A token names an existing OIDC subject and
/// authenticates as them, so the ratings it writes are that person's ratings, the mood tags it stamps
/// are <c>&lt;their username&gt;_liked</c>, and the albums it queues come down at their quality tier.
/// Nothing downstream has a code path for "a robot did this", and nothing needed one.</para>
/// </summary>
public class ApiTokenService
{
    private readonly IApiTokenRepo _tokens;
    private readonly IUserRepo _users;
    private readonly ILogger<ApiTokenService> _logger;

    public ApiTokenService(IApiTokenRepo tokens, IUserRepo users, ILogger<ApiTokenService> logger)
    {
        _tokens = tokens;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// Creates a token for <paramref name="subject"/>. The returned string is the only copy that will
    /// ever exist — what is stored is a hash of half of it.
    ///
    /// <para>Refused for a user with no <c>preferred_username</c>. That looks like an odd thing to be
    /// strict about until you follow what the username is <em>for</em>: <c>ArtistTag.For</c> builds
    /// the Plex mood tag out of it and returns null when there is none, and a null tag is skipped
    /// silently. A token for such an account would authenticate, rate, queue downloads and write no
    /// tags at all — a failure that surfaces weeks later as "my smart playlist is empty". Better to
    /// refuse to mint it, at the one moment there is a person present to read the reason.</para>
    /// </summary>
    public async Task<ApiTokenMintResult> Mint(
        string subject, string? name, bool devScope, TimeSpan? lifetime)
    {
        var user = await _users.Get(subject);
        if (user is null)
        {
            return new ApiTokenMintResult(null, "No such user.");
        }

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            return new ApiTokenMintResult(
                null,
                "That account has no username, so a token for it could not write the per-user Plex "
                + "mood tags. Sign in to the app once as that user first.");
        }

        if (lifetime is { } span && span <= TimeSpan.Zero)
        {
            // An expiry in the past is a client bug, and one that would mint a credential dead on
            // arrival — say so instead of handing back something that 401s immediately.
            return new ApiTokenMintResult(null, "Expiry must be in the future.");
        }

        var now = DateTimeOffset.UtcNow;
        var (id, secretHash, token) = ApiTokenDefaults.Generate();
        var label = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
        var expiresAt = lifetime is { } l ? now + l : (DateTimeOffset?)null;

        await _tokens.Add(new ApiTokenRecord(
            Id: id,
            Subject: subject,
            Name: label,
            SecretHash: secretHash,
            DevScope: devScope,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            RevokedAt: null));

        // The id, never the token. Worth an Information line: this is a credential coming into
        // existence, and dev scope in particular is something an audit should be able to find.
        _logger.LogInformation(
            "Minted API token {TokenId} ({Name}) for {Username}; dev scope {DevScope}, expires {ExpiresAt}",
            id, label, user.Username, devScope, expiresAt?.ToString("u") ?? "never");

        return new ApiTokenMintResult(
            new ApiTokenMinted(id, token, label, subject, devScope, expiresAt), null);
    }

    /// <summary>
    /// Checks a presented token and builds the principal it authenticates as, or explains why not.
    ///
    /// <para>The claims are built from the <em>current</em> user record rather than from anything
    /// snapshotted at mint time, so a token tracks its owner's profile the way a fresh login would. It
    /// costs one extra read per request on a path that serves scripts, which is the right trade:
    /// baking the username into the token document would mean that a user who changed theirs would
    /// have their automation quietly keep stamping the old <c>_liked</c> tag while the browser stamped
    /// the new one, and nothing would ever report the split.</para>
    ///
    /// <para>Never throws for a bad credential — malformed input is parsed defensively and every
    /// negative path returns a verdict. A store that is down still throws, and should: that is a 500,
    /// and calling it a 401 would send an operator to re-mint a token that was fine.</para>
    /// </summary>
    public async Task<ApiTokenVerdict> Verify(string presented)
    {
        if (!ApiTokenDefaults.TryParse(presented, out var id, out var presentedHash))
        {
            return Reject(ApiTokenFailure.Malformed, null);
        }

        var record = await _tokens.Get(id);
        if (record is null)
        {
            return Reject(ApiTokenFailure.Unknown, id);
        }

        // Constant time, and the only comparison of the secret anywhere. Both sides are SHA-256
        // digests, so this can't leak length either. It also means a store that returned the wrong row
        // for an id could not authenticate anyone — the row has to actually carry the matching hash.
        if (!CryptographicOperations.FixedTimeEquals(record.SecretHash, presentedHash))
        {
            return Reject(ApiTokenFailure.BadSecret, id);
        }

        var now = DateTimeOffset.UtcNow;
        if (record.RevokedAt is not null)
        {
            return Reject(ApiTokenFailure.Revoked, id);
        }

        if (record.ExpiresAt is { } expiry && expiry <= now)
        {
            return Reject(ApiTokenFailure.Expired, id);
        }

        var user = await _users.Get(record.Subject);
        if (user is null)
        {
            return Reject(ApiTokenFailure.NoSuchUser, id);
        }

        if (string.IsNullOrWhiteSpace(user.Username))
        {
            // Mint refuses this case, so reaching it means the account lost its username afterwards.
            // Failing closed rather than authenticating a principal that would write no mood tags:
            // the whole reason Mint is strict about this is that the silent version is undebuggable.
            return Reject(ApiTokenFailure.NoUsername, id);
        }

        return new ApiTokenVerdict(BuildPrincipal(record, user), null, id);
    }

    /// <summary>Every token this user holds, for the revoke list. No secrets, by construction — the
    /// service has none to leak.</summary>
    public async Task<ApiTokenSummary[]> List(string subject)
    {
        var now = DateTimeOffset.UtcNow;
        return (await _tokens.GetForSubject(subject))
            .Select(t => new ApiTokenSummary(
                t.Id, t.Name, t.DevScope, t.CreatedAt, t.ExpiresAt, t.RevokedAt,
                Active: t.RevokedAt is null && (t.ExpiresAt is null || t.ExpiresAt > now)))
            .ToArray();
    }

    /// <summary>Revokes one of this user's tokens. False if there was no live token of theirs by that id.</summary>
    public async Task<bool> Revoke(string subject, string id)
    {
        var revoked = await _tokens.Revoke(id, subject, DateTimeOffset.UtcNow);
        if (revoked)
        {
            _logger.LogInformation("API token {TokenId} was revoked.", id);
        }

        return revoked;
    }

    /// <summary>
    /// The identity a token authenticates as. Claim types match what the OIDC login produces
    /// (<c>MapInboundClaims = false</c> keeps those raw), so <c>GetSubject()</c>,
    /// <c>preferred_username</c> and the name/email the SPA reads all resolve identically whichever
    /// way the caller signed in. <c>preferred_username</c> is also the name claim, mirroring the
    /// OIDC handler's <c>NameClaimType</c>.
    /// </summary>
    private static ClaimsPrincipal BuildPrincipal(ApiTokenRecord record, AppUser user)
    {
        var claims = new List<Claim>
        {
            new("sub", record.Subject),
            new("preferred_username", user.Username!),
            // Identifies the credential, not the user — this is what makes a request traceable back to
            // one token, and what the DevUser gate keys off to tell a token from a browser session.
            new(ApiTokenClaims.TokenId, record.Id),
        };

        if (record.DevScope)
        {
            claims.Add(new Claim(ApiTokenClaims.DevScope, "true"));
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim("email", user.Email));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim("name", user.DisplayName));
        }

        var identity = new ClaimsIdentity(
            claims,
            authenticationType: ApiTokenDefaults.AuthenticationScheme,
            nameType: "preferred_username",
            roleType: ClaimsIdentity.DefaultRoleClaimType);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// One place every rejection passes through, so there is exactly one line to audit for "does this
    /// log the credential?". It doesn't: only the reason and, when the credential was well-formed
    /// enough to have one, the public id. A malformed token has nothing loggable at all — saying so is
    /// more useful than it sounds, since it distinguishes "sent us a cookie by mistake" from "sent us
    /// a token we've never heard of".
    /// </summary>
    private ApiTokenVerdict Reject(ApiTokenFailure failure, string? id)
    {
        _logger.LogWarning(
            "Rejected an API token ({Reason}); token id {TokenId}",
            failure, id ?? "(unparseable)");
        return new ApiTokenVerdict(null, failure, id);
    }
}
