namespace Mycelium.Interfaces;

/// <summary>
/// One track the library holds, identified in terms that outlive the server indexing it.
/// </summary>
/// <param name="File">
/// The backing file in the library server's path namespace. The durable identity — rating keys are
/// reissued by a rebuilt server, files are not — and the key per-user ratings are joined on.
/// </param>
public record LibraryTrack(
    string Artist,
    string Album,
    string Title,
    int? TrackNumber,
    string? File);

/// <summary>
/// The library's track listing: which songs exist, under which album, under which artist.
///
/// <para>Global rather than per-user, because the songs are a fact about the library while ratings
/// are a fact about a person. Kept so the metadata archive can write a real track listing per album
/// instead of only the songs somebody happened to rate.</para>
///
/// <para>Nothing in the app reads this yet; it exists to be archived.</para>
/// </summary>
public interface ILibraryTrackRepo
{
    /// <summary>
    /// Replaces the whole listing, returning how many tracks are now stored. Wholesale because the
    /// sweep that feeds it reads the entire library: a track deleted from disk simply stops appearing,
    /// and only a replace makes it stop appearing here too.
    /// </summary>
    Task<int> ReplaceAll(IReadOnlyList<LibraryTrack> tracks);

    /// <summary>Every track in the library.</summary>
    Task<LibraryTrack[]> GetAll();
}
