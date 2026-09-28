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
    /// <summary>A MusicBrainz link or a barcode: an identifier ties the two, not a title.</summary>
    High,

    /// <summary>Titles alone. Links the album, but is listed for review.</summary>
    Low,
}

/// <summary>One Deezer album that is an edition of a release group.</summary>
/// <param name="AlbumId">The id Deezer answers to now. An old id MusicBrainz links to is followed to it.</param>
/// <param name="Upc">The barcode, when it is known (a barcode match, or a Deezer album lookup).</param>
/// <param name="Available">Whether it can be streamed in this region. Null when Deezer didn't say.</param>
public record DeezerEdition(
    long AlbumId,
    string? Title,
    string? RecordType,
    string? ReleaseDate,
    int Tracks,
    string? Upc,
    bool? Available,
    EditionMatchMethod Method)
{
    public EditionConfidence Confidence =>
        Method is EditionMatchMethod.MbLink or EditionMatchMethod.Upc ? EditionConfidence.High : EditionConfidence.Low;
}

/// <summary>One album as MusicBrainz knows it, with the Deezer albums that are editions of it.</summary>
/// <param name="Mbid">The release group's MBID.</param>
/// <param name="PrimaryType">"Album", "EP", "Single", "Broadcast", "Other", or null.</param>
/// <param name="SecondaryTypes">"Live", "Compilation", "Remix" and so on. Empty for a plain studio record.</param>
/// <param name="FirstReleaseDate">"2024-04-12", "2024-04", "2024", or null.</param>
/// <param name="Rejected">Deezer album ids a person said are not this album. Never matched to it again.</param>
public record DiscographyReleaseGroup(
    string Mbid,
    string? Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? FirstReleaseDate,
    IReadOnlyList<DeezerEdition> Editions,
    IReadOnlyList<long> Rejected)
{
    /// <summary>
    /// An official album or EP with no secondary type: what an artist page shows open, and what the
    /// match rates are judged on. Iron Maiden has 338 release groups, most of them live records and
    /// compilations Deezer doesn't carry.
    /// </summary>
    public bool IsCore =>
        PrimaryType is "Album" or "EP" && SecondaryTypes.Count == 0;
}

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
public record ArtistDiscography(
    string Mbid,
    string? Name,
    IReadOnlyList<long> DeezerArtistIds,
    DateTimeOffset BuiltAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<DiscographyReleaseGroup> ReleaseGroups,
    IReadOnlyList<UnmatchedDeezerAlbum> UnmatchedDeezer);

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
