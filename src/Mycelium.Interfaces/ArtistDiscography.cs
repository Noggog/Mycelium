using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>
/// How a Deezer album was tied to a MusicBrainz release group. Strongest first: when two methods find
/// the same Deezer album, the earlier one is kept.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EditionMatchMethod
{
    /// <summary>A release in the group links to the Deezer album (a MusicBrainz url-rel).</summary>
    MbLink,

    /// <summary>Deezer answers a release's barcode with the album (<c>/album/upc:{barcode}</c>).</summary>
    Upc,

    /// <summary>
    /// The record-level title equals the group's, or one of its releases', within the artist. A title
    /// shared by several groups is settled by type, then year, or left unmatched.
    /// </summary>
    Title,

    /// <summary>A near title: one title's words all appear in the other, or nearly all of them are shared.</summary>
    TitleFuzzy,

    /// <summary>
    /// A title match on an album only Deezer's album search turned up, which the artist's own Deezer
    /// listing leaves out.
    /// </summary>
    Search,
}

/// <summary>How far an edition can be trusted. Only a high-confidence edition is ever picked unasked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EditionConfidence
{
    /// <summary>A MusicBrainz link or a barcode, with titles that agree: an identifier ties the two.</summary>
    High,

    /// <summary>Titles alone, or an identifier whose titles disagree. Links the album, but is listed for review.</summary>
    Low,
}

/// <summary>One Deezer album that is an edition of a release group.</summary>
/// <param name="AlbumId">The id Deezer answers to now. An old id MusicBrainz links to is followed to it.</param>
/// <param name="Upc">The barcode, when it is known (a barcode match, or a Deezer album lookup).</param>
/// <param name="Available">Whether it can be streamed in this region. Null when Deezer didn't say.</param>
/// <param name="TitleDisagrees">
/// A link or barcode placed it, but its title shares no word with any title of the release group:
/// MusicBrainz links the Otherness EP to the box set that holds it, and a barcode can come back as
/// another record. Kept, but at low confidence, so a person confirms it.
/// </param>
public record DeezerEdition(
    long AlbumId,
    string? Title,
    string? RecordType,
    string? ReleaseDate,
    int Tracks,
    string? Upc,
    bool? Available,
    EditionMatchMethod Method,
    bool TitleDisagrees = false)
{
    public EditionConfidence Confidence =>
        Method is EditionMatchMethod.MbLink or EditionMatchMethod.Upc && !TitleDisagrees
            ? EditionConfidence.High
            : EditionConfidence.Low;
}

/// <summary>One album as MusicBrainz knows it, with the Deezer albums that are editions of it.</summary>
/// <param name="Mbid">The release group's MBID.</param>
/// <param name="PrimaryType">"Album", "EP", "Single", "Broadcast", "Other", or null.</param>
/// <param name="SecondaryTypes">"Live", "Compilation", "Remix" and so on. Empty for a plain studio record.</param>
/// <param name="FirstReleaseDate">"2024-04-12", "2024-04", "2024", or null.</param>
/// <param name="Rejected">Deezer album ids a person said are not this album. Never matched to it again.</param>
/// <param name="ReleaseTitles">
/// Titles its releases carry that differ from the group's own: a group is named after one edition, and a
/// library may hold another ("Firewatch Original Soundtrack" in the group "Firewatch Original Score").
/// </param>
public record DiscographyReleaseGroup(
    string Mbid,
    string? Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? FirstReleaseDate,
    IReadOnlyList<DeezerEdition> Editions,
    IReadOnlyList<long> Rejected,
    IReadOnlyList<string>? ReleaseTitles = null)
{
    /// <summary>
    /// An official album or EP with no secondary type: what an artist page shows open, and what the
    /// match rates are judged on. Iron Maiden has 338 release groups, most of them live records and
    /// compilations Deezer doesn't carry.
    /// </summary>
    public bool IsCore =>
        PrimaryType is "Album" or "EP" && SecondaryTypes.Count == 0;
}

/// <summary>How an owned album was tied to a release group.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OwnedAlbumMatchMethod
{
    /// <summary>The record-level title equals the group's, one of its releases', or one of its Deezer editions'.</summary>
    Title,

    /// <summary>A near title, by the same rule Deezer albums are held to. Listed for review.</summary>
    TitleFuzzy,

    /// <summary>
    /// Nothing matched now, but the album already had this release group from the older one-search-per-album
    /// backfill, and the group is on this artist's discography. Kept rather than thrown away.
    /// </summary>
    Earlier,

    /// <summary>
    /// A person decided it on the reconciliation page (<see cref="AlbumIdentity.Manual"/>). With no release
    /// group, they said the album isn't on MusicBrainz. No rebuild overrides it.
    /// </summary>
    Manual,
}

/// <summary>One album the library owns, and the release group it is, if any.</summary>
/// <param name="LibraryArtist">The library artist it is filed under (<see cref="ArtistResolution.Id"/>).</param>
/// <param name="ReleaseGroup">The release group MBID. Null when nothing on the discography fits.</param>
/// <param name="Candidates">
/// When the title fits several release groups and nothing settles it: their MBIDs, likeliest first, for a
/// person to pick from. Null otherwise.
/// </param>
public record OwnedAlbumMatch(
    string Title,
    string LibraryArtist,
    string? ReleaseGroup,
    OwnedAlbumMatchMethod? Method,
    IReadOnlyList<string>? Candidates = null);

/// <summary>A Deezer album on the artist's Deezer page that matched no release group.</summary>
public record UnmatchedDeezerAlbum(long AlbumId, string? Title, string? RecordType, string? ReleaseDate);

/// <summary>
/// An artist's discography: MusicBrainz's release groups, each with its Deezer editions, plus the Deezer
/// albums that fit none of them. One per MusicBrainz artist. Built from cached MusicBrainz answers, and
/// rebuilt once <see cref="ExpiresAt"/> passes, which comes sooner for an artist still releasing.
/// </summary>
/// <param name="Mbid">The MusicBrainz artist.</param>
/// <param name="DeezerArtistIds">
/// The Deezer artist pages whose albums were matched. Empty when there is none, or when the library
/// artist shares its name with another act, since the name's Deezer page may be the other act's.
/// Links and barcodes still find editions without one.
/// </param>
/// <param name="Owned">
/// The library's albums by this artist, each with the release group it is. Also written to the catalog's
/// <c>albumIdentities</c>, which the metadata archive reads.
/// </param>
public record ArtistDiscography(
    string Mbid,
    string? Name,
    IReadOnlyList<long> DeezerArtistIds,
    DateTimeOffset BuiltAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<DiscographyReleaseGroup> ReleaseGroups,
    IReadOnlyList<UnmatchedDeezerAlbum> UnmatchedDeezer,
    IReadOnlyList<OwnedAlbumMatch>? Owned = null);

/// <summary>Stored <see cref="ArtistDiscography"/>s, one per MusicBrainz artist.</summary>
public interface IArtistDiscographyRepo
{
    Task<ArtistDiscography?> Get(string mbid);

    Task<ArtistDiscography[]> GetAll();

    /// <summary>When each stored discography is due for a rebuild, by artist MBID.</summary>
    Task<Dictionary<string, DateTimeOffset>> GetExpiresAt();

    /// <summary>Stores the discography, replacing the artist's previous one.</summary>
    Task Put(ArtistDiscography discography);

    /// <summary>Drops the discographies of artists (by MBID) the library no longer resolves to.</summary>
    Task DeleteAllExcept(IReadOnlyCollection<string> mbids);
}
