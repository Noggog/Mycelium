namespace Mycelium.Interfaces;

/// <summary>
/// Local store of the artists known to exist in the (shared) Plex library.
/// This is the source of truth for daily reads — the Plex server is only touched
/// by the sync job that calls <see cref="SyncFromLibrary"/>.
/// </summary>
public interface IArtistCatalogRepo
{
    /// <summary>Artists currently present in the library, ordered by name.</summary>
    Task<CatalogArtist[]> GetAllPresent();

    /// <summary>
    /// Upserts the catalog from a Plex pull: every supplied artist is marked present
    /// with <paramref name="syncedAt"/>; any artist not seen in this sync is marked absent
    /// (kept, not deleted, so taste state can still reference it).
    /// </summary>
    Task<CatalogSyncResult> SyncFromLibrary(IReadOnlyList<ArtistMetadata> artists, DateTimeOffset syncedAt);

    /// <summary>
    /// Fills in <c>ArtistImageUrl</c> for artists already in the catalog (e.g. from a Deezer
    /// ingestion pass, since the Plex sync supplies no images). Only artists that already exist
    /// are touched — this never creates phantom catalog entries for artists outside the library,
    /// and only sets the image when one is supplied. Returns the number of docs updated.
    /// </summary>
    Task<int> BackfillImages(IReadOnlyList<ArtistMetadata> artists);

    /// <summary>
    /// Replaces the stored genre tags for one artist — the mirror of a user genre edit made in Plex,
    /// so the artist list reflects it without waiting for the next catalog sync. Only touches artists
    /// already present (IsUpsert=false); the next sync overwrites this from Plex either way.
    /// </summary>
    Task SetGenres(ArtistKey artist, IReadOnlyList<string> genres);

    /// <summary>
    /// Stores the owned album titles for each artist (from the same Plex pull as the artist list),
    /// so the missing-album diff can run against the local catalog. Only touches artists already
    /// present — never creates phantom entries.
    /// </summary>
    Task SyncAlbums(IReadOnlyList<ArtistAlbums> artistAlbums);

    /// <summary>
    /// The owned album titles per artist, keyed by artist name (case-insensitive). Used by the
    /// missing-album diff and to hide ratings for albums that have since been acquired.
    /// </summary>
    Task<Dictionary<string, HashSet<string>>> GetOwnedAlbums();

    /// <summary>
    /// The Plex rating key of each owned album title, for the named artists only — outer key artist,
    /// inner key album title, both case-insensitive. Stored by the same sync as the titles; artists
    /// (or albums) whose keys predate that sync are simply absent, so callers must treat a miss as
    /// "no link available". Used to deep link a merge suggestion into Plex.
    /// </summary>
    Task<Dictionary<string, Dictionary<string, int>>> GetAlbumPlexRatingKeys(IReadOnlyCollection<string> artists);

    /// <summary>
    /// Names of present catalog artists that encode multiple artists joined by ';' (a Plex
    /// multi-value artifact, e.g. "Nina Simone;Hot Chip") — candidates for cleanup.
    /// </summary>
    Task<string[]> FindCombinedArtistNames();

    /// <summary>
    /// Splits a ';'-joined catalog artist into one present doc per <paramref name="parts"/> name
    /// (each inheriting the combined doc's albums), then deletes the combined doc. Idempotent:
    /// parts that already exist keep their data and just absorb any albums.
    /// </summary>
    Task SplitCombinedArtist(string combinedName, IReadOnlyList<string> parts, DateTimeOffset syncedAt);

    /// <summary>
    /// The Plex rating key(s) the artist resolves to (a name can map to several Plex items via
    /// ';'-joined collaborator titles), or empty when the artist isn't cataloged / not yet captured.
    /// Lets the tagger target the exact Plex item(s) instead of scanning the whole library.
    /// </summary>
    Task<IReadOnlyList<int>> GetPlexRatingKeys(ArtistKey artist);

    /// <summary>
    /// The stored Deezer identity for an artist plus whether it's a sticky user override, or null
    /// if the artist isn't cataloged or has never been resolved.
    /// </summary>
    Task<(DeezerIdentity Identity, bool IsOverride)?> GetDeezer(ArtistKey artist);

    /// <summary>
    /// Persists the Deezer identity an artist resolved to. <paramref name="isOverride"/> marks a
    /// user pin (resolved by id, never auto-changed). Opportunistic callers pass false and must not
    /// clobber an existing override — those writes are ignored for overridden rows. Never creates
    /// entries for artists outside the catalog (IsUpsert=false).
    /// </summary>
    Task SetDeezerIdentity(ArtistKey artist, DeezerIdentity identity, bool isOverride);

    /// <summary>
    /// Drops the override (and any "unlinked" flag) and clears the stored Deezer fields for an artist,
    /// so the next resolution re-derives the identity from a name search.
    /// </summary>
    Task ClearDeezerOverride(ArtistKey artist);

    /// <summary>Whether the artist is stickily detached from Deezer (no match — never auto-resolve).</summary>
    Task<bool> IsDeezerUnlinked(ArtistKey artist);

    /// <summary>
    /// Stickily detaches an artist from Deezer: clears the stored id and marks it "unlinked" so
    /// resolution returns null (no name search) until the user re-enables automatic resolution.
    /// </summary>
    Task SetDeezerUnlinked(ArtistKey artist);

    /// <summary>
    /// The stored MusicBrainz identity for an artist plus whether it's a sticky user override, or
    /// null if the artist isn't cataloged or has never been resolved. Mirrors <see cref="GetDeezer"/>.
    /// </summary>
    Task<(MusicBrainzIdentity Identity, bool IsOverride)?> GetMusicBrainz(ArtistKey artist);

    /// <summary>
    /// Persists the MusicBrainz identity an artist resolved to. <paramref name="isOverride"/> marks a
    /// user pin (resolved by MBID, never auto-changed). Opportunistic callers pass false and must not
    /// clobber an existing override. Never creates entries for artists outside the catalog.
    /// </summary>
    Task SetMusicBrainzIdentity(ArtistKey artist, MusicBrainzIdentity identity, bool isOverride);

    /// <summary>
    /// Drops the override (and any "unlinked" flag) and clears the stored MusicBrainz fields for an
    /// artist, so the next resolution re-derives the MBID from a name search.
    /// </summary>
    Task ClearMusicBrainzOverride(ArtistKey artist);

    /// <summary>Whether the artist is stickily detached from MusicBrainz (no match — never auto-resolve).</summary>
    Task<bool> IsMusicBrainzUnlinked(ArtistKey artist);

    /// <summary>
    /// Stickily detaches an artist from MusicBrainz: clears the stored MBID and marks it "unlinked"
    /// so resolution returns null (no name search) until the user re-enables automatic resolution.
    /// </summary>
    Task SetMusicBrainzUnlinked(ArtistKey artist);
}

public record CatalogSyncResult(int Upserted, int MarkedAbsent, int TotalPresent);
