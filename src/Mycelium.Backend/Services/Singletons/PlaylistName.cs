namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The one difference between what a stock playlist is called here and what it is called in Plex:
/// a playlist this app builds is wrapped — <c>// 3★+ (Fresh 1mo) \\</c> — so that in a Plex sidebar
/// listing dozens of hand-made playlists, the generated ones read as a set and sort together.
///
/// <para><b>The wrapper lives at the Plex boundary and nowhere else.</b> Definitions, the survey and
/// the page all speak the bare title, so nothing in the app has to remember the decoration to match,
/// sort or display a row — <see cref="InPlex"/> is applied on the way out, <see cref="Bare"/> on the
/// way back, and that is the whole of it.</para>
///
/// <para><b>Undecorating is what keeps the two names one name.</b> Existing playlists are matched by
/// their rules, so a bare-named playlist created before this wrapper existed is still recognised; it
/// is the <em>name clash</em> check that would otherwise split in two, reporting a hand-edited
/// "3★+ (Fresh 1mo)" as free while its wrapped twin was taken. Comparing bare-to-bare collapses
/// that: both spellings are the same name, which is the honest answer either way.</para>
/// </summary>
public static class PlaylistName
{
    private const string Prefix = "// ";
    private const string Suffix = @" \\";

    /// <summary>What Plex is told the playlist is called.</summary>
    public static string InPlex(string title) => $"{Prefix}{title}{Suffix}";

    /// <summary>
    /// A Plex playlist's name as the app says it: the wrapper taken off when it is there, and the
    /// name left exactly as the user wrote it when it isn't. A title that is <em>only</em> the
    /// wrapper is left alone — stripping it would leave nothing to call the playlist.
    /// </summary>
    public static string Bare(string title) =>
        title.Length > Prefix.Length + Suffix.Length
        && title.StartsWith(Prefix, StringComparison.Ordinal)
        && title.EndsWith(Suffix, StringComparison.Ordinal)
            ? title[Prefix.Length..^Suffix.Length]
            : title;
}
