namespace Mycelium.Interfaces;

/// <summary>
/// One long-lived API token, as stored. It authenticates a script <em>as</em> an existing user, so
/// <see cref="Subject"/> is an OIDC subject from <see cref="IUserRepo"/> and every per-user behaviour
/// downstream (ratings, the <c>&lt;username&gt;_liked</c> Plex mood tags, the quality tier) keeps
/// working with no notion that a token was involved.
///
/// <para><b>The secret is not here.</b> <see cref="SecretHash"/> is a SHA-256 of the random half of
/// the token and is the only trace of it the app keeps — the token itself exists exactly once, in the
/// HTTP response that minted it. This is the one place this app's credential handling differs from
/// the Deezer ARL (<c>StreamripArlStore</c>) and the Plex server token
/// (<c>IPlexServerTokenRepo</c>), and the reason is that those two are <em>replayed</em> to a third
/// party — the app has to be able to produce the original string, so it must store it. This one is
/// only ever <em>checked</em>, so it doesn't, and a dump of the <c>apiTokens</c> collection hands an
/// attacker nothing they can present.</para>
///
/// <para>A plain SHA-256 rather than a password KDF on purpose: the secret is 256 bits from a CSPRNG,
/// not a human-chosen password, so there is no dictionary to run and nothing for work factors to buy.
/// Argon2/PBKDF2 here would only add per-request CPU on the authentication path — a denial-of-service
/// lever aimed at the app by anyone who can send it a wrong token.</para>
/// </summary>
/// <param name="Id">
/// The token's public half — not a secret. It is carried in the token string, so it can be recovered
/// from a presented credential and named in a log line, which is how a rejection is diagnosable
/// without the value ever being written down. Also what the row is looked up by, so the query that
/// finds a token never depends on the secret.
/// </param>
/// <param name="Name">What the operator called it ("seed script", "playlist acquisition") — a label
/// for the revoke list, nothing more.</param>
/// <param name="DevScope">
/// Whether this token may use the dev endpoints. Default false, and settable only at creation: the
/// dev routes include wiping every mood tag in the library, which is not something a credential
/// living in a cron job should be able to reach because its owner happens to be in
/// <c>DEV_USERNAMES</c>.
/// </param>
/// <param name="ExpiresAt">When it stops working, or null for "until revoked".</param>
/// <param name="RevokedAt">When it was revoked, or null while it is live. Revocation keeps the row
/// rather than deleting it, so the id in an old log line still resolves to something.</param>
public record ApiTokenRecord(
    string Id,
    string Subject,
    string Name,
    byte[] SecretHash,
    bool DevScope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Stores the API tokens that let scripts call this app without a browser session. Minted from a
/// signed-in session and stored here, in the same spirit as the Plex server token: a credential the
/// operator can replace at runtime rather than one baked into the environment and needing a redeploy
/// to change.
/// </summary>
public interface IApiTokenRepo
{
    Task Add(ApiTokenRecord token);

    /// <summary>
    /// The row for a token id, or null if there is none. Lookup is by the token's <em>public</em> id,
    /// never by its secret or a hash of it — so the database query itself carries no secret and its
    /// timing leaks nothing. The secret is checked afterwards, in constant time.
    /// </summary>
    Task<ApiTokenRecord?> Get(string id);

    /// <summary>Every token belonging to one user, newest first. Revoked and expired rows included —
    /// the list is what an operator audits, and "what did I once hand out" is part of that.</summary>
    Task<ApiTokenRecord[]> GetForSubject(string subject);

    /// <summary>
    /// Revokes a token, returning whether it existed and was live. Scoped by <paramref name="subject"/>
    /// as well as id so that guessing an id — the public half, which appears in logs — can't revoke
    /// someone else's credential.
    /// </summary>
    Task<bool> Revoke(string id, string subject, DateTimeOffset at);
}
