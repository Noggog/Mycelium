using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Mycelium.Deezer.Models;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>One sampleable track: a title and its ~30s preview MP3.</summary>
public record DeezerPreviewTrack(string Title, string PreviewUrl);

/// <summary>What the SPA needs to sample an artist: a few 30-second previews and a link out.</summary>
/// <param name="Id">Deezer artist id.</param>
/// <param name="ArtistLink">Canonical deezer.com artist page (the "open wholesale" link).</param>
/// <param name="ImageUrl">Deezer's artist photo (largest available), or null if it supplied none.</param>
/// <param name="Tracks">The artist's top previewable tracks (biggest first), possibly empty.</param>
public record DeezerPlayInfo(long Id, string ArtistLink, string? ImageUrl, IReadOnlyList<DeezerPreviewTrack> Tracks);

/// <summary>
/// The outcome of a Deezer lookup, keeping "Deezer says there's no such artist" apart from "Deezer
/// never answered". Both used to arrive as a bare null, and conflating them is what turned a
/// momentary rate-limit into a sticky "Not on Deezer" in the readout: the caller had no way to tell
/// a verdict from a blip, so it cached the blip as the verdict.
/// </summary>
/// <param name="Value">What was found, or null on a miss <em>or</em> an unanswered call.</param>
/// <param name="Unavailable">True when Deezer never answered, so nothing here is evidence.</param>
public record DeezerLookup<T>(T? Value, bool Unavailable) where T : class
{
    /// <summary>Deezer answered, and has no such artist.</summary>
    public static DeezerLookup<T> Missing { get; } = new(null, false);

    /// <summary>Deezer never answered (transport error or rate-limit quota).</summary>
    public static DeezerLookup<T> Unreachable { get; } = new(null, true);

    public static DeezerLookup<T> Found(T value) => new(value, false);
}

/// <summary>What the SPA needs to sample a specific album: its previewable tracks and a link out.</summary>
/// <param name="Id">Deezer album id.</param>
/// <param name="AlbumLink">Canonical deezer.com album page.</param>
/// <param name="Tracks">The album's previewable tracks (track order), possibly empty.</param>
public record DeezerAlbumPlayInfo(long Id, string AlbumLink, IReadOnlyList<DeezerPreviewTrack> Tracks);

/// <summary>
/// Resolves an artist name to Deezer playback info: the artist id, its page link, and a 30-second
/// preview MP3 from the artist's top track. A plain &lt;audio&gt; element plays that preview with no
/// login/cookies/iframe (unlike the embed widget). Results are cached so showing a recommendation
/// card never re-hits Deezer for something already looked up.
/// </summary>
public class DeezerArtistResolver
{
    /// <summary>How many top tracks to offer as previews.</summary>
    private const int TopTrackCount = 5;

