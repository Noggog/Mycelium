using System.Net;
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
/// descriptive User-Agent (anonymous agents get throttled to near-nothing) and MusicBrainz's 1
/// request/second limit, which <see cref="MusicBrainzGate"/> enforces for every request this client
/// makes — including waiting out, and retrying, a 503.
/// </summary>
public class MusicBrainzApi : IMusicBrainzApi
{
    /// <summary>MusicBrainz's largest page for a browse.</summary>
    private const int BrowsePageSize = 100;

    /// <summary>
    /// Pages a browse will walk before giving up on it (10,000 rows). Far beyond any band; an artist
    /// past it (a classical composer with thousands of releases) answers null rather than a partial
    /// list, which a caller would otherwise read as the whole catalogue.
    /// </summary>
    private const int MaxBrowsePages = 100;

    /// <summary>
    /// A request that hasn't answered by now isn't going to. Without it HttpClient waits 100 seconds —
    /// holding the gate, and so every other MusicBrainz request, the whole time.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly ListenBrainzEndpointInfo _endpointInfo;
    private readonly MusicBrainzGate _gate;
    private readonly ILogger<MusicBrainzApi> _logger;

    public MusicBrainzApi(ListenBrainzEndpointInfo endpointInfo, MusicBrainzGate gate, ILogger<MusicBrainzApi> logger)
    {
        _httpClient = new HttpClient { Timeout = RequestTimeout };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(endpointInfo.UserAgent);
        _endpointInfo = endpointInfo;
        _gate = gate;
        _logger = logger;
    }

    public async Task<MusicBrainzArtist[]?> SearchArtists(string query, int limit)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["query"] = query;
        qs["fmt"] = "json";
        qs["limit"] = limit.ToString();
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/artist?{qs}";

        var result = await Get<MusicBrainzSearchResult>(url);
        return result?.Artists.ToArray();
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

    public async Task<MusicBrainzReleaseGroup[]?> BrowseReleaseGroups(string artistMbid)
    {
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/release-group?artist={Uri.EscapeDataString(artistMbid)}&fmt=json";
        return await Browse<MusicBrainzReleaseGroupBrowse, MusicBrainzReleaseGroup>(
            url, page => page.ReleaseGroups, page => page.Count);
    }

    public async Task<MusicBrainzRelease[]?> BrowseReleases(string artistMbid)
    {
        // Official releases of the types that can be downloaded as a record. Unfiltered, a band with a
        // long live history is mostly bootlegs (111 of Iron Maiden's 338 release groups are Live), and
        // every hundred of those is another second in the queue.
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["artist"] = artistMbid;
        qs["inc"] = "release-groups url-rels";
        qs["status"] = "official";
        qs["type"] = "album|ep|single";
        qs["fmt"] = "json";
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/release?{qs}";
        return await Browse<MusicBrainzReleaseBrowse, MusicBrainzRelease>(
            url, page => page.Releases, page => page.Count);
    }

    public async Task<MusicBrainzUrl?> LookupUrl(string resource)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["resource"] = resource;
        qs["inc"] = "artist-rels release-rels";
        qs["fmt"] = "json";
        var url = $"{_endpointInfo.MusicBrainzBaseUri}/ws/2/url?{qs}";

        var (answered, result) = await Fetch<MusicBrainzUrl>(url);
        if (!answered)
        {
            return null;
        }

        // A 404 is MusicBrainz answering "nothing links there", which is an answer, not a failure.
        return result ?? new MusicBrainzUrl { Resource = resource };
    }

    /// <summary>
    /// Walks every page of a browse. Null if any page goes unanswered: a short list is
    /// indistinguishable from "that's everything", and callers persist the difference.
    /// </summary>
    private async Task<TItem[]?> Browse<TPage, TItem>(
        string url, Func<TPage, List<TItem>> rows, Func<TPage, int> total)
        where TPage : class
    {
        var all = new List<TItem>();
        for (var page = 0; page < MaxBrowsePages; page++)
        {
            var (answered, result) = await Fetch<TPage>($"{url}&limit={BrowsePageSize}&offset={all.Count}");
            if (!answered)
            {
                return null;
            }

            // Browsing an MBID MusicBrainz doesn't have (404) is an answer: it has nothing under it.
            if (result is null)
            {
                return all.ToArray();
            }

            var got = rows(result);
            all.AddRange(got);
            if (got.Count == 0 || all.Count >= total(result))
            {
                return all.ToArray();
            }
        }

        _logger.LogWarning("MusicBrainz browse passed {Pages} pages without finishing; giving up on {Url}",
            MaxBrowsePages, url);
        return null;
    }

    private async Task<T?> Get<T>(string url) where T : class => (await Fetch<T>(url)).Value;

    /// <summary>
    /// One request through the gate. <c>Answered</c> is false when MusicBrainz never gave an answer —
    /// a transport error, a timeout, or still refusing after the gate's retries. A 404 <em>is</em> an
    /// answer (there is no such thing), and comes back answered with a null value.
    /// </summary>
    private async Task<(bool Answered, T? Value)> Fetch<T>(string url) where T : class
    {
        try
        {
            return await _gate.Run(async () =>
            {
                using var response = await _httpClient.GetAsync(url);
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                {
                    _logger.LogInformation("MusicBrainz rate-limited {Url}; the gate will back off and retry", url);
                    throw new MusicBrainzBusyException(RetryAfter(response));
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (true, (T?)null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MusicBrainz request failed: {Status} for {Url}", response.StatusCode, url);
                    return (false, (T?)null);
                }

                var body = await response.Content.ReadAsStringAsync();
                return (true, JsonConvert.DeserializeObject<T>(body));
            });
        }
        catch (MusicBrainzBusyException)
        {
            _logger.LogWarning("MusicBrainz still rate-limiting after {Retries} retries for {Url}",
                MusicBrainzGate.MaxRetries, url);
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz request errored for {Url}", url);
            return (false, null);
        }
    }

    /// <summary>The wait a 503 asked for, in either of the forms Retry-After comes in.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
}
