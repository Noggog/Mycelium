using System.Text.RegularExpressions;
using Mycelium.Deezer.Models;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Places Deezer albums on an artist's MusicBrainz release groups: the part of
/// <see cref="ArtistDiscographyBuilder"/> with no I/O. The lookups that need Deezer (following an old
/// album id, asking about a barcode) are done by the builder and handed in already answered.
///
/// <para><b>Order, strongest first.</b> A Deezer album goes to one release group only, by the strongest
/// method that places it:</para>
/// <list type="number">
/// <item>A MusicBrainz link from one of the group's releases to the album.</item>
/// <item>A barcode of one of the group's releases, which Deezer answers with the album.</item>
/// <item>The record-level title (<see cref="AlbumTitleMatcher.NormalizeRecord"/>) of the group or one of
/// its releases. A release group is named after one edition, so a Deezer album titled after another
/// edition is still found.</item>
/// <item>A near title (<see cref="EditionMatchMethod.TitleFuzzy"/>).</item>
/// </list>
/// <para>A title that fits several groups is settled by type (album, EP, single), then by year. Deezer's
/// date is often a reissue's, so the year only ever breaks a tie. Still tied, the album stays unmatched:
/// the 1984 <i>Aces High</i> single and a 2020 live single of the same name are the case this avoids.</para>
/// </summary>
public static partial class DeezerEditionMatcher
{
    /// <summary>Share of words two near titles must have in common, when neither contains the other.</summary>
    private const double NearTitleOverlap = 0.8;

    /// <param name="listing">
    /// The artist's Deezer albums: its own listing, then what album search added
    /// (<paramref name="searchOnly"/>). Empty when the artist has no Deezer page.
    /// </param>
    /// <param name="linked">Deezer albums a release links to, by release MBID, redirects already followed.</param>
    /// <param name="byBarcode">What Deezer answered for each barcode it has an album for.</param>
    /// <param name="rejected">Per release group MBID, Deezer album ids a person ruled out.</param>
    public static (List<DiscographyReleaseGroup> Groups, List<UnmatchedDeezerAlbum> Unmatched) Match(
        IReadOnlyList<MusicBrainzReleaseGroup> groups,
        IReadOnlyList<MusicBrainzRelease> releases,
        IReadOnlyList<DeezerAlbum> listing,
        IReadOnlySet<long> searchOnly,
        IReadOnlyDictionary<string, IReadOnlyList<DeezerAlbum>> linked,
        IReadOnlyDictionary<string, DeezerAlbum> byBarcode,
        IReadOnlyDictionary<string, IReadOnlySet<long>> rejected)
    {
        var known = groups.Where(g => g.Id is { Length: > 0 }).DistinctBy(g => g.Id).ToList();
        var groupIds = known.Select(g => g.Id!).ToHashSet();
        var placed = new Dictionary<long, (string Group, DeezerEdition Edition)>();

        bool IsRejected(string group, long albumId) =>
            rejected.TryGetValue(group, out var ids) && ids.Contains(albumId);

        void Place(string group, DeezerAlbum album, EditionMatchMethod method)
        {
            if (!IsRejected(group, album.id) && !placed.ContainsKey(album.id))
            {
                placed[album.id] = (group, Edition(album, method));
            }
        }

        var editions = releases
            .Where(r => r.ReleaseGroup?.Id is { } g && groupIds.Contains(g))
            .ToList();

        foreach (var release in editions)
        {
            foreach (var album in linked.GetValueOrDefault(release.Id ?? "") ?? [])
            {
                Place(release.ReleaseGroup!.Id!, album, EditionMatchMethod.MbLink);
            }
        }

        foreach (var release in editions)
        {
            if (release.Barcode is { Length: > 0 } barcode && byBarcode.TryGetValue(barcode, out var album))
            {
                Place(release.ReleaseGroup!.Id!, album, EditionMatchMethod.Upc);
            }
        }

        // Record key → the groups answering to it, by their own title or any release's.
        var byTitle = new Dictionary<string, HashSet<string>>();
        void Title(string? title, string group)
        {
            var key = AlbumTitleMatcher.NormalizeRecord(title);
            if (key.Length > 0)
            {
                (byTitle.TryGetValue(key, out var set) ? set : byTitle[key] = new()).Add(group);
            }
        }
        foreach (var group in known)
        {
            Title(group.Title, group.Id!);
        }
        foreach (var release in editions)
        {
            Title(release.Title, release.ReleaseGroup!.Id!);
        }

        var groupsById = known.ToDictionary(g => g.Id!);
        var titleGroups = byTitle.Select(t => (Words: Words(t.Key), Groups: t.Value)).ToList();

        foreach (var album in listing.Where(a => !placed.ContainsKey(a.id)))
        {
            var key = AlbumTitleMatcher.NormalizeRecord(album.title);
            if (key.Length == 0)
            {
                continue;
            }

            var exact = byTitle.GetValueOrDefault(key);
            if (exact is not null && Settle(exact, album, groupsById) is { } group)
            {
                Place(group, album, searchOnly.Contains(album.id) ? EditionMatchMethod.Search : EditionMatchMethod.Title);
                continue;
            }
            if (exact is not null)
            {
                // A tie the type and year couldn't break. A looser rule would only pick blindly.
                continue;
            }

            var words = Words(key);
            var near = titleGroups
                .Where(t => Near(words, t.Words))
                .SelectMany(t => t.Groups)
                .ToHashSet();
            if (near.Count > 0 && Settle(near, album, groupsById) is { } nearGroup)
            {
                Place(nearGroup, album, EditionMatchMethod.TitleFuzzy);
            }
        }

        var result = known
            .Select(g => new DiscographyReleaseGroup(
                g.Id!,
                g.Title,
                g.PrimaryType,
                g.SecondaryTypes ?? [],
                string.IsNullOrEmpty(g.FirstReleaseDate) ? null : g.FirstReleaseDate,
                placed.Values
                    .Where(p => p.Group == g.Id)
                    .Select(p => p.Edition)
                    .OrderBy(e => e.Method)
                    .ThenBy(e => e.ReleaseDate, StringComparer.Ordinal)
                    .ToList(),
                rejected.GetValueOrDefault(g.Id!)?.Order().ToList() ?? []))
            .ToList();

        var unmatched = listing
            .Where(a => !placed.ContainsKey(a.id))
            .DistinctBy(a => a.id)
            .Select(a => new UnmatchedDeezerAlbum(a.id, a.title, a.record_type, Blank(a.release_date)))
            .ToList();

        return (result, unmatched);
    }

