using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// <see cref="IPlexTokenSource"/> over the Mongo-stored credential, falling back to the environment.
///
/// <para>Cached behind a gate rather than read per request: every Plex call goes through here, and a
/// Mongo round trip on each one would be a real cost for a value that changes about once a year. The
/// cache is dropped by <see cref="Invalidate"/> when the token is re-linked, which is the only thing
/// that can change the answer.</para>
/// </summary>
public class PlexTokenSource : IPlexTokenSource
{
    private readonly IPlexServerTokenRepo _repo;
    private readonly PlexClientInfo _environment;
    private readonly ILogger<PlexTokenSource> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlexTokenResolution? _cached;

    public PlexTokenSource(
        IPlexServerTokenRepo repo, PlexClientInfo environment, ILogger<PlexTokenSource> logger)
    {
        _repo = repo;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string> Current()
    {
        var resolved = await Resolve();
        return resolved.Token ?? throw new PlexUnauthorizedException(
            "No Plex token is configured: nothing is linked in the dev panel and PLEX_TOKEN is unset.");
    }

    public async Task<PlexTokenResolution> Resolve()
    {
        // Double-checked around the gate: the common case is a hit and shouldn't queue behind it, and
        // the miss case must not have every concurrent caller hit Mongo at once on a cold start.
        if (_cached is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync();
        try
        {
            if (_cached is { } raced)
            {
                return raced;
            }

            var linked = await _repo.Get();
            _cached = linked is not null
                ? new PlexTokenResolution(linked.Token, PlexTokenOrigin.Linked, linked)
                : string.IsNullOrWhiteSpace(_environment.Token)
                    ? new PlexTokenResolution(null, PlexTokenOrigin.None, null)
                    : new PlexTokenResolution(_environment.Token, PlexTokenOrigin.Environment, null);

            _logger.LogInformation("Plex server token resolved from {Origin}.", _cached.Origin);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;
}
