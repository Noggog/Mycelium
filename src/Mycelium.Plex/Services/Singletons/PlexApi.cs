using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Mycelium.Plex.Services.Singletons;

public class PlexApi : IPlexApi
{
    private readonly PlexEndpointInfo _endpointInfo;
    private readonly PlexClientInfo _clientInfo;
    private readonly ILogger<PlexApi> _logger;
    private readonly HttpClient httpClient;

    // The server's machineIdentifier is immutable; fetched once and cached for the process lifetime.
    private string? _machineIdentifier;

    public PlexApi(PlexEndpointInfo endpointInfo, PlexClientInfo clientInfo, ILogger<PlexApi> logger)
    {
        _endpointInfo = endpointInfo;
        _clientInfo = clientInfo;
        _logger = logger;
        this.httpClient = new HttpClient();
        this.httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        this.httpClient.DefaultRequestHeaders.Add("X-Plex-Token", clientInfo.Token);
    }

    public async Task<string?> GetMachineIdentifier()
    {
        if (_machineIdentifier != null)
        {
            return _machineIdentifier;
        }

        // The root endpoint's MediaContainer carries the server identity, including machineIdentifier.
        var url = $"{_endpointInfo.BaseUri}/";
        _logger.LogDebug("Plex GetMachineIdentifier: {Url}", url);
        var response = await httpClient.GetStringAsync(url);
        var data = JObject.Parse(response);
        var id = data["MediaContainer"]?["machineIdentifier"]?.ToString();
        // Only cache a real answer so a transient failure doesn't pin a null for the process lifetime.
        if (!string.IsNullOrEmpty(id))
        {
            _machineIdentifier = id;
        }
        return _machineIdentifier;
    }

    public async Task<PlexLibrary[]> GetLibraries()
    {
        string url = $"{_endpointInfo.BaseUri}/library/sections";
        _logger.LogDebug("Plex GetLibraries: {Url}", url);
        var response = await httpClient.GetStringAsync(url);
        var data = JObject.Parse(response);
        return data["MediaContainer"]["Directory"].ToObject<PlexLibrary[]>();
    }

    public async Task<PlexMusicArtist[]> GetMusicArtists(int library)
    {
        // Mood tags come back inline on the bare listing; includeCollections=1 additionally pulls each
        // artist's Collection tags, which only the legacy-collection cleanup in PlexTagMaintenance reads.
        string url = $"{_endpointInfo.BaseUri}/library/sections/{library}/all?includeCollections=1";
        _logger.LogDebug("Plex GetMusicArtists from library {Library}: {Url}", library, url);
        var response = await httpClient.GetStringAsync(url);
        var data = JObject.Parse(response);
        return data["MediaContainer"]["Metadata"].ToObject<PlexMusicArtist[]>();
    }

    /// <summary>
    /// Fetches a single artist by its rating key (the targeted GET mirror of the whole-section
    /// listing). Returns <c>null</c> when the key no longer resolves (e.g. the item was removed or the
    /// library was rebuilt and keys shifted), so callers can fall back to a name scan. The
    /// <c>/library/metadata/{ratingKey}</c> response carries the same inline <c>Mood</c> (and, with
    /// includeCollections=1, <c>Collection</c>) arrays as the section listing, so the tagger can merge the
    /// artist's current tags without a full scan.
    ///
    /// <para>Unlike the section listing — which serializes <c>Guid</c> as a single string — the detail
    /// endpoint returns it as an <em>array</em> of external-id objects (mbid/etc.). That shape collides
    /// with <see cref="PlexMusicArtist.Guid"/> (a string), so we drop the field before deserializing; the
    /// tagger only needs RatingKey/Title/Mood and nothing reads Guid here.</para>
    /// </summary>
    public async Task<PlexMusicArtist?> GetMusicArtist(int ratingKey)
    {
        var url = $"{_endpointInfo.BaseUri}/library/metadata/{ratingKey}?includeCollections=1";
        _logger.LogDebug("Plex GetMusicArtist {RatingKey}: {Url}", ratingKey, url);
        var response = await httpClient.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var data = JObject.Parse(await response.Content.ReadAsStringAsync());
        if (data["MediaContainer"]?["Metadata"] is not JArray metadata
            || metadata.FirstOrDefault() is not JObject item)
        {
            return null;
        }

        // The detail endpoint serializes Guid as an array; PlexMusicArtist.Guid is a string. It's unused
        // on this path, so remove it rather than fight the type mismatch.
        item.Remove("Guid");
        return item.ToObject<PlexMusicArtist>();
    }

