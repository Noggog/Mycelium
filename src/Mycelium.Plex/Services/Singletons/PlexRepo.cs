using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services;

namespace Mycelium.Plex.Services.Singletons;

public class PlexRepo : ILibraryQuery
{
    private readonly PlexApi _plexApi;
    private readonly ILogger<PlexRepo> _logger;

    public PlexRepo(PlexApi plexApi, ILogger<PlexRepo> logger)
    {
        _plexApi = plexApi;
        _logger = logger;
    }

    public Task<ArtistPackage> QueryArtistPackage(ArtistKey artistKey)
    {
        throw new NotImplementedException();
    }

    public Task<ArtistPackage[]> QueryAllData()
    {
        throw new NotImplementedException();
    }

    public async Task<ArtistMetadata[]> QueryAllArtistMetadata()
    {
        var plexLibrary = await _plexApi.ResolveLibrary();
        // Plex joins collaborators into one title with ';' — split them so "Nina Simone;Hot Chip"
        // becomes two artists, then group by name (the split halves can collide with standalone
        // entries) and union each artist's genre tags across every title they appear in.
        return (await _plexApi.GetMusicArtists(plexLibrary.Key))
            .SelectMany(a => ArtistNames.Split(a.Title)
                .Select(name => (Name: name, a.RatingKey, Genres: ExtractGenres(a))))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistMetadata(
                ArtistKey: new ArtistKey(g.Key),
                ArtistImageUrl: null, // Plex supplies no public image URL; backfilled from Deezer later.
                Genres: g.SelectMany(x => x.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                // Keep the rating key of every Plex item this name appears in, so the tagger can target
                // them directly instead of rescanning the whole library (a ';'-joined collaborator title
                // makes one Plex item back several names; a name can also recur across items).
                PlexRatingKeys: g.Select(x => x.RatingKey).Distinct().ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractGenres(PlexMusicArtist artist) =>
        artist.Genre?
            .Select(t => t.Tag)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray()
        ?? Array.Empty<string>();

    public async Task<Dictionary<int, AudioQuality?>> QueryAlbumQuality(
        IReadOnlyCollection<int> albumRatingKeys)
    {
        var result = new Dictionary<int, AudioQuality?>();
        foreach (var key in albumRatingKeys.Distinct())
        {
            var tracks = await _plexApi.GetAlbumTracks(key);
            if (tracks.Length == 0)
            {
                // No tracks (or the key no longer resolves) is no evidence — leave it absent so it
                // stays "don't know" rather than being recorded as some quality it isn't.
                continue;
            }
            result[key] = AudioQualityTier.Majority(
                tracks.Select(t => AudioQualityTier.FromCodec(t.AudioCodec)));
        }
        return result;
    }

    public async Task<string[]> QueryAlbumFiles(int albumRatingKey) =>
        (await _plexApi.GetAlbumTracks(albumRatingKey))
        .Select(t => t.File)
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Select(f => f!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public async Task<Dictionary<int, AudioQuality?>> QueryAllAlbumQuality()
    {
        var plexLibrary = await _plexApi.ResolveLibrary();
        return (await _plexApi.GetMusicTracks(plexLibrary.Key))
            .Where(t => t.AlbumRatingKey != 0)
            .GroupBy(t => t.AlbumRatingKey)
            .ToDictionary(
                g => g.Key,
                g => AudioQualityTier.Majority(g.Select(t => AudioQualityTier.FromCodec(t.AudioCodec))));
    }

    public async Task<ArtistAlbums[]> QueryAllAlbums()
    {
        var plexLibrary = await _plexApi.ResolveLibrary();

        // Split a ';'-joined ParentTitle so a collaborative album is credited to each artist, then
        // regroup by the real artist name (matching the split done in QueryAllArtistMetadata).
        return (await _plexApi.GetMusicAlbums(plexLibrary.Key))
            .Where(a => !string.IsNullOrWhiteSpace(a.ParentTitle) && !string.IsNullOrWhiteSpace(a.Title))
            .SelectMany(a => ArtistNames.Split(a.ParentTitle).Select(name => (Name: name, a.Title, a.RatingKey)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistAlbums(
                new ArtistKey(g.Key),
                // Keep each title once (as before), now paired with the Plex item it came from so the
                // merge picker can link the album itself. A repeated title (a second copy of the same
                // record) keeps the first key — either opens the album in Plex.
                g.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new OwnedAlbum(t.First().Title, t.First().RatingKey))
                    .ToArray()))
            .ToArray();
    }
}