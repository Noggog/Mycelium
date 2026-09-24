using Mycelium.ListenBrainz.Models;

namespace Mycelium.ListenBrainz.Services;

/// <summary>
/// Thin client over MusicBrainz's keyless web service: artist resolution, and an artist's albums
/// (release groups) and their editions (releases). Degrades gracefully (returns null) on a miss or
/// transport error rather than throwing. Every request goes through <see cref="MusicBrainzGate"/>,
/// so it is limited to MusicBrainz's 1 req/s and queued behind interactive requests when made from
/// inside <see cref="MusicBrainzGate.Background"/>.
/// </summary>
public interface IMusicBrainzApi
{
    /// <summary>
    /// Free-text artist search in relevance order: empty when MusicBrainz answered with nothing, null
    /// when it didn't answer at all. The two must stay apart — a caller that records "no such artist"
    /// would otherwise turn a rate-limit blip into a permanent miss. Name resolution picks from these
    /// with <see cref="MusicBrainzArtistMatch"/>; the "Correct association" picker shows them as-is.
    /// </summary>
    Task<MusicBrainzArtist[]?> SearchArtists(string query, int limit);

    /// <summary>
    /// Look up a MusicBrainz artist by its MBID (name, disambiguation), or null if none/error. When
    /// MusicBrainz has merged the artist into another, the answer is the surviving artist — so its
    /// <see cref="MusicBrainzArtist.Id"/> can differ from <paramref name="mbid"/>, and the returned one
    /// is the one to keep.
    /// </summary>
    Task<MusicBrainzArtist?> GetArtist(string mbid);

    /// <summary>
    /// Resolve one of an artist's albums to its release-group MBID, or null on a miss or error.
    ///
    /// <para>Scoped to <paramref name="artistMbid"/> rather than searched by title alone, which is
    /// what makes the answer trustworthy: "Greatest Hits" matches thousands of records globally and
    /// exactly one within a given act's discography. An artist we have no MBID for therefore has no
    /// album MBIDs either — a wrong id is worse than none, since the whole point of storing one is
    /// that it can be trusted years later.</para>
    /// </summary>
    Task<MusicBrainzReleaseGroup?> SearchReleaseGroup(string artistMbid, string title);

    /// <summary>
    /// Every release group credited to the artist — its whole discography as MusicBrainz knows it, with
    /// primary and secondary types and first release dates. Walks every page. Empty when MusicBrainz
    /// has nothing (or no such artist); null when any page went unanswered, never a partial list.
    /// </summary>
    Task<MusicBrainzReleaseGroup[]?> BrowseReleaseGroups(string artistMbid);

    /// <summary>
    /// The artist's official album, EP and single releases, each with its release group, barcode and
    /// URL links (store pages among them). This is what ties a release group to a Deezer album: by a
    /// deezer.com link, or by a barcode Deezer answers as a UPC. Same empty/null contract as
    /// <see cref="BrowseReleaseGroups"/>.
    /// </summary>
    Task<MusicBrainzRelease[]?> BrowseReleases(string artistMbid);

    /// <summary>
    /// Which MusicBrainz artists and releases link to <paramref name="resource"/> (a Deezer artist or
    /// album page, say). Relations are empty when MusicBrainz has never heard of the URL; null means
    /// it didn't answer. MusicBrainz stores URLs in a normalised form (for Deezer,
    /// <c>https://www.deezer.com/artist/{id}</c>), and only an exact match is found.
    /// </summary>
    Task<MusicBrainzUrl?> LookupUrl(string resource);
}
