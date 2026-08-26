using System.Collections.Concurrent;
using System.Net;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// What the dev panel shows about the server's Plex credential.
///
/// <para><see cref="Valid"/> is null until something has actually asked Plex — an unchecked token is
/// not the same claim as a working one, and saying "connected" on the strength of a string being
/// present is how a dead token goes unnoticed for a month.</para>
/// </summary>
public record PlexServerTokenStatus(
    bool Configured,
    bool? Valid,
    string? Username,
    string? Email,
    DateTimeOffset? LinkedAt,
    DateTimeOffset? CheckedAt,
    string? Problem);

/// <summary>
/// The server's own Plex credential: what it is, whether it still works, and how to replace it
/// without a redeploy.
///
/// <para><b>Why this exists.</b> The token used to be an environment variable read once at startup, so
/// replacing an expired one meant editing <c>.env</c> and redeploying — and nothing noticed it had
/// expired until someone pressed a button and got a 500. The token now resolves through
/// <see cref="IPlexTokenSource"/> per request, which makes it replaceable at runtime; this service is
/// what replaces it, and what checks it.</para>
///
/// <para><b>The link flow</b> is the same plex.tv PIN dance <see cref="PlexLinkService"/> runs for
/// individual users, pointed at the server credential instead. As there, only the
/// <em>server-scoped</em> token is kept and the account-wide one that claimed the PIN is discarded —
/// so what lands in Mongo can reach this one library and nothing else in the account.</para>
/// </summary>
public class PlexServerTokenService
{
    private readonly IPlexServerTokenRepo _repo;
    private readonly IPlexTokenSource _tokens;
    private readonly IPlexAccountApi _accounts;
    private readonly IPlexApi _plexApi;
    private readonly ILogger<PlexServerTokenService> _logger;

    // Keyed by the dev user who started it, exactly as the per-user flow is: two operators linking at
    // once would otherwise claim each other's PIN. Lost on restart, which costs one click.
    private readonly ConcurrentDictionary<string, PlexPin> _pending = new();

    private volatile PlexServerTokenStatus? _last;

    public PlexServerTokenService(
        IPlexServerTokenRepo repo,
        IPlexTokenSource tokens,
        IPlexAccountApi accounts,
        IPlexApi plexApi,
        ILogger<PlexServerTokenService> logger)
    {
        _repo = repo;
        _tokens = tokens;
        _accounts = accounts;
        _plexApi = plexApi;
        _logger = logger;
    }

    /// <summary>
    /// The last verdict, or an unchecked description of what's configured if nothing has verified yet.
    /// Cheap — deliberately doesn't touch Plex, so the dev panel can poll it.
    /// </summary>
    public async Task<PlexServerTokenStatus> Status()
    {
        if (_last is { } known)
        {
            return known;
        }

        var resolved = await _tokens.Resolve();
        return new PlexServerTokenStatus(
            Configured: resolved.Token is not null,
            Valid: null,
            Username: resolved.Linked?.Username,
            Email: resolved.Linked?.Email,
            LinkedAt: resolved.Linked?.LinkedAt,
            CheckedAt: null,
            Problem: null);
    }

    /// <summary>
    /// Asks Plex whether the configured token still works, and pings plex.tv to push back its expiry.
    /// Run at startup and once a day off the back of the catalog sync, so a credential that lapses is
    /// noticed by the app rather than by whoever next presses a button.
    ///
    /// <para>The verdict comes from the media server, not plex.tv: the server is what the app actually
    /// reads, and a token can be fine at plex.tv while the server refuses it. The ping is best effort —
    /// a plex.tv outage says nothing about a token the server just accepted.</para>
    /// </summary>
    public async Task<PlexServerTokenStatus> Verify()
    {
        var resolved = await _tokens.Resolve();
        if (resolved.Token is null)
        {
            return Record(new PlexServerTokenStatus(
                false, false, null, null, null, DateTimeOffset.UtcNow,
                "Plex isn't linked yet. Use \u201cLink with Plex\u201d above to connect it."));
        }

        bool accepted;
        try
        {
            accepted = await _plexApi.AcceptsToken(resolved.Token);
        }
        catch (Exception ex)
        {
            // The server is unreachable or faulting. That is not a verdict on the token, so it isn't
            // recorded as one — the status says "can't tell" rather than blaming the credential.
            _logger.LogWarning(ex, "Couldn't verify the Plex token: the server didn't answer.");
            return Record(new PlexServerTokenStatus(
                true, null, resolved.Linked?.Username, resolved.Linked?.Email,
                resolved.Linked?.LinkedAt, DateTimeOffset.UtcNow,
                $"Couldn't reach the Plex server to check: {ex.Message}"));
        }

        if (accepted)
        {
            await KeepWarm(resolved.Token);
        }
        else
        {
            _logger.LogError(
                "The Plex token is no longer valid. Re-link it in the dev panel; until then the "
                + "catalog serves whatever the last successful sync stored.");
        }

        return Record(new PlexServerTokenStatus(
            Configured: true,
            Valid: accepted,
            Username: resolved.Linked?.Username,
            Email: resolved.Linked?.Email,
            LinkedAt: resolved.Linked?.LinkedAt,
            CheckedAt: DateTimeOffset.UtcNow,
            Problem: accepted
                ? null
                : "Plex rejected this token. It has expired or been revoked — re-link to mint a new one."));
    }

