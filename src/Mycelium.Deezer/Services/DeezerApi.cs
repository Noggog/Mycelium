using System.Web;
using Microsoft.Extensions.Logging;
using Mycelium.Deezer.Inputs;
using Mycelium.Deezer.Models;
using Newtonsoft.Json;

namespace Mycelium.Deezer.Services;

/// <summary>
/// docs: https://developers.deezer.com/api  (keyless, no auth required)
///   search:  GET /search/artist?q={name}   -> { data: [ {id, name, picture_*}, ... ] }
///   related: GET /artist/{id}/related       -> { data: [ {id, name, picture_*}, ... ] }
///
/// Mirrors the existing SpotifyApi/PlexApi convention: own HttpClient, Newtonsoft deserialization,
/// injected ILogger. Unlike those, this guards transport/parse failures and returns empty so a
/// flaky Deezer never takes ingestion down — resilience is the whole point of the persisted graph.
/// </summary>
public class DeezerApi : IDeezerApi
{
    private readonly HttpClient _httpClient;
    private readonly DeezerEndpointInfo _endpointInfo;
    private readonly ILogger<DeezerApi> _logger;

    public DeezerApi(DeezerEndpointInfo endpointInfo, ILogger<DeezerApi> logger)
    {
        _httpClient = new HttpClient();
        _endpointInfo = endpointInfo;
        _logger = logger;
    }

    // How many candidates to pull before picking a best match. Deezer's relevance order buries the
    // canonical artist behind collaborations/near-duplicates (e.g. "RJD2 & Supastition" outranks the
    // real "RJD2"), so we look past the top hit to find an exact-name match — one page is plenty.
    private const int SearchCandidates = 25;

    public async Task<DeezerArtist?> SearchArtist(string artistName)
    {
        return PickBestMatch(await SearchArtists(artistName, SearchCandidates), artistName);
    }

    /// <summary>
    /// Chooses the best artist from a Deezer search result set. Prefers an exact (case-insensitive)
    /// name match, and when several artists share that name (Deezer has many "Rjd2" entries) takes the
    /// most-followed — the canonical act. Only when nothing matches by name does it defer to Deezer's
    /// own relevance order (the first result, its strongest guess).
    /// </summary>
    internal static DeezerArtist? PickBestMatch(IReadOnlyList<DeezerArtist> candidates, string artistName)
    {
        var exact = candidates
            .Where(a => string.Equals(a.name?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.nb_fan ?? 0)
            .FirstOrDefault();

        return exact ?? candidates.FirstOrDefault();
    }

    public async Task<DeezerArtist[]> SearchArtists(string query, int limit)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["limit"] = limit.ToString();
        var url = $"{_endpointInfo.BaseUri}/search/artist?{qs}";

        var result = await Get<DeezerArtistList>(url);
        return result?.data.ToArray() ?? Array.Empty<DeezerArtist>();
    }

    public async Task<DeezerArtist?> GetArtist(long artistId)
    {
        var url = $"{_endpointInfo.BaseUri}/artist/{artistId}";
        return await Get<DeezerArtist>(url);
    }

    public async Task<DeezerArtist[]> GetRelated(long artistId)
    {
        var url = $"{_endpointInfo.BaseUri}/artist/{artistId}/related";
        var result = await Get<DeezerArtistList>(url);
        return result?.data.ToArray() ?? Array.Empty<DeezerArtist>();
    }

    public async Task<DeezerTrack[]> GetTopTracks(long artistId, int limit)
    {
        // Deezer orders /top by popularity, so these come back biggest-first.
        var url = $"{_endpointInfo.BaseUri}/artist/{artistId}/top?limit={limit}";
        var result = await Get<DeezerTrackList>(url);
        return result?.data.ToArray() ?? Array.Empty<DeezerTrack>();
    }

    public async Task<DeezerAlbum[]> GetAlbums(long artistId)
    {
        // limit=300 comfortably covers any discography in one page (Deezer's default page is 25).
        var url = $"{_endpointInfo.BaseUri}/artist/{artistId}/albums?limit=300";
        var result = await Get<DeezerAlbumList>(url);
        return result?.data.ToArray() ?? Array.Empty<DeezerAlbum>();
    }

    public async Task<DeezerAlbum?> GetAlbum(long albumId)
    {
        var url = $"{_endpointInfo.BaseUri}/album/{albumId}";
        return await Get<DeezerAlbum>(url);
    }

    // Deezer serves album tracks 25 at a time. Unpaged, a 33-track compilation silently arrives as 25
    // — and the download verifier compares this count against the files that actually landed, so a
    // short count would hide missing tracks instead of triggering the fallback pass. The cap keeps a
    // malformed next/total pair from looping forever (25 * 20 = 500 tracks, far past any real album).
    private const int MaxTrackPages = 20;

    public async Task<DeezerTrack[]> GetAlbumTracks(long albumId)
    {
        var tracks = new List<DeezerTrack>();
        for (var page = 0; page < MaxTrackPages; page++)
        {
            var url = $"{_endpointInfo.BaseUri}/album/{albumId}/tracks?index={tracks.Count}";
            var result = await Get<DeezerTrackList>(url);
            // A page that fails or comes back empty ends the walk — better a short list than none,
            // since every caller already treats this as best-effort.
            if (result is null || result.data.Count == 0)
            {
                break;
            }

            tracks.AddRange(result.data);
            if (result.next is null || tracks.Count >= result.total)
            {
                break;
            }
        }

        return tracks.ToArray();
    }

    private async Task<T?> Get<T>(string url) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deezer request failed: {Status} for {Url}", response.StatusCode, url);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer request errored for {Url}", url);
            return null;
        }
    }
}
