using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;
using Mycelium.Deezer.Inputs;
using Mycelium.Deezer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    // Deezer rate-limits at ~50 requests per 5 seconds per IP and answers the overflow with a
    // 200-wrapped "Quota limit exceeded" — which, read as data, is indistinguishable from "nothing
    // found". Opening one brand-new artist's discography is a burst on its own (the album listing,
    // then one /album/{id} per not-yet-owned release, which for a new artist is all of them), so the
    // ceiling is reachable from ordinary browsing, not just the nightly sweep. Pace every call
    // through a rolling window instead of finding the ceiling by hitting it.
    private const int WindowCalls = 40;
    private const int QuotaRetries = 2;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly DeezerEndpointInfo _endpointInfo;
    private readonly ILogger<DeezerApi> _logger;

    // Timestamps of the calls made inside the current window, oldest first.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _recent = new();

    public DeezerApi(DeezerEndpointInfo endpointInfo, ILogger<DeezerApi> logger)
    {
        _httpClient = new HttpClient();
        _endpointInfo = endpointInfo;
        _logger = logger;
    }

    // How many candidates to pull before picking a best match. Deezer's relevance order buries the
    // canonical artist behind collaborations/near-duplicates (e.g. "RJD2 & Supastition" outranks the
    // real "RJD2"), so we look past the top hit to find an exact-name match — one page is plenty.
    // Public so a caller that needs the reached/missed distinction can run the same search itself
    // (see DeezerArtistResolver) and still pick the way this client would.
    public const int SearchCandidates = 25;

    public async Task<DeezerArtist?> SearchArtist(string artistName)
    {
        return PickBestMatch(
            await SearchArtists(artistName, SearchCandidates) ?? Array.Empty<DeezerArtist>(), artistName);
    }

    /// <summary>
    /// Chooses the best artist from a Deezer search result set. Prefers an exact (case-insensitive)
    /// name match, and when several artists share that name (Deezer has many "Rjd2" entries) takes the
    /// most-followed — the canonical act. Only when nothing matches by name does it defer to Deezer's
    /// own relevance order (the first result, its strongest guess).
    /// </summary>
    public static DeezerArtist? PickBestMatch(IReadOnlyList<DeezerArtist> candidates, string artistName)
    {
        var exact = candidates
            .Where(a => string.Equals(a.name?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.nb_fan ?? 0)
            .FirstOrDefault();

        return exact ?? candidates.FirstOrDefault();
    }

    public async Task<DeezerArtist[]?> SearchArtists(string query, int limit)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["q"] = query;
        qs["limit"] = limit.ToString();
        var url = $"{_endpointInfo.BaseUri}/search/artist?{qs}";

        // Null (Deezer never answered) is deliberately not flattened to an empty page here: a caller
        // that persists the outcome must be able to tell a rate-limited call from "no such artist".
        var result = await Get<DeezerArtistList>(url);
        return result?.data.ToArray();
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

    public async Task<DeezerAlbum[]?> GetAlbums(long artistId)
    {
        // limit=300 comfortably covers any discography in one page (Deezer's default page is 25).
        var url = $"{_endpointInfo.BaseUri}/artist/{artistId}/albums?limit=300";
        // Null (Deezer never answered) is deliberately not flattened to an empty page, for the same
        // reason as SearchArtists: the caller persists the diff this feeds, and an unanswered call
        // read as "this artist has no albums" wipes the artist's missing-album rows.
        var result = await Get<DeezerAlbumList>(url);
        return result?.data.ToArray();
    }

    // Deezer's album search pages at 100 and answers for every artist with a name like the one asked
    // for, so the artist's own releases are scattered through the result set rather than sitting at the
    // front: Walk Off The Earth's 154 releases came out of a 206-result search. Five pages is well past
    // where a real artist's tail ends, and bounds the walk if Deezer keeps handing back a next link.
    private const int AlbumSearchPageSize = 100;
    private const int MaxAlbumSearchPages = 5;

    public async Task<DeezerAlbum[]?> SearchArtistAlbums(string artistName)
    {
        // The field-scoped form of Deezer's search grammar: artist:"..." keeps the whole name together
        // as one term instead of matching any album whose title happens to share a word with it.
        var query = Uri.EscapeDataString($"artist:\"{artistName}\"");
        var albums = new List<DeezerAlbum>();
        for (var page = 0; page < MaxAlbumSearchPages; page++)
        {
            // The offset steps by the page size, not by how many rows came back. Search pages are not
            // dense — Deezer answered index=0 of a 230-result search with 87 rows and a next link — and
            // walking by rows received would re-ask for ground already covered and never reach the end.
            var url = $"{_endpointInfo.BaseUri}/search/album"
                      + $"?limit={AlbumSearchPageSize}&index={page * AlbumSearchPageSize}&q={query}";
            var result = await Get<DeezerAlbumList>(url);
            // One unanswered page poisons the whole answer, so say so rather than hand back a short
            // list: the caller can't tell a truncated walk from an artist with nothing else, and it
            // persists the difference. Same reasoning as GetAlbums returning null.
            if (result is null)
            {
                return null;
            }

            albums.AddRange(result.data);
            // Deezer's own "there is more" flag is the only reliable end marker here, precisely because
            // a short page doesn't mean the last page.
            if (result.next is null)
            {
                return albums.ToArray();
            }
        }

        // Fell out of the loop with Deezer still offering more: the walk is bounded, so say so rather
        // than let a short catalog read as the whole one.
        _logger.LogWarning(
            "Deezer album search for \"{Artist}\" hit the {Pages}-page cap at {Count} results; "
            + "later releases may be missing", artistName, MaxAlbumSearchPages, albums.Count);
        return albums.ToArray();
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
        for (var attempt = 0; ; attempt++)
        {
            await Throttle();
            var (value, quotaHit) = await GetOnce<T>(url);
            if (!quotaHit || attempt == QuotaRetries)
            {
                if (quotaHit)
                {
                    _logger.LogWarning("Deezer still rate-limiting after {Retries} retries for {Url}", attempt, url);
                }
                return value;
            }

            // Sit out the rest of the window before trying again. The throttle above keeps us under
            // the ceiling in steady state; this covers the case where something else (a redeploy's
            // cold-start sweep, streamrip downloading from the same IP) already spent the budget.
            _logger.LogInformation("Deezer rate-limited {Url}; retrying in {Delay}", url, Window);
            await Task.Delay(Window);
        }
    }

    /// <summary>
    /// One attempt. Returns the parsed body (null on any failure) plus whether the failure was
    /// Deezer's rate-limit quota specifically — the one failure worth waiting out and retrying.
    /// </summary>
    private async Task<(T? Value, bool QuotaHit)> GetOnce<T>(string url) where T : class
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deezer request failed: {Status} for {Url}", response.StatusCode, url);
                return (null, response.StatusCode == HttpStatusCode.TooManyRequests);
            }

            var body = await response.Content.ReadAsStringAsync();
            // Deezer reports its own failures *inside a 200*: { "error": { "type", "message", "code" } }
            // — most often "Quota limit exceeded" when a burst trips the ~50-calls-per-5s rate limit.
            // Deserialized blindly that binds to a payload with no data, which reads to callers as an
            // authoritative "nothing found", so unwrap the envelope and fail the call instead.
            if (TryReadError(body, out var error))
            {
                _logger.LogWarning("Deezer returned an error for {Url}: {Error}", url, error);
                return (null, IsQuotaError(error));
            }

            return (JsonConvert.DeserializeObject<T>(body), false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deezer request errored for {Url}", url);
            return (null, false);
        }
    }

    // Deezer's quota refusal is code 4. Anchored against the following character so it can't also
    // match 40x/41x codes, which mean something else entirely and are not worth retrying.
    private static readonly Regex QuotaCode = new(@"""code""\s*:\s*4(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// Deezer's quota refusal, by either shape it arrives in: the type/message wording
    /// ("QuotaException" / "Quota limit exceeded"), or error code 4.
    /// </summary>
    private static bool IsQuotaError(string error) =>
        error.Contains("quota", StringComparison.OrdinalIgnoreCase) || QuotaCode.IsMatch(error);

    /// <summary>
    /// Blocks until this call fits inside the rolling window. Deezer's ceiling is ~50 requests per 5
    /// seconds per IP; we pace to <see cref="WindowCalls"/> so bursts from elsewhere in the app (or
    /// streamrip on the same IP) still have room. Held under a gate, so concurrent callers queue in
    /// arrival order rather than all deciding at once that there's room for one more.
    /// </summary>
    private async Task Throttle()
    {
        await _gate.WaitAsync();
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                while (_recent.Count > 0 && now - _recent.Peek() >= Window)
                {
                    _recent.Dequeue();
                }

                if (_recent.Count < WindowCalls)
                {
                    break;
                }

                // Full window — wait for the oldest call in it to age out.
                await Task.Delay(_recent.Peek() + Window - now);
            }

            _recent.Enqueue(DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether a 200 body is one of Deezer's error envelopes. Some successful responses carry an
    /// empty <c>error</c> ([] / {}), so only a populated one counts. A body that doesn't parse throws
    /// out to <see cref="Get{T}"/>'s catch, which is the right answer anyway — the deserializer
    /// wouldn't have made anything of it either.
    /// </summary>
    private static bool TryReadError(string body, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{')
        {
            return false;
        }

        var error = JObject.Parse(body)["error"];
        if (error is null or JValue { Value: null } or JArray { Count: 0 } or JObject { Count: 0 })
        {
            return false;
        }

        message = error.ToString(Formatting.None);
        return true;
    }
}
