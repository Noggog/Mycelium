using System.Text.Json.Serialization;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>Which of the reconciliation page's album groups an album is in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlbumReconcileKind
{
    /// <summary>The title fits several release groups and nothing settles it: a person picks one.</summary>
    Pick,

    /// <summary>No release group fits: most likely not on MusicBrainz yet.</summary>
    Missing,

    /// <summary>Linked on a near title only: a person confirms or unlinks it.</summary>
    Loose,
}

/// <summary>A release group in brief, as a suggestion or as what an album is linked to.</summary>
/// <param name="Tracks">The most tracks any of its Deezer editions has. Null when it has none.</param>
public record ReleaseGroupBrief(
    string Mbid,
    string? Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? FirstReleaseDate,
    int? Tracks,
    bool HasDeezer)
{
    public static ReleaseGroupBrief Of(DiscographyReleaseGroup g) => new(
        g.Mbid,
        g.Title,
        g.PrimaryType,
        g.SecondaryTypes,
        g.FirstReleaseDate,
        g.Editions.Count > 0 ? g.Editions.Max(e => e.Tracks) : null,
        g.Editions.Count > 0);
}

/// <summary>One owned album that needs a person.</summary>
/// <param name="LibraryArtist">The library artist it is filed under (<see cref="ArtistResolution.Id"/>).</param>
/// <param name="Linked">For <see cref="AlbumReconcileKind.Loose"/>: the group it is linked to.</param>
/// <param name="Candidates">For <see cref="AlbumReconcileKind.Pick"/>: the tied groups, likeliest first.</param>
/// <param name="DeezerAlbumId">
/// For <see cref="AlbumReconcileKind.Missing"/>: a Deezer album of the same record-level title that fits no
/// release group, for a Harmony import. Null when there is none.
/// </param>
public record AlbumReconcileItem(
    string Title,
    string LibraryArtist,
    AlbumReconcileKind Kind,
    ReleaseGroupBrief? Linked,
    IReadOnlyList<ReleaseGroupBrief> Candidates,
    long? DeezerAlbumId);

/// <summary>One artist's albums that need a person, collapsed to a row on the page.</summary>
public record AlbumReconcileArtist(
    string Mbid,
    string? Name,
    int Pick,
    int Missing,
    int Loose,
    IReadOnlyList<AlbumReconcileItem> Albums);

/// <summary>
/// The reconciliation page's album groups (<c>MUSICBRAINZ-IDENTITY.md</c> §6.1): owned albums the
/// discography build couldn't settle, per artist, and a person's answers to them. An answer is stored as a
/// manual <see cref="AlbumIdentity"/>, which no rebuild overrides, and patched onto the stored discography
/// so the page reflects it without waiting for one.
/// </summary>
public class AlbumReconciliation
{
    private readonly IArtistDiscographyRepo _discographies;
    private readonly IArtistResolutionRepo _resolutions;
    private readonly IArtistCatalogRepo _catalog;

    public AlbumReconciliation(
        IArtistDiscographyRepo discographies,
        IArtistResolutionRepo resolutions,
        IArtistCatalogRepo catalog)
    {
        _discographies = discographies;
        _resolutions = resolutions;
        _catalog = catalog;
    }

