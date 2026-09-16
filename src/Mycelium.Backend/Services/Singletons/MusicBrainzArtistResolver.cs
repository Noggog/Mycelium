using Microsoft.Extensions.Caching.Distributed;
using Mycelium.Interfaces;
using Mycelium.ListenBrainz.Models;
using Mycelium.ListenBrainz.Services;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// What a name resolution came to. <paramref name="Unreachable"/> means MusicBrainz never answered, so
/// a null <paramref name="Identity"/> says nothing about the artist; otherwise a null identity is a
/// confirmed "no MusicBrainz artist goes by this name" (or the user detached it).
/// </summary>
public record MusicBrainzResolution(MusicBrainzIdentity? Identity, bool Unreachable = false);

/// <summary>
/// Resolves an artist name to its MusicBrainz MBID — the id the ListenBrainz similarity endpoint is
/// keyed by. The counterpart to <see cref="DeezerArtistResolver"/> for the MetaBrainz source: honors
/// a user pin (override) stored on the catalog, otherwise searches by name and accepts only a hit that
/// actually goes by that name (<see cref="MusicBrainzArtistMatch"/>), and keeps the catalog in step
/// with the answer so the Artists-page "Sources" tab can show + correct it.
///
/// Caching matters more here than for Deezer: MusicBrainz is capped at ~1 req/s, so a cold pass over
/// a large library is slow. The MBID is immutable, so resolutions cache for 30 days; a confirmed
/// no-match caches as an empty string so a missing artist isn't re-searched every read.
/// </summary>
public class MusicBrainzArtistResolver
{
    private static readonly DistributedCacheEntryOptions IdCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30),
    };

    private readonly IMusicBrainzApi _musicBrainz;
    private readonly IDistributedCache _cache;
    private readonly IArtistCatalogRepo _catalog;

    public MusicBrainzArtistResolver(IMusicBrainzApi musicBrainz, IDistributedCache cache, IArtistCatalogRepo catalog)
    {
        _musicBrainz = musicBrainz;
        _cache = cache;
        _catalog = catalog;
    }

    /// <summary>
    /// The MusicBrainz artist a name resolves to (MBID, name, disambiguation), or null on no match /
    /// unreachable — see <see cref="Resolve"/> for a caller that needs to tell those apart.
    /// </summary>
    public async Task<MusicBrainzIdentity?> ResolveIdentity(string artistName) =>
        (await Resolve(artistName)).Identity;

    /// <summary>
    /// Resolves a name. Honors a user override pinned on the catalog (resolved by MBID, never
    /// re-searched) and a sticky unlink; otherwise searches by name and accepts only a hit that goes by
    /// it. Answers are cached so repeated reads never re-hit the rate-limited search API;
    /// <paramref name="fresh"/> skips the cache read (the relink pass, which exists because cached
    /// answers may be wrong) but still writes the new answer back.
    ///
    /// <para>The catalog follows the answer either way: a new MBID replaces the stored one, and a
    /// confirmed no-match clears a stored automatic link. Either change also drops the album release
    /// groups resolved under the old MBID, since that lookup was scoped to the old artist. An
    /// unanswered search changes nothing.</para>
    /// </summary>
    public async Task<MusicBrainzResolution> Resolve(string artistName, bool fresh = false)
    {
        var artist = new ArtistKey(artistName);

        // A sticky "unlinked" decision wins outright: the artist has no MusicBrainz match, so never
        // re-guess by name (that's exactly what produced the wrong link the user detached).
        if (await _catalog.IsMusicBrainzUnlinked(artist))
        {
            return new MusicBrainzResolution(null);
        }

        // A user pin wins next — the whole point is to stop guessing by name.
        var stored = await _catalog.GetMusicBrainz(artist);
        if (stored is { IsOverride: true })
        {
            return new MusicBrainzResolution(stored.Value.Identity);
        }

        var key = NameCacheKey(artistName);
        MusicBrainzIdentity? identity;

        var cached = fresh ? null : await _cache.GetStringAsync(key);
        if (cached != null)
        {
            identity = cached.Length == 0 ? null : Deserialize(cached);
        }
        else
        {
            var candidates = await _musicBrainz.SearchArtists(artistName, MusicBrainzArtistMatch.SearchCandidates);
            if (candidates is null)
            {
                // Unanswered: nothing is cached and nothing on the catalog moves, so a rate-limit blip
                // can neither suppress retries for 30 days nor unlink an artist that was fine.
                return new MusicBrainzResolution(null, Unreachable: true);
            }

            identity = ToIdentity(MusicBrainzArtistMatch.Pick(candidates, artistName));
            await _cache.SetStringAsync(key, identity is null ? "" : Serialize(identity), IdCacheOptions);
        }

        // Capture onto the catalog (for the Sources tab). Done on cache hits too, not just misses —
        // otherwise a warm cache (Redis) means nothing ever lands in the catalog. Skip when the catalog
        // already agrees, and never overturn an override (returned above, and the repo guards too).
        var storedMbid = stored?.Identity.Mbid;
        if (identity?.Mbid != storedMbid)
        {
            if (identity != null)
            {
                await _catalog.SetMusicBrainzIdentity(artist, identity, isOverride: false);
            }
            else
            {
                await _catalog.ClearMusicBrainzOverride(artist);
            }

            if (storedMbid != null)
            {
                await _catalog.ClearAlbumReleaseGroups(artist);
            }
        }

        return new MusicBrainzResolution(identity);
    }

    /// <summary>
    /// Pins an artist to a specific MBID (a user correction). Looks that artist up by id, persists it
    /// as a sticky override, and evicts any stale name-resolution. Returns the pinned identity, or
    /// null if MusicBrainz has no artist with that MBID.
    /// </summary>
    public async Task<MusicBrainzIdentity?> SetOverride(string artistName, string mbid)
    {
        var identity = ToIdentity(await _musicBrainz.GetArtist(mbid));
        if (identity is null)
        {
            return null;
        }

        var previous = await _catalog.GetMusicBrainz(new ArtistKey(artistName));
        await _catalog.SetMusicBrainzIdentity(new ArtistKey(artistName), identity, isOverride: true);
        if (previous != null && previous.Value.Identity.Mbid != identity.Mbid)
        {
            // Resolved under the old artist's MBID, so they belong to the old artist.
            await _catalog.ClearAlbumReleaseGroups(new ArtistKey(artistName));
        }
        await _cache.RemoveAsync(NameCacheKey(artistName));
        return identity;
    }

    /// <summary>Clears a user pin (or unlinked flag) so the artist re-resolves from a name search next time.</summary>
    public async Task ClearOverride(string artistName)
    {
        await _catalog.ClearMusicBrainzOverride(new ArtistKey(artistName));
        await _cache.RemoveAsync(NameCacheKey(artistName));
    }

    /// <summary>
    /// Stickily detaches an artist from MusicBrainz (the artist has no MusicBrainz match). Resolution
    /// returns null thereafter — no name search — until <see cref="ClearOverride"/> re-enables
    /// automatic resolution. Evicts any cached name-resolution so a stale hit can't resurrect it.
    /// </summary>
    public async Task SetUnlinked(string artistName)
    {
        await _catalog.SetMusicBrainzUnlinked(new ArtistKey(artistName));
        await _cache.RemoveAsync(NameCacheKey(artistName));
    }

    /// <summary>Free-text MusicBrainz artist search for the "Correct association" picker.</summary>
    public async Task<IReadOnlyList<MusicBrainzIdentity>> SearchArtists(string query, int limit) =>
        (await _musicBrainz.SearchArtists(query, limit) ?? Array.Empty<MusicBrainzArtist>())
            .Select(ToIdentity)
            .Where(i => i != null)
            .Select(i => i!)
            .ToArray();

    // v2: v1 answers were MusicBrainz's top hit whatever its name, so none of them can be trusted.
    private static string NameCacheKey(string artistName) =>
        $"musicbrainz:artist:v2:{artistName.ToLowerInvariant()}";

    private static MusicBrainzIdentity? ToIdentity(MusicBrainzArtist? artist) =>
        artist?.Id is { Length: > 0 }
            ? new MusicBrainzIdentity(artist.Id, artist.Name, artist.Disambiguation)
            : null;

    // The cached value is "mbid\tname\tdisambiguation"; a plain join avoids pulling in a serializer
    // for three fields (an MBID never contains a tab).
    private static string Serialize(MusicBrainzIdentity id) => $"{id.Mbid}\t{id.Name}\t{id.Disambiguation}";

    private static MusicBrainzIdentity Deserialize(string cached)
    {
        var parts = cached.Split('\t');
        return new MusicBrainzIdentity(
            parts[0],
            parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
            parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
    }
}
