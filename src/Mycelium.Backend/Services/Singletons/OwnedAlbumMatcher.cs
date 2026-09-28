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
/// often than it holds the live record of the same name. Several core groups go to the one whose title is
/// the album's at listing level (<see cref="AlbumTitleMatcher.Normalize"/>), so <i>Sun</i> isn't the EP
/// <i>Sun (sampler)</i>. Still tied, it stays unmatched, with the tied groups kept as ranked candidates
/// for a person. Only when no title is equal is a near title tried, by the rule Deezer albums are held
/// to, and settled the same way.</para>
///
/// <para>A person's decision (<see cref="AlbumIdentity.Manual"/>) is kept as it is, and a group a person
/// unlinked from an album is never matched to it again. An album nothing fits keeps the release group
/// the older search-per-album backfill found, but only when that group is on this artist's discography:
/// that backfill searched under whatever MBID the name resolved to then, which could be the wrong act.</para>
/// </summary>
public static class OwnedAlbumMatcher
{
    /// <param name="earlier">What is recorded now, by title (case-insensitive).</param>
    public static List<OwnedAlbumMatch> Match(
        string libraryArtist,
        IReadOnlyList<string> albums,
        IReadOnlyList<DiscographyReleaseGroup> groups,
        IReadOnlyDictionary<string, AlbumIdentity> earlier)
    {
        var byTitle = new Dictionary<string, HashSet<DiscographyReleaseGroup>>();
        foreach (var group in groups)
        {
            foreach (var key in Titles(group).Select(AlbumTitleMatcher.NormalizeRecord).Where(k => k.Length > 0))
            {
                (byTitle.TryGetValue(key, out var set) ? set : byTitle[key] = new()).Add(group);
            }
        }

        var titleWords = byTitle.Select(t => (Words: DeezerEditionMatcher.Words(t.Key), Groups: t.Value)).ToList();
        var known = groups.Select(g => g.Mbid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<OwnedAlbumMatch>();
        foreach (var album in albums.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = earlier.GetValueOrDefault(album);
            if (before is { Manual: true })
            {
                result.Add(new OwnedAlbumMatch(album, libraryArtist, before.Mbid, OwnedAlbumMatchMethod.Manual));
                continue;
            }

            var rejected = (before?.Rejected ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var key = AlbumTitleMatcher.NormalizeRecord(album);
            List<DiscographyReleaseGroup> candidates;
            OwnedAlbumMatchMethod method;
            if (byTitle.TryGetValue(key, out var exact))
            {
                candidates = exact.Where(g => !rejected.Contains(g.Mbid)).ToList();
                method = OwnedAlbumMatchMethod.Title;
            }
            else if (key.Length > 0)
            {
                var words = DeezerEditionMatcher.Words(key);
                candidates = titleWords
                    .Where(t => DeezerEditionMatcher.Near(words, t.Words))
                    .SelectMany(t => t.Groups)
                    .Distinct()
                    .Where(g => !rejected.Contains(g.Mbid))
                    .ToList();
                method = OwnedAlbumMatchMethod.TitleFuzzy;
            }
            else
            {
                candidates = [];
                method = OwnedAlbumMatchMethod.Title;
            }

            if (Settle(candidates, album) is { } group)
            {
                result.Add(new OwnedAlbumMatch(album, libraryArtist, group.Mbid, method));
            }
            else if (before?.Mbid is { } mbid && known.Contains(mbid) && !rejected.Contains(mbid))
            {
                result.Add(new OwnedAlbumMatch(album, libraryArtist, mbid, OwnedAlbumMatchMethod.Earlier));
            }
            else
            {
                result.Add(new OwnedAlbumMatch(
                    album, libraryArtist, null, null,
                    candidates.Count > 1 ? Rank(candidates, album).Select(g => g.Mbid).ToList() : null));
            }
        }
        return result;
    }

    /// <summary>
    /// The one group; else the one core group; else, among the core groups (or all, when none is core),
    /// the one whose title is the album's at listing level. Null when that still leaves a choice.
    /// </summary>
    private static DiscographyReleaseGroup? Settle(IReadOnlyList<DiscographyReleaseGroup> candidates, string album)
    {
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var core = candidates.Where(g => g.IsCore).ToList();
        var pool = core.Count > 0 ? core : candidates;
        if (pool.Count == 1)
        {
            return pool[0];
        }

        var listing = AlbumTitleMatcher.Normalize(album);
        var same = pool.Where(g => ListingTitles(g).Contains(listing)).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>
    /// Tied groups, likeliest first: the album's title at listing level, core, an album before an EP
    /// before anything else, one with a Deezer edition, then the earliest. A guide for a person, not a
    /// decision.
    /// </summary>
    internal static IEnumerable<DiscographyReleaseGroup> Rank(IEnumerable<DiscographyReleaseGroup> tied, string album)
    {
        var listing = AlbumTitleMatcher.Normalize(album);
        return tied
            .OrderByDescending(g => ListingTitles(g).Contains(listing))
            .ThenByDescending(g => g.IsCore)
            .ThenBy(g => g.PrimaryType switch { "Album" => 0, "EP" => 1, _ => 2 })
            .ThenByDescending(g => g.Editions.Count > 0)
            .ThenBy(g => g.FirstReleaseDate is { Length: > 0 } date ? date : "9999", StringComparer.Ordinal)
            .ThenBy(g => g.Mbid, StringComparer.Ordinal);
    }

    private static IEnumerable<string?> Titles(DiscographyReleaseGroup group) =>
        new[] { group.Title }
            .Concat(group.ReleaseTitles ?? [])
            .Concat(group.Editions.Select(e => e.Title));

    private static HashSet<string> ListingTitles(DiscographyReleaseGroup group) =>
        Titles(group).Select(AlbumTitleMatcher.Normalize).Where(k => k.Length > 0).ToHashSet();
}
