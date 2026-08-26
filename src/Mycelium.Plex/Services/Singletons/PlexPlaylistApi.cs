using System.Net;
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
/// </summary>
public class PlexPlaylistApi : IPlexPlaylistApi
{
    private readonly PlexEndpointInfo _endpointInfo;
    private readonly IPlexTokenSource _tokens;
    private readonly PlexAppIdentity _identity;
    private readonly IPlexApi _plexApi;
    private readonly ILogger<PlexPlaylistApi> _logger;
    private readonly HttpClient _httpClient = new();

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

        var playlists = new List<PlexPlaylist>(metadata.Count);
        foreach (var entry in metadata.OfType<JObject>())
        {
            var ratingKey = entry["ratingKey"]?.ToString();
            if (ratingKey is null)
            {
                continue;
            }

            // The listing never carries `content`, whatever it's asked for, so the rules cost a read each.
            var full = await ReadPlaylist(token, ratingKey);
            if (full is not null)
            {
                playlists.Add(full);
            }
        }

        return playlists.ToArray();
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
        HttpMethod method, string path, string token, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, _endpointInfo.BaseUri.TrimEnd('/') + path);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Token", token);
        request.Headers.Add("X-Plex-Product", _identity.Product);
        request.Headers.Add("X-Plex-Client-Identifier", _identity.ClientIdentifier);

        _logger.LogDebug("Plex {Method} {Path}", method, path);
        using var response = await _httpClient.SendAsync(request);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
    }
}
