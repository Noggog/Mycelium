using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Finds the release group each owned album is, on the artist's stored discography: the part of
/// <see cref="ArtistDiscographyBuilder"/> that fills <c>albumIdentities</c>. No I/O, and no MusicBrainz
/// request per album: the discography already holds every title the artist's release groups go by.
///
/// <para>A group answers to its own title, its releases' titles, and its Deezer editions' titles, all at
/// record level (<see cref="AlbumTitleMatcher.NormalizeRecord"/>), so "Powerslave (2015 Remaster)" on
/// disk is <i>Powerslave</i>. A title several groups answer to goes to the one core group
/// (<see cref="DiscographyReleaseGroup.IsCore"/>) among them, since the library holds albums far more
/// often than it holds the live record of the same name. Still tied, it stays unmatched. Only then is a
/// near title tried, by the rule Deezer albums are held to.</para>
///
/// <para>An album nothing fits keeps the release group the older search-per-album backfill found, but
/// only when that group is on this artist's discography: that backfill searched under whatever MBID the
/// name resolved to then, which could be the wrong act.</para>
/// </summary>
public static class OwnedAlbumMatcher
{
    /// <param name="earlier">What is recorded now, by title (case-insensitive), from any earlier matcher.</param>
    public static List<OwnedAlbumMatch> Match(
        string libraryArtist,
        IReadOnlyList<string> albums,
        IReadOnlyList<DiscographyReleaseGroup> groups,
        IReadOnlyDictionary<string, string?> earlier)
    {
        var byTitle = new Dictionary<string, HashSet<DiscographyReleaseGroup>>();
        foreach (var group in groups)
        {
            var titles = new[] { group.Title }
                .Concat(group.ReleaseTitles ?? [])
                .Concat(group.Editions.Select(e => e.Title));
            foreach (var key in titles.Select(AlbumTitleMatcher.NormalizeRecord).Where(k => k.Length > 0))
            {
                (byTitle.TryGetValue(key, out var set) ? set : byTitle[key] = new()).Add(group);
            }
        }

        var titleWords = byTitle.Select(t => (Words: DeezerEditionMatcher.Words(t.Key), Groups: t.Value)).ToList();
        var known = groups.Select(g => g.Mbid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<OwnedAlbumMatch>();
        foreach (var album in albums.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = AlbumTitleMatcher.NormalizeRecord(album);
            OwnedAlbumMatch Found(DiscographyReleaseGroup? group, OwnedAlbumMatchMethod method) =>
                new(album, libraryArtist, group?.Mbid, group is null ? null : method);

            if (byTitle.TryGetValue(key, out var exact))
            {
                if (Settle(exact) is { } group)
                {
                    result.Add(Found(group, OwnedAlbumMatchMethod.Title));
                    continue;
                }
            }
            else if (key.Length > 0)
            {
                var words = DeezerEditionMatcher.Words(key);
                var near = titleWords
                    .Where(t => DeezerEditionMatcher.Near(words, t.Words))
                    .SelectMany(t => t.Groups)
                    .ToHashSet();
                if (Settle(near) is { } group)
                {
                    result.Add(Found(group, OwnedAlbumMatchMethod.TitleFuzzy));
                    continue;
                }
            }

            var before = earlier.GetValueOrDefault(album);
            result.Add(before is not null && known.Contains(before)
                ? new OwnedAlbumMatch(album, libraryArtist, before, OwnedAlbumMatchMethod.Earlier)
                : Found(null, OwnedAlbumMatchMethod.Title));
        }
        return result;
    }

    /// <summary>The one group, or the one core group among several; null when that still leaves a choice.</summary>
    private static DiscographyReleaseGroup? Settle(IReadOnlyCollection<DiscographyReleaseGroup> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates.First();
        }

        var core = candidates.Where(g => g.IsCore).ToList();
        return core.Count == 1 ? core[0] : null;
    }
}
