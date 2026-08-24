namespace Mycelium.Interfaces;

public record AlbumKey(string AlbumName);
public record Album(AlbumKey Key, string? AlbumArt);

/// <summary>The owned albums for one artist, as pulled from the Plex library catalog.</summary>
public record ArtistAlbums(ArtistKey Artist, IReadOnlyList<OwnedAlbum> Albums);

/// <summary>
/// One album the library holds: the title the missing-album diff matches against, plus the Plex
/// rating key it lives under so the UI can deep link straight to it (see PlexDeepLink). Captured on
/// the same pull as the titles, so a library rebuild that shifts keys self-heals on the next sync.
///
/// <para><paramref name="Quality"/> is how good the copy on disk is — the majority tier of its
/// tracks (see <see cref="AudioQualityTier.Majority"/>). Null means we haven't determined it: Plex
/// reported no codecs, or the album was synced before quality tracking existed. Null deliberately
/// never reads as "needs upgrading" — see <see cref="AudioQuality"/>.</para>
/// </summary>
public record OwnedAlbum(string Title, int PlexRatingKey, AudioQuality? Quality = null);

/// <summary>
/// An album that exists on Deezer for an artist the user owns, but isn't in the library — a
/// candidate to acquire so an owned band stays current. A global fact about the library (not
/// per-user); the per-user verdict on it lives in <see cref="AlbumRating"/>.
///
/// <see cref="Artist"/> is the band whose discography surfaced it (where it shows in the feed).
/// <see cref="AlbumArtist"/> is the album's real credited act per Deezer — for a collaboration
/// surfaced via one member (e.g. a duo record under "Milo") these differ, and the library files
/// the album under the album-artist, so that is the key to match ownership against.
///
/// <see cref="Year"/> is the Deezer release year, surfaced beside the title so a recommendation can
/// be placed in time; null when Deezer gave no date (or for rows written before year tracking).
///
/// <see cref="OwnedQuality"/> is how good the copy already on disk is, and null when the library
/// doesn't have the album at all. That is what separates a gap from an upgrade: the sync emits a row
/// whenever the library is short of the best tier <em>any</em> user is entitled to, and the per-user
/// feed then decides which of those to actually offer (a user capped at 320 is not shown an album
/// they already have at 320).
///
/// <see cref="RecordType"/> is Deezer's classification of the release. Every type the sync lists is
/// persisted, including the ones the Discover feed doesn't push (see
/// <see cref="AlbumRecordType.IsFeedEligible"/>): the row is what carries
/// <see cref="DeezerAlbumId"/> through reconcile to the downloader, so a single a user queues from the
/// discography drill-down has to have one or it would sit on the to-buy list forever un-downloadable.
/// The feed filters on this rather than the sync, so "browsable" and "pushed at you" stay separable.
/// Null for rows written before record-type tracking — those are pre-existing LPs and EPs, so the feed
/// treats an absent type as eligible.
///
/// Each pressing Deezer lists is its own row here — the deluxe edition, the remaster and the plain LP
/// are three <see cref="MissingAlbum"/>s with three <see cref="DeezerAlbumId"/>s, and all three are
/// offered. Owning one pressing does not mark the others owned: they are separate releases, declined
/// individually with a dismiss or a block.
/// </summary>
public record MissingAlbum(
    ArtistKey Artist,
    AlbumKey Album,
    string? AlbumArt,
    long DeezerAlbumId,
    ArtistKey? AlbumArtist = null,
    int? Year = null,
    string? RecordType = null,
    AudioQuality? OwnedQuality = null)
{
    /// <summary>
    /// Whether this row is an <em>upgrade</em> — the library has the album, just not well enough —
    /// rather than a genuine gap. The two are the same kind of row (both carry the Deezer id the
    /// downloader needs) but they are entirely different propositions to a user: one grows the
    /// library, the other replaces a record they already own. See <see cref="FeedKind.UpgradeAlbum"/>.
    /// </summary>
    public bool IsUpgrade => OwnedQuality is not null;

    /// <summary>The artist the library files this album under — <see cref="AlbumArtist"/> when known,
    /// else <see cref="Artist"/> (non-collaboration albums are filed under the listing artist).</summary>
    public ArtistKey MatchArtist => AlbumArtist ?? Artist;
}

/// <summary>
/// Deezer's <c>record_type</c> values and which of them the Discover feed pushes. The missing-album
/// sync persists every type it lists so each one carries a Deezer id the downloader can use; this is
/// the gate that decides which of those are actually offered unprompted, applied where a feed is built
/// rather than where the rows are written. Singles and compilations are browsable in an artist's
/// discography (badged with their type) but never pushed — the feed would otherwise fill with radio
/// edits and greatest-hits repackages.
/// </summary>
public static class AlbumRecordType
{
    private static readonly HashSet<string> FeedEligible =
        new(StringComparer.OrdinalIgnoreCase) { "album", "ep" };

    /// <summary>
    /// Whether a release of this type belongs in the Discover feed. A null/absent type reads as
    /// eligible: rows written before record-type tracking predate singles being listed at all, so they
    /// can only be the LPs and EPs the old filter let through.
    /// </summary>
    public static bool IsFeedEligible(string? recordType) =>
        recordType is null || FeedEligible.Contains(recordType);
}

/// <summary>
/// One album already in the library, offered as a merge target for a release the diff calls missing.
/// Carries the artist because the copy we own can be filed under a different act than the one whose
/// discography surfaced it (e.g. Plex's "Matthewdavid's Mindflight" vs. Deezer's "Matthewdavid").
///
/// <see cref="PlexUrl"/> opens the suggestion in Plex so a near-miss title can be checked against the
/// real thing before merging; null when the album's rating key isn't captured yet (synced before keys
/// were stored) or Plex couldn't be reached to identify the server.
/// </summary>
public record LibraryAlbumOption(string Artist, string Album, string? PlexUrl = null);

/// <summary>
/// Canonical (artist, album) identity used to match a user's album verdict against a missing album.
/// One definition shared by the rating store and the feed filter so they never drift.
/// </summary>
public static class AlbumRatingKey
{
    public static string For(string artist, string album) =>
        $"{artist.ToLowerInvariant()} {album.ToLowerInvariant()}";
}
