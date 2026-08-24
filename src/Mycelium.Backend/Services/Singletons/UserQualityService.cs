using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Answers "how good a copy is this user allowed to pull down?" — the stored per-user ceiling if
/// they have one, else the deployment default.
///
/// <para>Exists as its own service because three unrelated callers need the same answer and must not
/// disagree about it: the reconcile that stamps a purchase row's target, the discovery feed that
/// decides whether to offer an upgrade, and the missing-album sync that needs the highest tier
/// <em>anyone</em> is entitled to. If the feed resolved a default differently from the reconcile, a
/// user would be offered an upgrade that then downloaded at the quality they already had.</para>
/// </summary>
public class UserQualityService
{
    private readonly IUserRepo _users;
    private readonly AudioQuality _default;

    public UserQualityService(IUserRepo users, AudioQuality defaultQuality)
    {
        _users = users;
        _default = defaultQuality;
    }

    /// <summary>The tier applied to a user who has none set — the deployment default.</summary>
    public AudioQuality Default => _default;

    /// <summary>
    /// The tier this user's requests download at. An unknown subject gets the default: they can't
    /// have likes without a user doc, but resolving to "no quality at all" would make a stray id
    /// silently un-downloadable rather than merely un-privileged.
    /// </summary>
    public async Task<AudioQuality> For(string userId) =>
        (await _users.Get(userId))?.MaxQuality ?? _default;

    /// <summary>
    /// The best tier anyone is entitled to. This is the ceiling the missing-album sync diffs
    /// against: it walks Deezer once for the whole library, so it can't ask per-user, and diffing
    /// against the ceiling means a row exists for anything <em>somebody</em> could want better. The
    /// per-user feed then filters that superset down (see DiscoveryEngine).
    /// </summary>
    public async Task<AudioQuality> Ceiling()
    {
        var best = _default;
        foreach (var user in await _users.GetAll())
        {
            if (user.MaxQuality is { } quality && quality > best)
            {
                best = quality;
            }
        }
        return best;
    }

    /// <summary>
    /// The highest tier among the users who asked for something — what a shared purchase row
    /// downloads at. The list is global, so an album two people want is fetched once, at the better
    /// of their two entitlements: a lossy user riding along on a lossless user's request costs
    /// nothing extra, while the reverse would quietly cheat the lossless user.
    /// </summary>
    public async Task<AudioQuality> BestOf(IEnumerable<string> userIds)
    {
        var best = (AudioQuality?)null;
        foreach (var id in userIds.Distinct(StringComparer.Ordinal))
        {
            var quality = await For(id);
            if (best is null || quality > best)
            {
                best = quality;
            }
        }
        return best ?? _default;
    }
}
