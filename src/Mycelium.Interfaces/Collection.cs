namespace Mycelium.Interfaces;

/// <summary>
/// A release that the artist-rooted walk can never reach — a various-artists compilation, a
/// soundtrack, a cast recording — surfaced for the Browse "Collections" view.
///
/// <para><b>Why it needs its own shape.</b> Everything else in the app is found <em>through</em> an
/// artist: the catalog lists owned acts, the similarity graph grows from them, and the missing-album
/// diff walks each owned artist's Deezer discography. A compilation is credited to an umbrella
/// (<see cref="UmbrellaArtist"/>) whose discography is empty — Deezer's own "Various Artists" lists no
/// albums at all — so no walk starting from an artist will ever produce one. The only way in is to
/// search for, or paste, the record itself, which is what this row is the result of.</para>
///
/// <para><see cref="Umbrella"/> is what decides whether a like is stamped onto the <em>album</em> in
/// Plex. A record credited to a real act carries its verdict on the artist, as everything else does;
/// one credited to an umbrella has no artist that could hold it, so the album does
/// (<see cref="IAlbumTagger"/>). Rows that aren't umbrella-credited still appear — a search is allowed
/// to answer honestly — they're just ordinary albums, acquired the ordinary way.</para>
///
/// <para><see cref="Owned"/> is library presence; <see cref="Verdict"/> is this user's thumb (null
/// when they haven't decided). Both can be true at once: an owned compilation is still rateable, which
/// is the point of the view — a like on something already on the shelf is what puts it in "My
/// Library".</para>
/// </summary>
public record CollectionItem(
    long DeezerAlbumId,
    string Title,
    ArtistKey Artist,
    string? CoverUrl,
    string? Link,
    bool Umbrella,
    bool Owned,
    DiscoveryStatus? Verdict,
    int? Year = null,
    int TrackCount = 0,
    string? RecordType = null,
    string? PlexUrl = null);

/// <summary>
/// The seam for stamping a user's taste verdict onto an <em>album</em> in the library backend (Plex)
/// as a mood tag — the album-level twin of <see cref="IArtistTagger"/>.
///
/// <para>It exists for the one case the artist tag can't serve: a collection credited to an umbrella
/// (<see cref="UmbrellaArtist"/>). Tagging "Various Artists" as liked would say the user likes every
/// compilation in the library, so the verdict goes on the record instead, where a smart playlist picks
/// it up through Plex's "Album Mood" field.</para>
///
/// <para>Best-effort and additive, exactly as the artist tagger is: it merges with the album's existing
/// moods and never throws, so a tagging failure can't break the rating it accompanies.</para>
/// </summary>
public interface IAlbumTagger
{
    /// <summary>
    /// Reconciles the managed moods on one album — the record <paramref name="albumTitle"/> filed
    /// under <paramref name="albumArtist"/> — ensuring <paramref name="add"/> is present (when
    /// non-null) and every tag in <paramref name="remove"/> is absent, leaving all other moods alone.
    ///
    /// <para>A no-op when the library doesn't hold the album yet: there is nothing to tag, and the
    /// verdict is re-stamped once it arrives (see the album tag backfill). Same add/remove contract as
    /// <see cref="IArtistTagger.SetTags"/>.</para>
    /// </summary>
    Task SetTags(string albumArtist, string albumTitle, string? add, IReadOnlyCollection<string> remove);
}
