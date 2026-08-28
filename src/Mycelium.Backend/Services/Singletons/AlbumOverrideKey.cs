namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The lookup key for an album merge (<see cref="Mycelium.Interfaces.AlbumMatchOverride"/>):
/// the act the library files the album under, plus the album title in its canonical form. Built
/// identically by the purchase reconcile and the missing-album diff — the same discipline as
/// <see cref="AlbumTitleMatcher"/> — so a merge recorded once is honoured by both, and typography
/// differences between the stored title and the diffed title still match.
///
/// Keyed at record granularity (<see cref="AlbumTitleMatcher.NormalizeRecord"/>), the same as
/// ownership: a merge answers "the library already has this record, under this name", and the row
/// the merge was recorded from is whichever pressing happened to be on screen. Keying it tighter
/// would let the deluxe edition of a merged album come back as a gap the next morning.
/// </summary>
public static class AlbumOverrideKey
{
    public static string For(string artist, string? title) =>
        $"{AlbumTitleMatcher.NormalizeArtist(artist)} {AlbumTitleMatcher.NormalizeRecord(title)}";
}
