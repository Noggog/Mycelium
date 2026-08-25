namespace Mycelium.Interfaces;

/// <summary>
/// One asserted album merge: a release the diff sees as "missing" is actually already in the
/// library under a slightly different title (e.g. Deezer's "DOOM (Original Game Soundtrack)" vs.
/// Plex's "Doom: Original Game Soundtrack"), which the title normalizer can't collapse on its own.
/// Usually the user's assertion, made from the merge picker; the purchase reconcile also writes one
/// on its own account when a release it downloaded lands under the title Plex simplified it to.
/// Keyed by the act the library files the album under (<see cref="MatchArtist"/>) plus the Deezer
/// title; <see cref="LibraryTitle"/> is the owned album it was merged into (kept for display/audit).
/// </summary>
public record AlbumMatchOverride(string MatchArtist, string DeezerTitle, string LibraryTitle);

/// <summary>
/// Durable store of manual album merges. Consulted by BOTH the purchase reconcile and the
/// missing-album diff so a merged album drops off the download queue AND stops resurfacing as
/// missing anywhere — the same "single match definition" the title normalizer enforces, extended
/// with human-supplied equivalences. Global (a fact about the shared library), one doc per merge.
/// </summary>
public interface IAlbumMatchOverrideRepo
{
    /// <summary>Every recorded merge.</summary>
    Task<AlbumMatchOverride[]> GetAll();

    /// <summary>Records (or refreshes) a merge. Idempotent for the same (match-artist, Deezer title).</summary>
    Task Add(AlbumMatchOverride @override);
}
