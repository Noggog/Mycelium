using System.Security.Claims;

namespace Mycelium.Backend.Services.Auth;

/// <summary>
/// The set of users allowed to see and use the in-app dev panel (Plex tag maintenance, similarity
/// debugging). Sourced from the <c>DEV_USERNAMES</c> env var — a comma-separated list of
/// <c>preferred_username</c>s. Empty means "nobody", so the panel and its endpoints are closed to
/// everyone unless explicitly opted in. Kept out of hardcoded config so who counts as a dev is a
/// per-deployment decision.
/// </summary>
public sealed class DevUsers
{
    private readonly HashSet<string> _usernames;

    public DevUsers(string? configured)
    {
        _usernames = (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .ToHashSet();
    }

    /// <summary>The configured dev usernames (lowercased) — for diagnostics/logging.</summary>
    public IReadOnlyCollection<string> Configured => _usernames;

    /// <summary>
    /// Whether the signed-in user's <c>preferred_username</c> is in the configured dev set. This is a
    /// question about the <em>person</em> only — for the gate that actually guards the dev endpoints,
    /// use <see cref="AllowsDevTools"/>.
    /// </summary>
    public bool Includes(ClaimsPrincipal user)
    {
        var username = user.FindFirst("preferred_username")?.Value?.ToLowerInvariant();
        return username != null && _usernames.Contains(username);
    }

    /// <summary>
    /// The real gate on the dev panel and its endpoints: the user is a dev <em>and</em> the credential
    /// they presented is allowed to act as one.
    ///
    /// <para>The second half only ever subtracts, and only for API tokens: an interactive session
    /// always passes it. It exists because <see cref="Includes"/> keys on
    /// <c>preferred_username</c>, which an API token carries exactly as a browser session does (it
    /// must — the Plex mood tags are built from it). Without this, minting an ordinary automation
    /// token as a maintainer would silently hand that token the destructive maintenance routes,
    /// including the one that strips every <c>_liked</c>/<c>_disliked</c> tag off the whole library.
    /// Dev scope on a token is opt-in at creation and off by default.</para>
    ///
    /// <para>Used by the <c>DevUser</c> authorization policy and by <c>/auth/me</c>, so that what the
    /// UI shows and what the server enforces cannot drift apart.</para>
    /// </summary>
    public bool AllowsDevTools(ClaimsPrincipal user) =>
        Includes(user) && ApiTokenClaims.AllowsDev(user);
}
