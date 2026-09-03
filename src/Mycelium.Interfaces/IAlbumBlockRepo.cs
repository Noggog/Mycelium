namespace Mycelium.Interfaces;

/// <summary>
/// One globally blocked album: a release nobody should be offered — a bad Deezer entry, a reissue
/// that duplicates something owned, a record the library has decided against carrying. Distinct from
/// an <see cref="AlbumMatchOverride"/> (which asserts "we already have this") and from a per-user
/// "meh" (<see cref="DiscoveryStatus.Disliked"/>, which only hides it from the one user who said so).
/// <see cref="BlockedBy"/> is the <b>username</b> of whoever placed it, kept for audit — anyone may
/// lift it. A username rather than the OIDC subject on purpose: this field is never matched on, only
/// read by a person or exported, and an identity-provider id means nothing outside the provider that
/// issued it. Rows written before that was settled hold a subject and are migrated on startup by
/// <see cref="IAlbumBlockRepo.BackfillAttribution"/>.
/// </summary>
public record AlbumBlock(
    string Artist,
    string Album,
    string? BlockedBy = null,
    AlbumBlockScope Scope = AlbumBlockScope.Release,
    DateTimeOffset? RetryAfter = null)
{
    /// <summary>Whether this verdict is in force at <paramref name="now"/>.</summary>
    public bool AppliesAt(DateTimeOffset now) => RetryAfter is null || RetryAfter > now;
}

/// <summary>
/// What a block actually forbids. The distinction exists because declining to <em>replace</em> a
/// record you own is not the same as deciding the library shouldn't carry it, and conflating them
/// would make "no thanks, this one's fine as it is" hide an album from every surface in the app.
/// </summary>
public enum AlbumBlockScope
{
    /// <summary>
    /// Don't offer this release at all — a bad Deezer entry, a duplicate reissue, a record the
    /// library has decided against. Applies to every album surface.
    /// </summary>
    Release,

    /// <summary>
    /// Keep the copy we have: don't offer to replace it with a better one. The album stays visible
    /// and owned everywhere else; only the upgrade feed passes over it.
    ///
    /// <para>Two things write this, told apart by <see cref="AlbumBlock.RetryAfter"/>: a user saying
    /// "not this one" (no stamp — it stands until lifted), and the downloader discovering Deezer has
    /// nothing better to offer (a stamp, since a catalogue can gain a lossless master later and
    /// foreclosing on that permanently would be wrong).</para>
    /// </summary>
    Upgrade,
}

/// <summary>
/// Durable, global store of blocked albums. Consulted when serving every album surface (the
/// missing-album feed, a liked artist's inline albums, the Artists-page discography) so a blocked
/// release stops being offered to <em>everyone</em>, not just the user who blocked it.
///
/// Blocks are held here rather than applied to <see cref="IMissingAlbumRepo"/> so the nightly Deezer
/// re-diff can't resurrect them, and so the album's row (and the Deezer id the downloader needs)
/// survives for anyone who had already queued it to buy.
/// </summary>
public interface IAlbumBlockRepo
{
    /// <summary>Every block on record.</summary>
    Task<AlbumBlock[]> GetAll();

    /// <summary>Records a block. Idempotent for the same (artist, album, scope).</summary>
    Task Add(AlbumBlock block);

    /// <summary>Lifts a block, returning the album to everyone's feeds.</summary>
    Task Remove(string artist, string album, AlbumBlockScope scope = AlbumBlockScope.Release);

    /// <summary>
    /// One-time migration: rewrites <see cref="AlbumBlock.BlockedBy"/> from the OIDC subject the field
    /// used to hold to the username it holds now, given a subject → username map. Returns how many
    /// rows were rewritten.
    ///
    /// <para>Idempotent, and safe to run against a mixed collection: only values that match a known
    /// subject are touched, so a row already holding a username is left exactly as it is. A subject
    /// belonging to a user who has since been deleted can't be resolved and stays as it was — the
    /// export writes those as an opaque placeholder rather than publishing an identity-provider id.
    /// </para>
    /// </summary>
    Task<int> BackfillAttribution(IReadOnlyDictionary<string, string> usernamesBySubject);
}
