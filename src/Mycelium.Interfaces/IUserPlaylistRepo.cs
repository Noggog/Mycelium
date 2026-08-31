namespace Mycelium.Interfaces;

/// <summary>One track of a hand-built playlist, at its position in the running order.</summary>
public record PlaylistTrack(int Position, string Artist, string Album, string Title, string? File);

/// <summary>
/// One user's playlist, in a form that could be rebuilt somewhere that has never heard of Plex.
/// </summary>
/// <param name="Smart">
/// Whether the playlist is rule-driven. The two kinds are archived differently on purpose: a smart
/// playlist keeps its <paramref name="Rules"/> and no membership, because the rules are the durable
/// thing and the membership is just their current answer; a hand-built one keeps its
/// <paramref name="Tracks"/>, because that list *is* the work.
/// </param>
/// <param name="Rules">The stored filter query, for a smart playlist. Null otherwise.</param>
/// <param name="Tracks">The ordered membership, for a hand-built playlist. Empty for a smart one.</param>
public record UserPlaylist(
    string Title,
    bool Smart,
    string? Rules,
    IReadOnlyList<PlaylistTrack> Tracks);

/// <summary>
/// Local mirror of each user's Plex playlists.
///
/// <para>Playlists are created in, and owned by, the user's own Plex account — Mycelium has never
/// stored one. That makes them, along with star ratings, one of exactly two things a Plex failure
/// would take with it permanently, and a hand-curated playlist is the least reconstructable thing in
/// the whole system. Mirroring them here gives the metadata archive something to commit without
/// having to reach into Plex itself.</para>
///
/// <para>Nothing in the app reads these; they exist to be archived.</para>
/// </summary>
public interface IUserPlaylistRepo
{
    /// <summary>
    /// Replaces everything stored for one user, returning how many playlists they now have. A
    /// wholesale replace for the same reason as the ratings mirror: the sweep reads the account's whole
    /// playlist list, so a playlist deleted in Plex is one that simply stops appearing, and only a
    /// replace makes it stop appearing here too.
    /// </summary>
    Task<int> ReplaceForUser(string userId, IReadOnlyList<UserPlaylist> playlists);

    /// <summary>Every playlist stored for one user.</summary>
    Task<UserPlaylist[]> GetForUser(string userId);
}