    // The artist id is immutable; cache it long. Preview URLs can rotate, so cache them briefly.
    private static readonly DistributedCacheEntryOptions IdCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30),
    };

    // A miss is cached too — a name Deezer genuinely doesn't carry shouldn't re-search on every card —
    // but nowhere near as long as a hit. Deezer's search is not perfectly stable (a name that answers
    // now can answer with nothing under load), and a miss held for 30 days strands the artist as "Not
    // on Deezer" everywhere resolution feeds: the sample player, the discography, the album diff. Half
    // an hour keeps the re-search cheap while letting a bad answer age out on its own.
    private static readonly DistributedCacheEntryOptions MissCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
    };

    // Deezer's preview MP3s are signed urls with a short ~15-minute expiry (hdnea=exp=… token); a
    // cached url older than that yields a dead link the browser rejects (MEDIA_ERR_SRC_NOT_SUPPORTED).
    // Keep the cache well under the token life so served urls are almost always still valid, and let
    // the client force a refresh (below) for the case where a payload goes stale while it's on screen.
    private static readonly DistributedCacheEntryOptions TopTracksCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
    };

    private readonly IDeezerApi _deezer;
    private readonly IDistributedCache _cache;
    private readonly IArtistCatalogRepo _catalog;

    public DeezerArtistResolver(IDeezerApi deezer, IDistributedCache cache, IArtistCatalogRepo catalog)
    {
        _deezer = deezer;
        _cache = cache;
        _catalog = catalog;
    }

    /// <summary>
    /// Full sample/link/image info for an artist name. Pass forceRefresh to bypass the cached (and
    /// possibly expired) preview urls and re-mint them. The result distinguishes "no Deezer match"
    /// from "Deezer didn't answer" so the endpoint can answer 404 vs. 503 — the sample player showing
    /// a permanent "Not on Deezer" for what was really a five-second rate-limit is the bug this
    /// separation exists to prevent.
    /// </summary>
    public async Task<DeezerLookup<DeezerPlayInfo>> ResolvePlayInfo(string artistName, bool forceRefresh = false)
    {
        var lookup = await Lookup(artistName);
        if (lookup.Value is not { } identity)
        {
            return lookup.Unavailable ? DeezerLookup<DeezerPlayInfo>.Unreachable : DeezerLookup<DeezerPlayInfo>.Missing;
        }

        var tracks = await ResolveTopTracks(identity.Id, forceRefresh);
        return DeezerLookup<DeezerPlayInfo>.Found(new DeezerPlayInfo(
            identity.Id,
            identity.Link ?? $"https://www.deezer.com/artist/{identity.Id}",
            identity.ImageUrl,
            tracks));
    }

    /// <summary>The Deezer artist id for a name, or null if Deezer has no match. Cached.</summary>
    public async Task<long?> ResolveArtistId(string artistName) =>
        (await ResolveIdentity(artistName))?.Id;

    /// <summary>
    /// The Deezer artist a name resolves to (id, name, fans, link, photo), or null on no match.
    /// Honors a user override pinned on the catalog (resolved by id, never re-searched); otherwise
    /// takes Deezer's top name-search hit. Every successful resolution is persisted to the catalog
    /// so the id is captured opportunistically (e.g. when an artist is sampled or thumbed-up), and
    /// cached so cards never re-hit Deezer for something already looked up. A search Deezer never
    /// answered also resolves to null, but is never written anywhere — only an answer is evidence.
    /// </summary>
    public async Task<DeezerIdentity?> ResolveIdentity(string artistName) =>
        (await Lookup(artistName)).Value;

    /// <summary>
    /// <see cref="ResolveIdentity"/>, with the reason for a null answer kept: whether Deezer said no
    /// or simply never answered. Callers that <em>record</em> the outcome — the discography diff
    /// persists what it sees — must read this rather than the bare identity.
    /// </summary>
    public async Task<DeezerLookup<DeezerIdentity>> Lookup(string artistName)
    {
        // A sticky "unlinked" decision wins outright: the artist has no Deezer match, so never
        // re-guess by name (that's exactly what produced the wrong link the user detached).
        if (await _catalog.IsDeezerUnlinked(new ArtistKey(artistName)))
        {
            return DeezerLookup<DeezerIdentity>.Missing;
        }

        // A user pin wins next — the whole point is to stop guessing by name.
        var stored = await _catalog.GetDeezer(new ArtistKey(artistName));
        if (stored is { IsOverride: true })
        {
            return DeezerLookup<DeezerIdentity>.Found(stored.Value.Identity);
        }

        var key = NameCacheKey(artistName);
        DeezerIdentity? identity;

        var cached = await _cache.GetStringAsync(key);
        if (cached != null)
        {
            identity = cached.Length == 0 ? null : JsonSerializer.Deserialize<DeezerIdentity>(cached);
        }
        else
        {
            var candidates = await _deezer.SearchArtists(artistName, DeezerApi.SearchCandidates);
            if (candidates is null)
            {
                // Deezer never answered (unreachable, or rate-limited). Unresolved for this call, but
                // nothing is written: caching that would turn a momentary quota blip into a sticky
                // "not on Deezer" for everything downstream, for as long as the entry lives.
                return DeezerLookup<DeezerIdentity>.Unreachable;
            }

            identity = ToIdentity(DeezerApi.PickBestMatch(candidates, artistName));
            await _cache.SetStringAsync(
                key,
                identity is null ? "" : JsonSerializer.Serialize(identity),
                identity is null ? MissCacheOptions : IdCacheOptions);
        }

        // Opportunistic capture onto the catalog (for the Artists page). Done on cache hits too, not
        // just misses — otherwise a warm cache (Redis) means nothing ever lands in the catalog. Skip
        // the write when the catalog already has this id, and never overturn an override (repo guards).
        if (identity != null && stored?.Identity.Id != identity.Id)
        {
            await _catalog.SetDeezerIdentity(new ArtistKey(artistName), identity, isOverride: false);
        }

        return identity is null ? DeezerLookup<DeezerIdentity>.Missing : DeezerLookup<DeezerIdentity>.Found(identity);
    }

    /// <summary>
    /// Just the Deezer photo for an artist name (or null on no match / unreachable), using the same
    /// 30-day name cache as <see cref="ResolveIdentity"/> but skipping the catalog round-trips — for
    /// metadata enrichment of recommended artists that aren't in the library (so never have a pin or
    /// catalog row). A warmed identity cache makes this a pure cache read.
    /// </summary>
    public async Task<string?> ResolveImageUrl(string artistName)
    {
        var key = NameCacheKey(artistName);

        var cached = await _cache.GetStringAsync(key);
        if (cached != null)
        {
            return cached.Length == 0 ? null : JsonSerializer.Deserialize<DeezerIdentity>(cached)?.ImageUrl;
        }

        var candidates = await _deezer.SearchArtists(artistName, DeezerApi.SearchCandidates);
        if (candidates is null)
        {
            // Deezer didn't answer — no photo this pass, and nothing cached (see ResolveIdentity).
            return null;
        }

        var identity = ToIdentity(DeezerApi.PickBestMatch(candidates, artistName));
        await _cache.SetStringAsync(
            key,
            identity is null ? "" : JsonSerializer.Serialize(identity),
            identity is null ? MissCacheOptions : IdCacheOptions);
        return identity?.ImageUrl;
    }

    /// <summary>
    /// Pins an artist to a specific Deezer id (a user correction). Fetches that artist by id,
    /// persists it as a sticky override, and evicts any stale name-resolution. Returns the pinned
    /// identity, or null if Deezer has no artist with that id.
    /// </summary>
    public async Task<DeezerIdentity?> SetOverride(string artistName, long deezerId)
    {
        var identity = ToIdentity(await _deezer.GetArtist(deezerId));
        if (identity is null)
        {
            return null;
        }

        await _catalog.SetDeezerIdentity(new ArtistKey(artistName), identity, isOverride: true);
        await _cache.RemoveAsync(NameCacheKey(artistName));
        return identity;
    }

    /// <summary>Clears a user pin (or unlinked flag) so the artist re-resolves from a name search next time.</summary>
    public async Task ClearOverride(string artistName)
    {
        await _catalog.ClearDeezerOverride(new ArtistKey(artistName));
        await _cache.RemoveAsync(NameCacheKey(artistName));
    }

    /// <summary>
    /// Stickily detaches an artist from Deezer (the artist has no Deezer match). Resolution returns
    /// null thereafter — no name search — until <see cref="ClearOverride"/> re-enables automatic
    /// resolution. Evicts any cached name-resolution so a stale hit can't resurrect the wrong id.
    /// </summary>
    public async Task SetUnlinked(string artistName)
    {
        await _catalog.SetDeezerUnlinked(new ArtistKey(artistName));
        await _cache.RemoveAsync(NameCacheKey(artistName));
    }

    /// <summary>Free-text Deezer artist search for the "Correct association" picker.</summary>
    public async Task<IReadOnlyList<DeezerIdentity>> SearchArtists(string query, int limit) =>
        (await _deezer.SearchArtists(query, limit) ?? Array.Empty<DeezerArtist>())
            .Select(ToIdentity)
            .Where(i => i != null)
            .Select(i => i!)
            .ToArray();

    // v2: the cached value changed shape (CachedArtist -> DeezerIdentity), so bump the key to ignore
    // any stale v1 entries lingering in a persistent cache (Redis) rather than mis-deserializing them.
    private static string NameCacheKey(string artistName) => $"deezer:artist:v2:{artistName.ToLowerInvariant()}";

    private static DeezerIdentity? ToIdentity(DeezerArtist? artist) =>
        artist is null
            ? null
            : new DeezerIdentity(
                artist.id,
                artist.name,
                artist.nb_fan,
                artist.link ?? $"https://www.deezer.com/artist/{artist.id}",
                artist.BestImageUrl);

    /// <summary>
    /// Sample/link info for a specific Deezer album id. The album id is already known (it comes from
    /// the missing-album record), so this never misses — the link is always valid; the track list may
    /// be empty if Deezer has no previews. Previews are cached briefly since they can rotate.
    /// </summary>
    public async Task<DeezerAlbumPlayInfo> ResolveAlbumPlayInfo(long albumId, bool forceRefresh = false)
    {
        var tracks = await ResolveAlbumTracks(albumId, forceRefresh);
        return new DeezerAlbumPlayInfo(albumId, $"https://www.deezer.com/album/{albumId}", tracks);
    }

    private async Task<IReadOnlyList<DeezerPreviewTrack>> ResolveAlbumTracks(long albumId, bool forceRefresh = false)
    {
        var key = $"deezer:albumtracks:{albumId}";

        var cached = forceRefresh ? null : await _cache.GetStringAsync(key);
        if (cached != null)
        {
            return cached.Length == 0
                ? Array.Empty<DeezerPreviewTrack>()
                : JsonSerializer.Deserialize<DeezerPreviewTrack[]>(cached) ?? Array.Empty<DeezerPreviewTrack>();
        }

        var tracks = (await _deezer.GetAlbumTracks(albumId))
            .Where(t => !string.IsNullOrEmpty(t.preview) && !string.IsNullOrEmpty(t.title))
            .Select(t => new DeezerPreviewTrack(t.title!, t.preview!))
            .ToArray();

        await _cache.SetStringAsync(
            key,
            tracks.Length == 0 ? "" : JsonSerializer.Serialize(tracks),
            TopTracksCacheOptions);
        return tracks;
    }

    private async Task<IReadOnlyList<DeezerPreviewTrack>> ResolveTopTracks(long artistId, bool forceRefresh = false)
    {
        var key = $"deezer:toptracks:{artistId}";

        var cached = forceRefresh ? null : await _cache.GetStringAsync(key);
        if (cached != null)
        {
            return cached.Length == 0
                ? Array.Empty<DeezerPreviewTrack>()
                : JsonSerializer.Deserialize<DeezerPreviewTrack[]>(cached) ?? Array.Empty<DeezerPreviewTrack>();
        }

        var tracks = (await _deezer.GetTopTracks(artistId, TopTrackCount))
            .Where(t => !string.IsNullOrEmpty(t.preview) && !string.IsNullOrEmpty(t.title))
            .Select(t => new DeezerPreviewTrack(t.title!, t.preview!))
            .ToArray();

        await _cache.SetStringAsync(
            key,
            tracks.Length == 0 ? "" : JsonSerializer.Serialize(tracks),
            TopTracksCacheOptions);
        return tracks;
    }
}
