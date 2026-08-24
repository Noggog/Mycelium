using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>Whether this user has connected a Plex account, and which one.</summary>
public record PlexLinkStatus(bool Linked, string? Username, string? Email, DateTimeOffset? LinkedAt)
{
    public static readonly PlexLinkStatus Unlinked = new(false, null, null, null);
}

/// <summary>How a claim attempt went.</summary>
public enum PlexLinkOutcome
{
    /// <summary>Stored — the user's own Plex account is now connected.</summary>
    Linked,

    /// <summary>The user hasn't finished approving yet. The normal answer while the browser tab is open.</summary>
    Pending,

    /// <summary>No link is in flight, or the code timed out. The user starts again.</summary>
    Expired,

    /// <summary>They approved, but that Plex account can't see this server — nothing we made would reach them.</summary>
    NoServerAccess,

    /// <summary>plex.tv doesn't recognise the pasted token. Only reachable from the paste path.</summary>
    InvalidToken,
}

public record PlexLinkCompletion(PlexLinkOutcome Outcome, PlexLinkStatus Status);

/// <summary>
/// Connects an app user to their own Plex account, so playlists are created <em>for them</em> — in their
/// sidebar, filtered by their star ratings and their play history — rather than in the server owner's
/// account with everyone's rules pointed at the owner's taste.
///
/// <para><b>The flow.</b> plex.tv issues a short-lived PIN; the user approves it in a browser tab at
/// app.plex.tv; the app then claims it for a token. The pending PIN is held in memory keyed by user, so
/// the client can poll with no arguments and never has to carry the code around. A backend restart
/// mid-flow loses it, which costs the user one click to start again.</para>
///
/// <para>Only the <em>server-scoped</em> token is kept (see <see cref="IPlexAccountApi.ResolveAccount"/>);
/// the account-wide token that claimed the PIN is used once and discarded.</para>
/// </summary>
public class PlexLinkService
{
    private readonly IPlexLinkRepo _links;
    private readonly IPlexAccountApi _accounts;
    private readonly IPlexApi _plexApi;
    private readonly ILogger<PlexLinkService> _logger;

    private readonly ConcurrentDictionary<string, PlexPin> _pending = new();

    public PlexLinkService(
        IPlexLinkRepo links,
        IPlexAccountApi accounts,
        IPlexApi plexApi,
        ILogger<PlexLinkService> logger)
    {
        _links = links;
        _accounts = accounts;
        _plexApi = plexApi;
        _logger = logger;
    }

    public async Task<PlexLinkStatus> Status(string subject)
    {
        var link = await _links.Get(subject);
        return link is null
            ? PlexLinkStatus.Unlinked
            : new PlexLinkStatus(true, link.Username, link.Email, link.LinkedAt);
    }

    /// <summary>
    /// Begins a link and returns the URL to send the user to. Any half-finished attempt by the same user
    /// is replaced — the last button press is the one that counts.
    /// </summary>
    public async Task<string> Start(string subject, string? forwardUrl)
    {
        var pin = await _accounts.CreatePin(forwardUrl);
        _pending[subject] = pin;
        return pin.AuthUrl;
    }

