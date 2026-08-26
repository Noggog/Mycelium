using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Tests;

/// <summary>
/// A fixed token with no store behind it. The real <see cref="PlexTokenSource"/> reads Mongo and falls
/// back to the environment; tests that only need <see cref="PlexApi"/> to send *a* token shouldn't have
/// to stand either up.
/// </summary>
public sealed class StaticPlexTokenSource : IPlexTokenSource
{
    private readonly string _token;

    public StaticPlexTokenSource(string token) => _token = token;

    public Task<string> Current() => Task.FromResult(_token);

    public Task<PlexTokenResolution> Resolve() =>
        Task.FromResult(new PlexTokenResolution(_token, PlexTokenOrigin.Environment, null));

    public void Invalidate()
    {
    }
}
