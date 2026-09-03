using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Archive;

/// <summary>
/// The collections an archive is built from, and the single read that assembles them into an
/// <see cref="ArchiveInput"/>.
///
/// <para>Shared rather than owned by the nightly snapshot because there are now two consumers — the
/// snapshot, and a per-user takeout (<see cref="TakeoutBuilder"/>) — and a collection added to one
/// but not the other would produce two archives that quietly disagree about what the archive
/// contains.</para>
/// </summary>
public static class ArchiveCollections
{
    /// <summary>
    /// The collections the archive keeps. The ones left out are all re-derivable and would churn:
    /// `relatedArtists` (rebuilt from Deezer/ListenBrainz on a staleness clock), `missingAlbums`
    /// (delete-and-reinsert per artist on every sync), `deezerAlbumArtists` (a memo cache),
    /// `recommendations` (vestigial) and `appSettings` (a UI toggle).
    /// </summary>
    public const string Users = "users";

    public const string PlexLinks = "plexLinks";
    public const string Artists = "artists";
    public const string ArtistVerdicts = "userQueue";
    public const string Purchases = "purchases";
    public const string Blocks = "blockedAlbums";
    public const string MatchOverrides = "albumMatchOverrides";

    /// <summary>
    /// Star ratings, harvested out of Plex by <c>StarHarvester</c>. Read from Mongo like everything
    /// else — the archive deliberately never talks to Plex itself, so it keeps one source and one
    /// failure mode, and a Plex outage can't stop the rest of the snapshot being taken.
    /// </summary>
    public const string TrackRatings = "userTrackRatings";

    /// <summary>Playlists, harvested out of Plex by <c>PlaylistHarvester</c>. Same reasoning.</summary>
    public const string Playlists = "userPlaylists";

    /// <summary>The library's track listing, so an album file can carry a real one.</summary>
    public const string LibraryTracks = "libraryTracks";

    /// <summary>
    /// Just the collections a takeout <em>summary</em> counts: everything a person authored, plus the
    /// artist list the album total is derived from. The rest are left empty.
    ///
    /// <para>Worth having as its own read because of what it leaves out. <see cref="LibraryTracks"/>
    /// is the largest collection in the system — a track per file, tens of thousands of them — and
    /// contributes no number here; the summary is fetched every time somebody opens the page, whereas
    /// the export it describes is built only when somebody asks for it. Keep this in step with
    /// <c>TakeoutBuilder.Summarize</c>: a count drawn from a collection missing here would silently
    /// read zero.</para>
    /// </summary>
    public static async Task<ArchiveInput> ReadCounted(IArchiveDump dump) => new(
        Users: await dump.Dump(Users),
        PlexLinks: [],
        Artists: await dump.Dump(Artists),
        ArtistVerdicts: await dump.Dump(ArtistVerdicts),
        Purchases: await dump.Dump(Purchases),
        Blocks: await dump.Dump(Blocks),
        MatchOverrides: [],
        TrackRatings: await dump.Dump(TrackRatings),
        Playlists: await dump.Dump(Playlists),
        LibraryTracks: []);

    /// <summary>Reads every collection an archive is built from, in one pass.</summary>
    public static async Task<ArchiveInput> Read(IArchiveDump dump) => new(
        await dump.Dump(Users),
        await dump.Dump(PlexLinks),
        await dump.Dump(Artists),
        await dump.Dump(ArtistVerdicts),
        await dump.Dump(Purchases),
        await dump.Dump(Blocks),
        await dump.Dump(MatchOverrides),
        await dump.Dump(TrackRatings),
        await dump.Dump(Playlists),
        await dump.Dump(LibraryTracks));
}
