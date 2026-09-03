using System.Web;
using Microsoft.Extensions.Logging;
using Mycelium.ListenBrainz.Inputs;
using Mycelium.ListenBrainz.Models;
using Newtonsoft.Json;

namespace Mycelium.ListenBrainz.Services;

/// <summary>
/// docs: https://musicbrainz.org/doc/MusicBrainz_API  (keyless)
///   search: GET /ws/2/artist?query={name}&amp;fmt=json&amp;limit=1 -> { artists: [ {id, name, score}, ... ] }
///
/// Mirrors <c>DeezerApi</c>: own HttpClient, Newtonsoft, injected ILogger, returns null on any
/// failure so ingestion survives a flaky upstream. Adds two MusicBrainz-specific requirements: a
/// descriptive User-Agent (anonymous agents get throttled to near-nothing) and a self-imposed
/// 1 request/second cap — MusicBrainz declines (HTTP 503) callers that exceed it. The rate gate
/// serializes every request through this client, which is exactly the throughput we want.
/// </summary>
public class MusicBrainzApi : IMusicBrainzApi
{
    /// <summary>MusicBrainz's published limit is ~1 req/s; pad slightly to stay safely under.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _httpClient;
    private readonly ListenBrainzEndpointInfo _endpointInfo;
    private readonly ILogger<MusicBrainzApi> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public MusicBrainzApi(ListenBrainzEndpointInfo endpointInfo, ILogger<MusicBrainzApi> logger)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(endpointInfo.UserAgent);
        _endpointInfo = endpointInfo;
        _logger = logger;
    }

    public async Task<MusicBrainzArtist?> SearchArtist(string artistName)
    {
        // Results come back in descending search-score order; the first is the strongest match.
        return (await SearchArtists(artistName, 1)).FirstOrDefault();
    }

    public async Task<MusicBrainzArtist[]> SearchArtists(string query, int limit)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["query"] = query;
        qs["fmt"] = "json";
        qs["limit"] = limit.ToString();
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/artist?{qs}";

        var result = await Get<MusicBrainzSearchResult>(url);
        return result?.Artists.ToArray() ?? Array.Empty<MusicBrainzArtist>();
    }

    public async Task<MusicBrainzArtist?> GetArtist(string mbid)
    {
        // Lookup-by-id returns the artist object directly (no search envelope).
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/artist/{mbid}?fmt=json";
        return await Get<MusicBrainzArtist>(url);
    }

    /// <summary>
    /// The strongest release group for (artist, title). Two guards on top of the search itself, both
    /// because a wrong MBID is worse than no MBID: the query is scoped to the artist's own MBID, and
    /// a hit whose title doesn't actually match the one asked for is discarded.
    /// </summary>
    public async Task<MusicBrainzReleaseGroup?> SearchReleaseGroup(string artistMbid, string title)
    {
        if (string.IsNullOrWhiteSpace(artistMbid) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Lucene syntax. The title is quoted so that punctuation and spaces in it are a phrase rather
        // than a set of loose terms, and any quote inside it is dropped — escaping it would be
        // correct too, but a title containing a double quote is vanishingly rare and dropping it
        // cannot produce a malformed query.
        var phrase = title.Replace("\"", " ").Trim();
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["query"] = $"arid:{artistMbid} AND releasegroup:\"{phrase}\"";
        qs["fmt"] = "json";
        qs["limit"] = "5";
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/release-group?{qs}";

        var result = await Get<MusicBrainzReleaseGroupSearchResult>(url);
        return Pick(result?.ReleaseGroups, phrase);
    }

    /// <summary>
    /// The hit that actually is the album asked for, or null.
    ///
    /// <para>Separated from the request because it is the judgement call, not the plumbing.
    /// MusicBrainz scores loosely: an act's <em>other</em> records come back as partial matches on a
    /// shared word, so taking the top hit blindly would file <i>OK Computer</i> under <i>Kid A</i> —
    /// and unlike a missing id, a wrong one is invisible and permanent. Only an exact title match
    /// (case- and whitespace-insensitive) is accepted; everything else is a miss, which costs an id
    /// we never had.</para>
    /// </summary>
    public static MusicBrainzReleaseGroup? Pick(
        IEnumerable<MusicBrainzReleaseGroup>? candidates, string title) =>
        candidates?.FirstOrDefault(g =>
            g.Id is { Length: > 0 }
            && string.Equals(g.Title?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<T?> Get<T>(string url) where T : class
    {
        await Throttle();
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz request failed: {Status} for {Url}", response.StatusCode, url);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz request errored for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Block until at least <see cref="MinInterval"/> has elapsed since the previous request. Held
    /// across the whole call site so requests through this client never overlap or burst.
    /// </summary>
    private async Task Throttle()
    {
        await _gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextAllowed)
            {
                await Task.Delay(_nextAllowed - now);
            }
            _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
        }
        finally
        {
            _gate.Release();
        }
    }
}