    /// <summary>
    /// Polled by the client while the user is approving. Stores the link on success and clears the
    /// pending PIN either way, since a claimed or expired PIN can't be reused.
    /// </summary>
    public async Task<PlexLinkCompletion> Complete(string subject)
    {
        if (!_pending.TryGetValue(subject, out var pin))
        {
            return new PlexLinkCompletion(PlexLinkOutcome.Expired, await Status(subject));
        }

        var accountToken = await _accounts.ClaimPin(pin.Id, pin.Code);
        if (accountToken is null)
        {
            // Either still unapproved (keep polling) or dead. The claim call can't tell us which, so the
            // PIN stays pending until the user gives up or restarts — a dead PIN just never resolves.
            return new PlexLinkCompletion(PlexLinkOutcome.Pending, PlexLinkStatus.Unlinked);
        }

        var machineId = await _plexApi.GetMachineIdentifier()
                        ?? throw new InvalidOperationException(
                            "Can't verify the Plex link: the server is unreachable.");

        var account = await _accounts.ResolveAccount(accountToken, machineId);
        _pending.TryRemove(subject, out _);

        if (account is null)
        {
            _logger.LogWarning("A Plex account approved a link but has no access to this server.");
            return new PlexLinkCompletion(PlexLinkOutcome.NoServerAccess, PlexLinkStatus.Unlinked);
        }

        var link = new PlexLink(
            Subject: subject,
            AccountId: account.AccountId,
            Username: account.Username,
            Email: account.Email,
            ServerToken: account.ServerToken,
            LinkedAt: DateTimeOffset.UtcNow);

        await _links.Upsert(link);
        _logger.LogInformation("Linked Plex account {Username} to app user.", account.Username);

        return new PlexLinkCompletion(
            PlexLinkOutcome.Linked,
            new PlexLinkStatus(true, link.Username, link.Email, link.LinkedAt));
    }

    /// <summary>
    /// Links an account from a token the user pasted, rather than through the PIN flow. The PIN flow can
    /// only ever link whoever is already signed in at app.plex.tv in that browser, which makes one case
    /// impossible: acting as a Plex Home / managed user, who has no browser session of their own to
    /// approve with. Pasting their token is how you name a <em>specific</em> account.
    ///
    /// <para>Verified against plex.tv before anything is written, so a truncated or revoked paste is
    /// refused rather than stored and left to fail every later call. And as with the PIN flow, only the
    /// <em>server-scoped</em> token comes back from <see cref="IPlexAccountApi.ResolveAccount"/> and is
    /// kept — whatever was pasted is used once to ask who it belongs to, then discarded. Pasting an
    /// account-wide token therefore doesn't leave an account-wide credential in the database.</para>
    /// </summary>
    public async Task<PlexLinkCompletion> LinkWithToken(string subject, string? token)
    {
        var pasted = token?.Trim();
        if (string.IsNullOrEmpty(pasted))
        {
            return new PlexLinkCompletion(PlexLinkOutcome.InvalidToken, await Status(subject));
        }

        var machineId = await _plexApi.GetMachineIdentifier()
                        ?? throw new InvalidOperationException(
                            "Can't verify the Plex link: the server is unreachable.");

        PlexAccount? account;
        try
        {
            account = await _accounts.ResolveAccount(pasted, machineId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode
                   is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // plex.tv doesn't know this token — a partial paste or a revoked token, which is the user's
            // to correct. Anything else (a plex.tv outage, a network failure) still throws: that's not
            // the same answer and shouldn't be reported to them as a bad token.
            _logger.LogInformation("A pasted Plex token was rejected by plex.tv ({Status}).", ex.StatusCode);
            return new PlexLinkCompletion(PlexLinkOutcome.InvalidToken, await Status(subject));
        }

        if (account is null)
        {
            _logger.LogWarning("A pasted Plex token is valid but has no access to this server.");
            return new PlexLinkCompletion(PlexLinkOutcome.NoServerAccess, await Status(subject));
        }

        // A completed paste supersedes any half-finished PIN flow — the last thing the user did wins,
        // the same rule Start() applies.
        _pending.TryRemove(subject, out _);

        var link = new PlexLink(
            Subject: subject,
            AccountId: account.AccountId,
            Username: account.Username,
            Email: account.Email,
            ServerToken: account.ServerToken,
            LinkedAt: DateTimeOffset.UtcNow);

        await _links.Upsert(link);
        _logger.LogInformation(
            "Linked Plex account {Username} to app user from a pasted token.", account.Username);

        return new PlexLinkCompletion(
            PlexLinkOutcome.Linked,
            new PlexLinkStatus(true, link.Username, link.Email, link.LinkedAt));
    }

    /// <summary>
    /// Forgets the account and its token. Playlists already created stay where they are — they belong to
    /// the user's Plex account, not to this app.
    /// </summary>
    public async Task Unlink(string subject)
    {
        _pending.TryRemove(subject, out _);
        await _links.Delete(subject);
    }
}
