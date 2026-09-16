using Mycelium.ListenBrainz.Models;

namespace Mycelium.ListenBrainz.Services;

/// <summary>
/// Thin client over MusicBrainz's keyless search API, used only to resolve an artist name to its
/// MBID (the id the ListenBrainz similarity endpoint needs). Degrades gracefully (returns null) on
/// a miss or transport error rather than throwing, and self-throttles to MusicBrainz's 1 req/s.
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

    /// <summary>Look up a MusicBrainz artist by its MBID (name, disambiguation), or null if none/error.</summary>
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
}
