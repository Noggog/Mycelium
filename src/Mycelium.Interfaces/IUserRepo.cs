namespace Mycelium.Interfaces;

/// <summary>
/// Persists app users keyed by OIDC subject. Populated on login (no self-registration) — the
/// IdP is the source of truth for identity; this store exists so taste/seeds have something stable
/// to hang off and so we can show "first seen / last login".
/// </summary>
public interface IUserRepo
{
    /// <summary>
    /// Upserts the user on login: profile fields and last-login are refreshed every time;
    /// first-seen is set once on initial insert.
    /// </summary>
    Task UpsertOnLogin(AppUser user);

    Task<AppUser?> Get(string subject);

    /// <summary>
    /// Every known user, by display name then username. "Known" means "has logged in at least
    /// once" — this store has no other way to learn of anyone (the IdP is the source of truth for
    /// identity and we never enumerate it), so a user who has never signed in cannot be listed or
    /// given a quality tier until they do.
    /// </summary>
    Task<AppUser[]> GetAll();

    /// <summary>
    /// Sets a user's download quality ceiling. Null clears it, returning them to the deployment
    /// default. Only touches users that already exist (IsUpsert=false), so a typo'd subject can't
    /// conjure a phantom account with an entitlement attached.
    /// </summary>
    Task SetMaxQuality(string subject, AudioQuality? quality);

    /// <summary>
    /// Records whether this user rates in half stars, which decides where the generated smart
    /// playlists put the "never play again" floor. Null clears it back to unset (the catalog default).
    /// Only touches users that already exist, for the same reason <see cref="SetMaxQuality"/> does.
    /// </summary>
    Task SetHalfStarRatings(string subject, bool? halfStars);

    /// <summary>
    /// One-time migration: gives <paramref name="quality"/> to every user who has no tier set yet,
    /// and returns how many were updated.
    ///
    /// <para>Needed because the deployment default is deliberately the <em>lower</em> tier — a new
    /// account shouldn't quietly cost 3x the disk before anyone notices. Applied bare, that flag
    /// would also demote everyone already using the app: their queued downloads would drop to 320
    /// with no announcement, and the upgrade feed would show nothing at all (it surfaces rows only
    /// where some user out-ranks the copy on disk, which no one would). Backfilling existing users
    /// to lossless makes the default apply only to accounts created afterwards.</para>
    /// </summary>
    Task<int> BackfillMissingQuality(AudioQuality quality);
}
