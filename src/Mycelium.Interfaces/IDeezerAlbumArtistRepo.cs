namespace Mycelium.Interfaces;

/// <summary>
/// Durable memo of the act Deezer credits a release to, keyed by Deezer album id. Deezer's
/// discography listing omits the album-artist, so learning it costs a <c>/album/{id}</c> call each —
/// and a collaboration surfaced through one member (a duo record listed under "Milo") is filed in the
/// library under the duo, so the diff can't tell owned from missing without it.
///
/// An album id's credited act never changes, so this is a pure memo: written once, read forever. It
/// lives in Mongo rather than in process memory because the alternative — re-learning the whole map
/// after every restart — put a hundred-odd rate-limited Deezer calls in front of the first person to
/// open an artist's albums.
/// </summary>
public interface IDeezerAlbumArtistRepo
{
    /// <summary>
    /// The credited act for each of these album ids that we've already learned. Ids we haven't are
    /// simply absent — a missing entry means "not looked up yet", never "no artist".
    /// </summary>
    Task<Dictionary<long, string>> Get(IReadOnlyCollection<long> albumIds);

    /// <summary>Records what a batch of lookups learned. Idempotent.</summary>
    Task Put(IReadOnlyDictionary<long, string> artistsByAlbumId);
}