    public async Task<PlexMusicAlbum[]> GetMusicAlbums(int library)
    {
        // type=9 is the album metadata type; parentTitle carries the owning artist's name.
        string url = $"{_endpointInfo.BaseUri}/library/sections/{library}/all?type=9";
        _logger.LogDebug("Plex GetMusicAlbums from library {Library}: {Url}", library, url);
        var response = await httpClient.GetStringAsync(url);
        var data = JObject.Parse(response);
        var metadata = data["MediaContainer"]?["Metadata"];
        return metadata?.ToObject<PlexMusicAlbum[]>() ?? Array.Empty<PlexMusicAlbum>();
    }

    /// <summary>
    /// All tracks under an artist via <c>/library/metadata/{ratingKey}/allLeaves</c> — Plex flattens the
    /// artist's albums into one track list. Each track carries <c>userRating</c> (0–10, the token
    /// account's rating; absent when unrated). Returns empty when the key 404s (item removed / keys
    /// shifted on a rebuild) so the rating summary degrades to "no stats" rather than throwing.
    ///
    /// <para>Read as <paramref name="token"/>, not as the app: star ratings are per-Plex-account, so
    /// asking with the server's own token would report the owner's stars to every user alike.</para>
    /// </summary>
    public async Task<PlexTrack[]> GetArtistTracks(int ratingKey, string token)
    {
        var url = $"{_endpointInfo.BaseUri}/library/metadata/{ratingKey}/allLeaves";
        _logger.LogDebug("Plex GetArtistTracks {RatingKey}: {Url}", ratingKey, url);
        using var request = AsToken(HttpMethod.Get, url, token);
        var response = await httpClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<PlexTrack>();
        }

