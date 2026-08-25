namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The lookup key for an album merge (<see cref="Mycelium.Interfaces.AlbumMatchOverride"/>):
/// the act the library files the album under, plus the album title in its canonical form. Built
/// identically by the purchase reconcile and the missing-album diff — the same discipline as
/// <see cref="AlbumTitleMatcher"/> — so a merge recorded once is honoured by both, and typography
/// differences between the stored title and the diffed title still match.
/// </summary>
public static class AlbumOverrideKey
{
    public static string For(string artist, string? title) =>
        $"{artist.ToLowerInvariant()} {AlbumTitleMatcher.Normalize(title)}";
}