    /// <summary>
    /// Pings plex.tv so the token's expiry is pushed back. Never throws: this is maintenance, and a
    /// plex.tv blip must not turn a healthy check into a failure.
    /// </summary>
    private async Task KeepWarm(string token)
    {
        try
        {
            await _accounts.Ping(token);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't ping plex.tv to refresh the token; it remains valid.");
        }
    }

    /// <summary>Begins a link and returns the URL to send the operator to.</summary>
    public async Task<string> Start(string subject, string? forwardUrl)
    {
        var pin = await _accounts.CreatePin(forwardUrl);
        _pending[subject] = pin;
        return pin.AuthUrl;
    }

    /// <summary>
    /// Polled while the operator approves in their browser. On success the new token is stored, the
    /// resolver's cache dropped so the next Plex call uses it, and the result verified straight away —
    /// the panel then shows a checked verdict rather than an optimistic one.
    /// </summary>
    public async Task<(PlexLinkOutcome Outcome, PlexServerTokenStatus Status)> Complete(string subject)
    {
        if (!_pending.TryGetValue(subject, out var pin))
        {
            return (PlexLinkOutcome.Expired, await Status());
        }

        var accountToken = await _accounts.ClaimPin(pin.Id, pin.Code);
        if (accountToken is null)
        {
            // Still unapproved, or dead — the claim call can't distinguish. Keep polling.
            return (PlexLinkOutcome.Pending, await Status());
        }

        // Asked with the *account* token that just came back, not the app's own: the whole point of
        // this flow is that the stored one may be dead, and the machine id is what scopes the new
        // credential to this server.
        var machineId = await _plexApi.GetMachineIdentifier()
                        ?? throw new InvalidOperationException(
                            "Can't complete the link: the Plex server is unreachable.");

        var account = await _accounts.ResolveAccount(accountToken, machineId);
        _pending.TryRemove(subject, out _);

        if (account is null)
        {
            _logger.LogWarning(
                "A Plex account approved the server link but has no access to this server.");
            return (PlexLinkOutcome.NoServerAccess, await Status());
        }

        await Store(account.ServerToken, account.Username, account.Email);
        _logger.LogInformation(
            "The server's Plex token was re-linked from account {Username}.", account.Username);
        return (PlexLinkOutcome.Linked, await Verify());
    }

    /// <summary>
    /// Stores a token pasted by the operator instead of running the PIN flow — the escape hatch when
    /// the browser dance isn't available, and the way to install a token copied out of Plex Web.
    ///
    /// <para>Verified against the server before anything is written, so a truncated paste is refused
    /// rather than stored and left to fail every later call. plex.tv is asked for the account name too,
    /// but only as a label: a server access token isn't a plex.tv account token and won't resolve to
    /// one, and that must not stop a token the server plainly accepts from being installed.</para>
    /// </summary>
    public async Task<(PlexLinkOutcome Outcome, PlexServerTokenStatus Status)> LinkWithToken(string? token)
    {
        var pasted = token?.Trim();
        if (string.IsNullOrEmpty(pasted))
        {
            return (PlexLinkOutcome.InvalidToken, await Status());
        }

        if (!await _plexApi.AcceptsToken(pasted))
        {
            return (PlexLinkOutcome.InvalidToken, await Status());
        }

        await Store(pasted, await AttributeToken(pasted), null);
        _logger.LogInformation("The server's Plex token was replaced from a pasted value.");
        return (PlexLinkOutcome.Linked, await Verify());
    }

    /// <summary>
    /// Best-effort "who does this belong to", for the panel's benefit only. A server access token is
    /// not a plex.tv account token, so this legitimately comes back empty for a perfectly good paste.
    /// </summary>
    private async Task<string?> AttributeToken(string token)
    {
        try
        {
            var machineId = await _plexApi.GetMachineIdentifier();
            return machineId is null ? null : (await _accounts.ResolveAccount(token, machineId))?.Username;
        }
        catch (HttpRequestException ex) when (ex.StatusCode
                   is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't attribute the pasted Plex token to an account.");
            return null;
        }
    }

    /// <summary>Disconnects Plex, leaving the app with no credential until something links one.</summary>
    public async Task<PlexServerTokenStatus> Clear()
    {
        await _repo.Clear();
        _tokens.Invalidate();
        _last = null;
        _logger.LogInformation("The stored Plex token was cleared; Plex is now unlinked.");
        return await Verify();
    }

    private async Task Store(string token, string? username, string? email)
    {
        await _repo.Set(new PlexServerCredential(token, username, email, DateTimeOffset.UtcNow));
        // The write alone changes nothing: the resolver caches, and this is what makes the next Plex
        // call pick the new token up.
        _tokens.Invalidate();
        _last = null;
    }

    private PlexServerTokenStatus Record(PlexServerTokenStatus status)
    {
        _last = status;
        return status;
    }
}
