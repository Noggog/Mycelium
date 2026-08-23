namespace Mycelium.Interfaces;

public record Recommendation(ArtistKey ArtistKey, ArtistKey[] SourceArtists);

public record ArtistKey(string ArtistName);

/// <summary>
/// Names that stand in for "no single act" rather than naming one — the bucket a library files
/// compilations and soundtracks under. They're not something a user can have taste about, so they
/// must never be offered to rate or grown from, however they got into the graph or the library.
/// Deliberately a short, exact list: anything fuzzier would swallow real acts (there is a band
/// called "Various", and "VA" is a real artist name).
/// </summary>
public static class PlaceholderArtist
{
    /// <summary>The canonical spelling, used when we have to *write* the placeholder — e.g. filing a
    /// hand-added compilation under the same act the library will.</summary>
    public const string VariousArtists = "Various Artists";

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        VariousArtists,
        "Various Artist",
        "Unknown Artist",
    };

    public static bool Is(string? artistName) =>
        artistName != null && Names.Contains(artistName.Trim());
}

public record ArtistMetadata(
    ArtistKey ArtistKey,
    string? ArtistImageUrl,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<int>? PlexRatingKeys = null);

/// <summary>
/// The Deezer artist a library name resolves to: its id, Deezer's own spelling, popularity, page
/// link and photo. Comparing <see cref="Name"/> to the library name (and eyeballing <see cref="Fans"/>)
/// is how a misassociation is spotted — e.g. library "ALEX" resolving to Deezer's "Alex Warren".
/// </summary>
public record DeezerIdentity(long Id, string? Name, int? Fans, string? Link, string? ImageUrl);

/// <summary>
/// The MusicBrainz artist a library name resolves to: its MBID (the stable id the ListenBrainz
/// similarity endpoint is keyed by), MusicBrainz's own spelling, and the disambiguation comment
/// that tells two same-named acts apart. The counterpart to <see cref="DeezerIdentity"/>.
/// </summary>
public record MusicBrainzIdentity(string Mbid, string? Name, string? Disambiguation = null);

public record CatalogArtist(
    ArtistKey ArtistKey,
    string? ArtistImageUrl,
    DateTimeOffset LastSeenAt,
    DeezerIdentity? Deezer = null,
    bool DeezerOverride = false,
    IReadOnlyList<string>? Genres = null,
    MusicBrainzIdentity? MusicBrainz = null,
    bool MusicBrainzOverride = false);

public record ArtistPackage(ArtistMetadata Metadata, Album[] Albums);

// ---- Cross-source identity ("Sources" tab on the Artists page) ----

/// <summary>
/// One artist's resolved identity on a single external source, for the Artists-page "Sources" tab:
/// the id, a link out to that source's artist page, and whether it's a sticky user override. A
/// source with no resolved id yet still appears (Id null) so it can be corrected. Non-correctable
/// sources (e.g. ListenBrainz, whose identity is just the MusicBrainz MBID) have no pin/clear.
/// <paramref name="Unlinked"/> marks a sticky "detached" decision (Id null, but deliberately so):
/// the artist has no match on this source, so it must never auto-resolve by name again.
/// </summary>
public record SourceIdentity(
    string Source,
    string? Id,
    string? Name,
    string? Detail,
    string? Link,
    string? ImageUrl,
    bool IsOverride,
    bool Correctable,
    bool Unlinked = false);

/// <summary>
/// One candidate in a source's "Correct association" search picker. <paramref name="Popularity"/> is
/// the source's own follower/listener count when it has one (Deezer fans), left null otherwise — it
/// is what lets a caller collapse several same-named candidates onto the canonical act, the same
/// tie-break the resolvers use when they pick a match by name.
/// </summary>
public record SourceCandidate(string Id, string? Name, string? Detail, string? Link, string? ImageUrl,
    int? Popularity = null);

/// <summary>The cross-source identity view of one artist, one entry per surfaced source.</summary>
public record ArtistSources(ArtistKey Artist, IReadOnlyList<SourceIdentity> Sources);

/// <summary>A deep link to an artist's page on a library source (e.g. "Open in Plex").</summary>
public record LibraryLink(string Label, string Url);

/// <summary>
/// An artist's presence on one library source (Plex, eventually Navidrome), for the Artists-page
/// "Library" tab: whether the artist is in that library and deep links to open it there (one per
/// matched item — a name can map to several Plex rating keys).
/// </summary>
public record LibrarySource(string Source, string Label, bool Present, IReadOnlyList<LibraryLink> Links);

/// <summary>The library-presence view of one artist, one entry per registered library source.</summary>
public record ArtistLibraries(ArtistKey Artist, IReadOnlyList<LibrarySource> Sources);

/// <summary>
/// The editable descriptor tags a library artist carries in Plex — genres, styles and moods — for the
/// Browse page's "Tags" tab. <see cref="Present"/> is false when the artist isn't in the library (no
/// Plex item to tag). <see cref="Moods"/> excludes the app's own "&lt;user&gt;_liked"/"_disliked"
/// verdict tags (see <see cref="ArtistTag.IsManaged"/>): those are rating state, not descriptors, and
/// must never be shown or edited here — the thumbs own them.
/// </summary>
public record ArtistTags(
    ArtistKey Artist,
    bool Present,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    IReadOnlyList<string> Moods);

/// <summary>
/// A compact summary of the user's per-song Plex ratings for one artist, on a 0–5 star scale, for the
/// discovery readout. Plex only has songs for artists already in the library, so <see cref="Present"/>
/// is false when the artist has no Plex rating keys. <see cref="RatedCount"/> is 0 when the artist is in
/// Plex but no song is rated (or Plex was unreachable); <see cref="Highest"/>/<see cref="Lowest"/>/
/// <see cref="Average"/> are null in both empty cases. <see cref="TrackCount"/> is the total songs seen.
/// </summary>
public record ArtistRatingStats(
    ArtistKey Artist,
    bool Present,
    double? Highest,
    double? Lowest,
    double? Average,
    int RatedCount,
    int TrackCount);