using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Mycelium.Plex.Services.Smart;

namespace Mycelium.Plex.Services.Singletons;

/// <summary>
/// <see cref="IPlexPlaylistApi"/> over the Plex HTTP API.
///
/// <para><b>Building a smart playlist.</b> The rules travel as a <c>uri=</c> parameter holding a
/// <c>server://…/library/sections/{n}/all?{rules}</c> address, which means the rule query is
/// percent-encoded <em>twice</em> — once by <see cref="PlexFilterSerializer"/> for the operators, and
/// again here when the whole address becomes a parameter value. Getting that wrong is the classic way
/// to end up with a playlist whose filter silently matches nothing.</para>
///
/// <para><b>Artwork.</b> A playlist's poster lives on the ordinary <c>/library/metadata/{key}</c>
/// route, not under <c>/playlists</c> — the rating key is the same one either way. Uploading and
/// selecting are two separate operations there; see <see cref="UploadPlaylistPoster"/>.</para>
/// </summary>
public class PlexPlaylistApi : IPlexPlaylistApi
{
    private readonly PlexEndpointInfo _endpointInfo;
    private readonly IPlexTokenSource _tokens;
    private readonly PlexAppIdentity _identity;
    private readonly IPlexApi _plexApi;
    private readonly ILogger<PlexPlaylistApi> _logger;

    /// <summary>
    /// The timeout is explicit and generous because creating a smart playlist is not a metadata write:
    /// Plex evaluates the rules across the whole music section before it answers, which on a large
    /// library and domestic hardware can run well past the 100 seconds an HttpClient allows by default.
    /// Timing out there is the worst of both worlds — Plex goes on to build the playlist, while this app
    /// reports a failure and never records what it made.
    /// </summary>
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public PlexPlaylistApi(
        PlexEndpointInfo endpointInfo,
        IPlexTokenSource tokens,
        PlexAppIdentity identity,
        IPlexApi plexApi,
        ILogger<PlexPlaylistApi> logger)
    {
        _endpointInfo = endpointInfo;
        _tokens = tokens;
        _identity = identity;
        _plexApi = plexApi;
        _logger = logger;
    }

    /// <summary>
    /// How many playlist reads run at once. The listing costs one read per playlist and someone with a
    /// well-tended library has dozens, which is a visible wait when they run one at a time. Six is
    /// enough to hide nearly all of the latency while staying polite to what is usually a home server
    /// on domestic hardware — this is a page load, not a batch job.
    /// </summary>
    private const int PlaylistReadConcurrency = 6;

    public async Task<PlexPlaylist[]> GetSmartAudioPlaylists(string token)
    {
        // smart=1 narrows the listing server-side, so the per-playlist reads below are only spent on
        // playlists that can have rules at all.
        var listing = await Send(HttpMethod.Get, "/playlists?playlistType=audio&smart=1", token);
        var metadata = listing?["MediaContainer"]?["Metadata"] as JArray;
        if (metadata is null)
        {
            return Array.Empty<PlexPlaylist>();
        }

        var keys = metadata.OfType<JObject>()
            .Select(entry => entry["ratingKey"]?.ToString())
            .Where(key => key is not null)
            .ToArray();

        // The listing never carries `content`, whatever it's asked for, so the rules cost a read each.
        // Results land in a fixed slot rather than being appended, so concurrency doesn't shuffle the
        // order the server gave — callers compare against it and a stable order keeps logs readable.
        var read = new PlexPlaylist?[keys.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, keys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = PlaylistReadConcurrency },
            async (i, _) => read[i] = await ReadPlaylist(token, keys[i]!));

