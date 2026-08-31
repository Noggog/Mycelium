namespace Mycelium.Interfaces;

/// <summary>
/// One user's star rating on one track, in a form that means something away from the server it was
/// read off.
/// </summary>
/// <param name="Stars">
/// 0–5 with a half-star step — Plex's own 0–10 halved. Stars rather than the raw scale because that
/// is the concept every music system shares; the halving is exact, so nothing is lost.
/// </param>
/// <param name="File">
/// The backing file in the library server's path namespace. The most durable identity a track has —
/// rating keys are reissued by a rebuilt server, but the files are the library. Null when the server
/// reported no media, in which case the artist/album/title triple is all there is.
/// </param>
public record TrackRating(
    string Artist,
    string Album,
    string Title,
    int? TrackNumber,
    string? File,
    double Stars);

/// <summary>
/// Local mirror of each user's Plex song ratings.
///
/// <para>Ratings are per-Plex-account and live nowhere but Plex, which makes them one of exactly two
/// things (playlists are the other) that a Plex failure would take with it for good. Mirroring them
/// here gives them a second home, and lets the metadata archive stay a plain Mongo-to-git job instead
/// of having to reach into Plex on a different cadence with a different failure mode.</para>
///
/// <para>Nothing in the app reads these yet. They exist to be archived — and, later, to spare
/// <c>ReconsiderSweepService</c> a per-artist round trip it currently makes to Plex for numbers this
/// collection already holds.</para>
/// </summary>
public interface IUserTrackRatingRepo
{
    /// <summary>
    /// Replaces everything stored for one user with <paramref name="ratings"/>, returning how many rows
    /// the user now has.
    ///
    /// <para>A wholesale replace rather than an upsert because the sweep that feeds it reads every
    /// rated track the account has: a rating cleared in Plex simply stops appearing, and only a replace
    /// makes it stop appearing here too. An upsert would let a rating someone deliberately removed live
    /// on for ever.</para>
    /// </summary>
    Task<int> ReplaceForUser(string userId, IReadOnlyList<TrackRating> ratings);

    /// <summary>Every rating stored for one user.</summary>
    Task<TrackRating[]> GetForUser(string userId);
}