    /// <summary>
    /// The one group among <paramref name="candidates"/> the album belongs to: narrowed by type, then by
    /// year, each only when it leaves something. Null when that still leaves more than one.
    /// </summary>
    private static string? Settle(
        IReadOnlyCollection<string> candidates, DeezerAlbum album, IReadOnlyDictionary<string, MusicBrainzReleaseGroup> groups)
    {
        IReadOnlyCollection<string> left = candidates;
        left = Narrow(left, g => SameType(album.record_type, groups[g]));
        left = Narrow(left, g => album.Year is { } year && Year(groups[g].FirstReleaseDate) == year);
        return left.Count == 1 ? left.First() : null;
    }

    private static IReadOnlyCollection<string> Narrow(IReadOnlyCollection<string> groups, Func<string, bool> keep)
    {
        if (groups.Count < 2)
        {
            return groups;
        }

        var kept = groups.Where(keep).ToList();
        return kept.Count > 0 ? kept : groups;
    }

    /// <summary>Deezer's record type ("album", "ep", "single", "compilation") against the group's types.</summary>
    private static bool SameType(string? recordType, MusicBrainzReleaseGroup group) =>
        recordType?.ToLowerInvariant() switch
        {
            "album" => group.PrimaryType == "Album" && group.SecondaryTypes?.Contains("Compilation") != true,
            "ep" => group.PrimaryType == "EP",
            "single" => group.PrimaryType == "Single",
            "compilation" => group.SecondaryTypes?.Contains("Compilation") == true,
            _ => false,
        };

    /// <summary>
    /// Whether two record keys name the same record loosely: every word of the shorter (two words at
    /// least) is in the longer — a dropped subtitle, "Maiden England" and "Maiden England '88" — or they
    /// share nearly all their words. Punctuation and "&amp;" against "and" are already folded by the key.
    /// A word only one of them has may not mark a different recording: "Aces High (Live)" is not a near
    /// "Aces High".
    /// </summary>
    private static bool Near(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        var (shorter, longer) = a.Count <= b.Count ? (a, b) : (b, a);
        if (shorter.Count == 0
            || a.Except(b).Concat(b.Except(a)).Any(AlbumTitleMatcher.IsDistinctRecordingWord))
        {
            return false;
        }

        var shared = shorter.Count(longer.Contains);
        if (shared == shorter.Count && shorter.Count >= 2)
        {
            return true;
        }

        return (double)shared / (a.Count + b.Count - shared) >= NearTitleOverlap;
    }

    private static IReadOnlySet<string> Words(string key) =>
        NonWord().Split(key).Where(w => w.Length > 0).ToHashSet();

    private static int? Year(string? date) =>
        date is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), out var year) && year > 0 ? year : null;

    private static DeezerEdition Edition(DeezerAlbum album, EditionMatchMethod method) =>
        new(album.id, album.title, album.record_type, Blank(album.release_date), album.nb_tracks,
            Blank(album.upc), album.available, method);

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