        return read.OfType<PlexPlaylist>().ToArray();
    }

    public async Task<PlexPlaylist[]> GetAudioPlaylists(string token)
    {
        // No smart filter: the archive wants the hand-built playlists too, and those are the ones a
        // rebuild couldn't reproduce.
        var listing = await Send(HttpMethod.Get, "/playlists?playlistType=audio", token);
        var metadata = listing?["MediaContainer"]?["Metadata"] as JArray;
        if (metadata is null)
        {
            return Array.Empty<PlexPlaylist>();
        }

        var keys = metadata.OfType<JObject>()
            .Select(entry => entry["ratingKey"]?.ToString())
            .Where(key => key is not null)
            .ToArray();

        // Same fixed-slot pattern as the smart listing: concurrency for latency, without shuffling the
        // order the server gave.
        var read = new PlexPlaylist?[keys.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, keys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = PlaylistReadConcurrency },
            async (i, _) => read[i] = await ReadPlaylist(token, keys[i]!));

        return read.OfType<PlexPlaylist>().ToArray();
    }

    public async Task<PlexPlaylistItem[]> GetPlaylistItems(string token, string ratingKey)
    {
        var response = await Send(HttpMethod.Get, $"/playlists/{ratingKey}/items", token, allowNotFound: true);
        if (response?["MediaContainer"]?["Metadata"] is not JArray metadata)
        {
            // A playlist that has been deleted between the listing and this read, or one with no tracks.
            return Array.Empty<PlexPlaylistItem>();
        }

        var items = new List<PlexPlaylistItem>(metadata.Count);
        var position = 1;
        foreach (var entry in metadata.OfType<JObject>())
        {
            items.Add(new PlexPlaylistItem(
                Position: position++,
                Artist: entry["grandparentTitle"]?.ToString(),
                Album: entry["parentTitle"]?.ToString(),
                Title: entry["title"]?.ToString(),
                File: entry["Media"]?.FirstOrDefault()?["Part"]?.FirstOrDefault()?["file"]?.ToString()));
        }

        return items.ToArray();
    }

    public async Task<PlexPlaylist> CreateSmartPlaylist(
        string token, string title, int sectionKey, PlexSmartFilter filter)
    {
        var uri = await BuildSectionUri(sectionKey, filter);
        var path = "/playlists"
                   + $"?type=audio&smart=1&title={Uri.EscapeDataString(title)}"
                   + $"&uri={Uri.EscapeDataString(uri)}";

        _logger.LogInformation("Creating smart playlist {Title} over section {Section}", title, sectionKey);
        var response = await Send(HttpMethod.Post, path, token);
        var created = (response?["MediaContainer"]?["Metadata"] as JArray)?.OfType<JObject>().FirstOrDefault();
        if (created is null)
        {
            throw new InvalidOperationException($"Plex accepted the playlist '{title}' but returned no item.");
        }

        return ToPlaylist(created);
    }

    public async Task<PlexPlaylist> UpdateSmartPlaylistFilter(
        string token, string ratingKey, int sectionKey, PlexSmartFilter filter)
    {
        var uri = await BuildSectionUri(sectionKey, filter);
        var path = $"/playlists/{ratingKey}/items?uri={Uri.EscapeDataString(uri)}";

        _logger.LogInformation("Rewriting rules of smart playlist {RatingKey}", ratingKey);
        await Send(HttpMethod.Put, path, token);

        // The update response doesn't echo the playlist, and leafCount changes with the new rules, so
        // read it back rather than reporting stale numbers.
        return await ReadPlaylist(token, ratingKey)
               ?? throw new InvalidOperationException($"Playlist {ratingKey} vanished after its update.");
    }

    public async Task SetPlaylistSummary(string token, string ratingKey, string summary)
    {
        // A playlist's own route, not /library/metadata — the rating key is the same either way, but
        // only /playlists accepts an edit to a playlist's fields. Sending just `summary` leaves the
        // title alone; naming a field at all is what makes Plex write it.
        _logger.LogInformation("Describing smart playlist {RatingKey}", ratingKey);
        await Send(
            HttpMethod.Put,
            $"/playlists/{ratingKey}?summary={Uri.EscapeDataString(summary)}",
            token);
    }

    public async Task UploadPlaylistPoster(
        string token, string ratingKey, Stream image, string contentType)
    {
        var posters = $"/library/metadata/{ratingKey}/posters";

        _logger.LogInformation("Uploading a cover to playlist {RatingKey}", ratingKey);
        using var content = new StreamContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        await Send(HttpMethod.Post, posters, token, content: content);

        // Storing a poster and *showing* it are separate choices in Plex, and whether an upload wins
        // the second one by itself has varied between server versions. Reading the list back and
        // selecting explicitly costs one request and removes the question; an upload Plex already
        // selected needs nothing further.
        var uploaded = LastUpload(await Send(HttpMethod.Get, posters, token));
        if (uploaded is null)
        {
            _logger.LogWarning(
                "Plex accepted a cover for playlist {RatingKey} but doesn't list it", ratingKey);
            return;
        }

        if (uploaded.Value.Selected)
        {
            return;
        }

        await Send(
            HttpMethod.Put,
            $"/library/metadata/{ratingKey}/poster?url={Uri.EscapeDataString(uploaded.Value.Key)}",
            token);
    }

    /// <summary>
    /// The last uploaded poster in a poster listing, and whether it is the selected one — or null when
    /// the listing holds none.
    ///
    /// <para>Only an <c>upload://</c> key is ours: the same listing also carries whatever the agent
    /// found and the auto-generated composite, and selecting one of those would replace the cover we
    /// just set with the mosaic it was meant to displace. "Last" because Plex appends, so the newest
    /// upload is the one this call just made.</para>
    ///
    /// <para>The entries arrive under <c>Metadata</c> on some server versions and <c>Photo</c> on
    /// others — this is the XML <c>&lt;Photo&gt;</c> tag showing through the JSON — so both are read.</para>
    /// </summary>
    private static (string Key, bool Selected)? LastUpload(JObject? listing)
    {
        var container = listing?["MediaContainer"];
        var entries = (container?["Metadata"] ?? container?["Photo"]) as JArray;

        return entries?
            .OfType<JObject>()
            .Select(entry => (
                Key: entry["ratingKey"]?.ToString() ?? "",
                Selected: entry["selected"]?.Value<bool>() ?? false))
            .Where(entry => entry.Key.StartsWith("upload://", StringComparison.Ordinal))
            .Cast<(string Key, bool Selected)?>()
            .LastOrDefault();
    }

    public async Task<IReadOnlyList<PlexTagEntry>> GetSectionTags(int sectionKey, string field, int type)
    {
        // Library metadata, identical for every account — read with the server token.
        var response = await Send(
            HttpMethod.Get, $"/library/sections/{sectionKey}/{field}?type={type}", await _tokens.Current());
        var directory = response?["MediaContainer"]?["Directory"] as JArray;
        if (directory is null)
        {
            return Array.Empty<PlexTagEntry>();
        }

        return directory.OfType<JObject>()
            .Select(d => new PlexTagEntry(d["key"]?.ToString() ?? "", d["title"]?.ToString() ?? ""))
            .Where(t => t.Key.Length > 0 && t.Title.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// The <c>server://</c> address a smart playlist's rules live at. The machine identifier is the
    /// server's own, not the user's — a linked account addresses the same library.
    /// </summary>
    private async Task<string> BuildSectionUri(int sectionKey, PlexSmartFilter filter)
    {
        var machineId = await _plexApi.GetMachineIdentifier()
                        ?? throw new InvalidOperationException(
                            "Plex server is unreachable, so its machine identifier is unknown.");
        var query = PlexFilterSerializer.Serialize(filter);
        return $"server://{machineId}/com.plexapp.plugins.library/library/sections/{sectionKey}/all?{query}";
    }

    private async Task<PlexPlaylist?> ReadPlaylist(string token, string ratingKey)
    {
        var response = await Send(HttpMethod.Get, $"/playlists/{ratingKey}", token, allowNotFound: true);
        var item = (response?["MediaContainer"]?["Metadata"] as JArray)?.OfType<JObject>().FirstOrDefault();
        return item is null ? null : ToPlaylist(item);
    }

    private static PlexPlaylist ToPlaylist(JObject item) => new(
        RatingKey: item["ratingKey"]?.ToString() ?? "",
        Title: item["title"]?.ToString() ?? "",
        Smart: item["smart"]?.Value<bool>() ?? false,
        LeafCount: item["leafCount"]?.Value<int>() ?? 0,
        Content: item["content"]?.ToString());

    /// <summary>
    /// One request carrying <paramref name="token"/> as the acting account. The token is set per request
    /// rather than on the client, because this class serves every linked user from the one instance.
    /// </summary>
    private async Task<JObject?> Send(
        HttpMethod method, string path, string token, bool allowNotFound = false,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, _endpointInfo.BaseUri.TrimEnd('/') + path);
        request.Content = content;
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Token", token);
        request.Headers.Add("X-Plex-Product", _identity.Product);
        request.Headers.Add("X-Plex-Client-Identifier", _identity.ClientIdentifier);

        _logger.LogDebug("Plex {Method} {Path}", method, path);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No answer at all. Distinguished from a refusal because Plex may have carried out the
            // write regardless — a create that times out still leaves a playlist behind.
            throw new PlexRequestException(method, path, status: null, detail: ex.Message, inner: ex);
        }

        using (response)
        {
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // Plex answers errors in HTML as often as not, so the body is only worth repeating as a
                // short excerpt — enough to tell "unauthorized" from "invalid filter" without pasting a
                // page of markup into the browser.
                throw new PlexRequestException(method, path, response.StatusCode, Excerpt(body));
            }

            return string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
        }
    }

    /// <summary>The first line of a response body, capped — a hint at what Plex objected to.</summary>
    private static string? Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var line = body.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
