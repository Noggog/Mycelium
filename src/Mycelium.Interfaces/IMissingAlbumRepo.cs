namespace Mycelium.Interfaces;

/// <summary>
/// Global store of albums that exist on Deezer for owned artists but aren't in the library — the
/// raw material for each user's "missing albums" feed. A fact about the shared library, not
/// per-user; the per-user verdict lives in <see cref="IUserAlbumRatingRepo"/>. One doc per
/// (artist, album); populated by the missing-album sync job.
/// </summary>
public interface IMissingAlbumRepo
{
    /// <summary>
    /// Replaces the full missing-album set for one artist (deletes the artist's prior rows, inserts
    /// the current ones). Albums acquired since the last run simply stop being supplied, so they
    /// drop out — keeping the feed honest with no separate cleanup pass.
    /// </summary>
    Task ReplaceForArtist(string artistName, IReadOnlyList<MissingAlbum> missing);

    /// <summary>
    /// Inserts or updates <em>one</em> row, leaving the artist's other rows alone — the additive
    /// counterpart to <see cref="ReplaceForArtist"/>.
    ///
    /// <para>Needed because collections all file under the same handful of umbrella acts
    /// (<see cref="UmbrellaArtist"/>): every various-artists compilation anyone adds is a row under
    /// "Various Artists", so writing one through <see cref="ReplaceForArtist"/> would delete all the
    /// others. There is no discography behind these rows for a replace to be the truth of, either —
    /// each is added on its own, by someone naming that record.</para>
    /// </summary>
    Task Upsert(MissingAlbum missing);

    /// <summary>Every missing album, ordered by artist then album, for building a user's feed.</summary>
    Task<MissingAlbum[]> GetAll();
}