        response.EnsureSuccessStatusCode();
        var data = JObject.Parse(await response.Content.ReadAsStringAsync());
        var metadata = data["MediaContainer"]?["Metadata"];
        return metadata?.ToObject<PlexTrack[]>() ?? Array.Empty<PlexTrack>();
    }

    public async Task<bool> AcceptsToken(string token)
    {
        // The root endpoint is the cheapest thing the server will refuse to answer for a bad token.
        using var request = AsToken(HttpMethod.Get, $"{_endpointInfo.BaseUri}/", token);
        using var response = await httpClient.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        // Anything else unexpected is the server misbehaving, not a verdict on the token — throwing
        // keeps that from being reported to the user as "your token is wrong".
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// A request that asks as <paramref name="token"/> rather than as the app. Setting X-Plex-Token on
    /// the message suppresses the client's default header (HttpClient only fills in defaults a request
    /// hasn't set), which is what keeps a per-user read from being answered for the server owner.
    /// </summary>
    private static HttpRequestMessage AsToken(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Plex-Token", token);
        return request;
    }

    public async Task<PlexRecentlyAddedItem[]> GetRecentlyAdded(int libraryKey, int maxResults = 5)
    {
        string url = $"{_endpointInfo.BaseUri}/library/sections/{libraryKey}/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size={maxResults}";
        var response = await httpClient.GetStringAsync(url);
        var data = JObject.Parse(response);
        return data["MediaContainer"]["Metadata"].ToObject<PlexRecentlyAddedItem[]>();
    }

    /// <summary>
    /// Kicks off a Plex scan of one library section (the empty-args refresh — Plex walks the section's
    /// folders for new/changed media). Fire-and-forget on Plex's side; this just issues the request.
    /// </summary>
    public async Task RefreshLibrary(int libraryKey)
    {
        string url = $"{_endpointInfo.BaseUri}/library/sections/{libraryKey}/refresh";
        _logger.LogDebug("Plex RefreshLibrary {Library}: {Url}", libraryKey, url);
        var response = await httpClient.GetAsync(url); // Plex accepts GET for section refresh
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Edits an artist's Mood set in section <paramref name="library"/>: adds every tag in
    /// <paramref name="add"/> and removes every tag in <paramref name="remove"/> in a single edit.
    /// Mood is where this app's per-user like/dislike verdicts live: of the artist fields Plex will filter
    /// on (genre, mood, style, country, collection), it's the one that tags the artist without creating a
    /// library object — a Collection would show up in the library's Collections tab, which no Plex setting
    /// can hide, and a Label isn't filterable for artists at all.
    /// </summary>
    public Task SetArtistMoods(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove) =>
        SetArtistTags("mood", library, ratingKey, add, remove);

    /// <summary>
    /// The Genre-field twin of <see cref="SetArtistMoods"/>, behind the tag editor. Genres are the
    /// broad buckets Plex shows on an artist ("Rock", "Electronic"); the app also mirrors them into the
    /// catalog so the artist list stays in step without waiting for the next Plex sync.
    /// </summary>
    public Task SetArtistGenres(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove) =>
        SetArtistTags("genre", library, ratingKey, add, remove);

    /// <summary>
    /// The Style-field twin of <see cref="SetArtistMoods"/>, behind the tag editor. Styles are the
    /// finer-grained descriptors Plex hangs under a genre ("Shoegaze", "Post-Punk").
    /// </summary>
    public Task SetArtistStyles(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove) =>
        SetArtistTags("style", library, ratingKey, add, remove);

    /// <summary>
    /// The Collection-field twin of <see cref="SetArtistMoods"/>. Only the cleanup path uses it, to strip
    /// the "&lt;user&gt;_liked"/"_disliked" collections an earlier version of the tagger wrote.
    /// </summary>
    public Task SetArtistCollections(
        int library, int ratingKey, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove) =>
        SetArtistTags("collection", library, ratingKey, add, remove);

    /// <summary>
    /// Edits one tag field (<c>genre</c>/<c>style</c>/<c>mood</c>/<c>collection</c>) on an artist. Plex's tag edit is <b>not</b> a
    /// whole-field replace — listing <c>{field}[i].tag.tag</c> only adds, and a tag is dropped only via the
    /// explicit <c>{field}[].tag.tag-</c> parameter — so callers pass the delta, not the desired final set.
    /// That's what keeps hand-applied tags on the same field (e.g. the "ambient"/"heavy" moods driving
    /// existing smart collections) intact. Removed tags must be spelled exactly as Plex stores them (case
    /// included), so read them off the current item. <c>type=8</c> is the artist metadata type;
    /// <c>{field}.locked=1</c> pins the field so a later refresh won't drop the tags.
    /// </summary>
    private async Task SetArtistTags(
        string field, int library, int ratingKey,
        IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        if (add.Count == 0 && remove.Count == 0)
        {
            return;
        }

        var url = new StringBuilder(
            $"{_endpointInfo.BaseUri}/library/sections/{library}/all?type=8&id={ratingKey}");
        var i = 0;
        foreach (var tag in add)
        {
            url.Append($"&{field}[{i}].tag.tag={Uri.EscapeDataString(tag)}");
            i++;
        }
        if (remove.Count > 0)
        {
            // Plex drops tags only via the "-" suffix param: a comma-separated list, each value escaped.
            var dropped = string.Join(",", remove.Select(Uri.EscapeDataString));
            url.Append($"&{field}[].tag.tag-={dropped}");
        }
        url.Append($"&{field}.locked=1");

        _logger.LogDebug(
            "Plex artist {RatingKey} {Field} edit: +{Add} -{Remove}", ratingKey, field, add.Count, remove.Count);
        var response = await httpClient.PutAsync(url.ToString(), content: null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Resolves the music library to operate on: the one named by <c>PLEX_LIBRARY</c> when it matches,
    /// otherwise the first artist-type library. Logs which path it took. Shared by the catalog reads
    /// and the post-download rescan so they always target the same section.
    /// </summary>
    public async Task<PlexLibrary> ResolveLibrary()
    {
        var plexLibraries = await GetLibraries();
        var preferredPlexLibrary = Environment.GetEnvironmentVariable("PLEX_LIBRARY");
        PlexLibrary? plexLibrary = null;
        if (preferredPlexLibrary == null)
        {
            _logger.LogWarning("PLEX_LIBRARY not set; falling back to the first artist-type library.");
        }
        else if (plexLibraries.FirstOrDefault(it => string.Equals(it.Title, preferredPlexLibrary)) == null)
        {
            _logger.LogWarning(
                "Preferred Plex library {Library} not found; falling back to the first artist-type library.",
                preferredPlexLibrary);
        }
        else
        {
            plexLibrary = plexLibraries.First(it => string.Equals(it.Title, preferredPlexLibrary));
            _logger.LogInformation("Using preferred Plex library {Library}.", plexLibrary.Title);
        }

        if (plexLibrary == null)
        {
            plexLibrary = plexLibraries.Where(it => it.Type == "artist").Take(1).First();
            _logger.LogWarning("Fell back to artist-type Plex library {Library}.", plexLibrary.Title);
        }

        return plexLibrary;
    }
}

public class PlexLibrary
{
    public int Key { get; set; }
    public string Title { get; set; }
    public string Type { get; set; }
}

public class PlexRecentlyAddedItem
{
    public string Title { get; set; }
}

public record PlexMusicArtist
{
    public int RatingKey { get; set; }
    public string Key { get; set; }
    public string Guid { get; set; }
    public string Title { get; set; }

    // Plex returns genre tags inline on the section listing, e.g. "Genre":[{"tag":"Pop/Rock"}].
    public PlexTag[]? Genre { get; set; }

    // Mood tags, returned inline on the section listing like Genre, e.g. "Mood":[{"tag":"Melancholy"}].
    // The per-user like/dislike verdicts live here — filterable by a music smart playlist, and unlike a
    // Collection it doesn't create a library object. Provider-supplied moods share the field, so writes
    // must stay delta-based.
    public PlexTag[]? Mood { get; set; }

    // Style tags — the finer-grained descriptors Plex hangs under a genre ("Shoegaze"), returned
    // inline like Genre and Mood. Read/written by the tag editor only.
    public PlexTag[]? Style { get; set; }

    // Collection memberships, returned inline when includeCollections=1 is set (see GetMusicArtists).
    // Read only by the cleanup that strips the like/dislike collections an earlier tagger wrote.
    public PlexTag[]? Collection { get; set; }

    /// <summary>The artist's current mood tags; empty when it has none.</summary>
    public string[] Moods() =>
        Mood?.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();

    /// <summary>The artist's current genre tags; empty when it has none.</summary>
    public string[] Genres() =>
        Genre?.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();

    /// <summary>The artist's current style tags; empty when it has none.</summary>
    public string[] Styles() =>
        Style?.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();

    /// <summary>The artist's current collection names; empty when it belongs to none.</summary>
    public string[] Collections() =>
        Collection?.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();
}

/// <summary>A Plex tag entry — genres, moods, styles all serialize as <c>{ "tag": "..." }</c>.</summary>
public record PlexTag
{
    public string Tag { get; set; }
}

public record PlexMusicAlbum
{
    public int RatingKey { get; set; }
    public string Title { get; set; }       // album title
    public string ParentTitle { get; set; } // owning artist's name
}

/// <summary>
/// A track ("leaf") returned by <c>allLeaves</c>. <see cref="UserRating"/> is the token account's rating
/// on Plex's 0–10 scale (10 = five stars) and is <c>null</c> for an unrated track — only rated tracks
/// feed the artist rating summary.
/// </summary>
public record PlexTrack
{
    public string Title { get; set; }
    public double? UserRating { get; set; }
}