    /// <summary>Every artist with an owned album that needs a person, by name.</summary>
    public async Task<IReadOnlyList<AlbumReconcileArtist>> List() =>
        (await _discographies.GetAll())
            .Select(Items)
            .OfType<AlbumReconcileArtist>()
            .OrderBy(a => a.Name ?? a.Mbid, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>How many owned albums need a person, across every artist: the nav badge's share.</summary>
    public async Task<int> Count() =>
        (await _discographies.GetAll()).Sum(d => Items(d)?.Albums.Count ?? 0);

    /// <summary>One artist's albums that need a person, or null when none do.</summary>
    internal static AlbumReconcileArtist? Items(ArtistDiscography d)
    {
        var groups = d.ReleaseGroups.ToDictionary(g => g.Mbid, StringComparer.OrdinalIgnoreCase);
        var deezer = d.UnmatchedDeezer
            .GroupBy(u => AlbumTitleMatcher.NormalizeRecord(u.Title))
            .ToDictionary(g => g.Key, g => g.First().AlbumId);

        var items = new List<AlbumReconcileItem>();
        foreach (var owned in d.Owned ?? [])
        {
            if (owned.Method == OwnedAlbumMatchMethod.TitleFuzzy
                && owned.ReleaseGroup is { } linked
                && groups.TryGetValue(linked, out var group))
            {
                items.Add(new AlbumReconcileItem(
                    owned.Title, owned.LibraryArtist, AlbumReconcileKind.Loose, ReleaseGroupBrief.Of(group), [], null));
            }
            else if (owned.ReleaseGroup is null && owned.Method != OwnedAlbumMatchMethod.Manual)
            {
                var candidates = (owned.Candidates ?? [])
                    .Select(c => groups.GetValueOrDefault(c))
                    .OfType<DiscographyReleaseGroup>()
                    .Select(ReleaseGroupBrief.Of)
                    .ToList();
                items.Add(candidates.Count > 0
                    ? new AlbumReconcileItem(owned.Title, owned.LibraryArtist, AlbumReconcileKind.Pick, null, candidates, null)
                    : new AlbumReconcileItem(
                        owned.Title, owned.LibraryArtist, AlbumReconcileKind.Missing, null, [],
                        deezer.TryGetValue(AlbumTitleMatcher.NormalizeRecord(owned.Title), out var id) ? id : null));
            }
        }

        return items.Count == 0
            ? null
            : new AlbumReconcileArtist(
                d.Mbid,
                d.Name,
                items.Count(i => i.Kind == AlbumReconcileKind.Pick),
                items.Count(i => i.Kind == AlbumReconcileKind.Missing),
                items.Count(i => i.Kind == AlbumReconcileKind.Loose),
                items
                    .OrderBy(i => i.Kind)
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    /// <summary>
    /// Links an album to a release group: a pick among tied groups, a pasted group, or a confirmed loose
    /// match. The group need not be on the artist's discography: MusicBrainz may credit the record to
    /// another artist ("The Miles Davis Quintet").
    /// </summary>
    public Task<ArtistDiscography?> Link(string artistMbid, string libraryArtist, string title, string releaseGroup) =>
        Decide(artistMbid, libraryArtist, title,
            (before, _) => new AlbumIdentity(title, releaseGroup, Manual: true, before?.Rejected),
            owned => owned with { ReleaseGroup = releaseGroup, Method = OwnedAlbumMatchMethod.Manual, Candidates = null });

    /// <summary>
    /// Says the album isn't on MusicBrainz and a person is leaving it that way: it drops off the page,
    /// and no rebuild matches it.
    /// </summary>
    public Task<ArtistDiscography?> Leave(string artistMbid, string libraryArtist, string title) =>
        Decide(artistMbid, libraryArtist, title,
            (before, _) => new AlbumIdentity(title, null, Manual: true, before?.Rejected),
            owned => owned with { ReleaseGroup = null, Method = OwnedAlbumMatchMethod.Manual, Candidates = null });

    /// <summary>
    /// Unlinks an album from the group it is linked to, and never matches it there again. It goes back
    /// to "not on MusicBrainz".
    /// </summary>
    public Task<ArtistDiscography?> Unlink(string artistMbid, string libraryArtist, string title) =>
        Decide(artistMbid, libraryArtist, title,
            (before, match) => new AlbumIdentity(
                title,
                null,
                Manual: false,
                (before?.Rejected ?? []).Concat(match.ReleaseGroup is { } linked ? [linked] : []).Distinct().ToList()),
            owned => owned with { ReleaseGroup = null, Method = null, Candidates = null });

    /// <summary>
    /// Records a decision on the catalog, then patches the stored discography to match. Null when the
    /// artist has no discography, or the album isn't one of the library artist's owned albums on it.
    /// </summary>
    private async Task<ArtistDiscography?> Decide(
        string artistMbid,
        string libraryArtist,
        string title,
        Func<AlbumIdentity?, OwnedAlbumMatch, AlbumIdentity> identity,
        Func<OwnedAlbumMatch, OwnedAlbumMatch> owned)
    {
        var discography = await _discographies.Get(artistMbid);
        var index = discography?.Owned?.ToList().FindIndex(o =>
            o.LibraryArtist == libraryArtist && string.Equals(o.Title, title, StringComparison.OrdinalIgnoreCase)) ?? -1;
        if (discography is null || index < 0 || await _resolutions.Get(libraryArtist) is not { } resolution)
        {
            return null;
        }

        var match = discography.Owned![index];
        var artist = new ArtistKey(resolution.Artist);
        var before = (await _catalog.GetAlbumIdentities(artist)).GetValueOrDefault(match.Title);
        await _catalog.SetAlbumIdentities(artist, [identity(before, match) with { Title = match.Title }]);

        var patched = discography.Owned!.ToList();
        patched[index] = owned(match);
        var updated = discography with { Owned = patched };
        await _discographies.Put(updated);
        return updated;
    }
}
