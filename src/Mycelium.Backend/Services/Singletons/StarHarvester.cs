using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>How one pass went, for logging and for the dev panel.</summary>
/// <param name="Users">Accounts swept.</param>
/// <param name="Ratings">Rows stored across all of them.</param>
/// <param name="Skipped">Accounts with no Plex link, or whose sweep failed.</param>
/// <param name="Tracks">Tracks in the library listing, refreshed by the same pass.</param>
public record StarHarvestResult(int Users, int Ratings, int Skipped, int Tracks = 0);

/// <summary>
/// Copies every user's Plex song ratings into Mongo, so they exist somewhere other than Plex.
///
/// <para>Star ratings and playlists are the only two things in this system that live *exclusively* on
/// the Plex server — everything else already has a home in Mongo. That makes them exactly what a Plex
/// failure would take with it, and the reason this exists: the metadata archive can only commit what
/// Mongo holds, so anything meant to outlive Plex has to be mirrored here first.</para>
///
/// <para>Ratings are per-Plex-account, so each user is swept through their <em>own</em> linked token —
/// asking with the server's token would record the owner's stars as everyone's. One paged library
/// sweep per account, returning only rated tracks, which in a normal library is a small fraction of
/// the whole.</para>
///
/// <para>Per-user failures are logged and skipped so one bad account can't abort the pass; a failed
/// pass simply retries at the next interval.</para>
/// </summary>
public class StarHarvester
{
    private readonly IUserRepo _users;
    private readonly IPlexLinkRepo _links;
    private readonly IPlexApi _plex;
    private readonly IUserTrackRatingRepo _ratings;
    private readonly ILibraryTrackRepo _tracks;
    private readonly ILogger<StarHarvester> _logger;

    public StarHarvester(
        IUserRepo users,
        IPlexLinkRepo links,
        IPlexApi plex,
        IUserTrackRatingRepo ratings,
        ILibraryTrackRepo tracks,
        ILogger<StarHarvester> logger)
    {
        _users = users;
        _links = links;
        _plex = plex;
        _ratings = ratings;
        _tracks = tracks;
        _logger = logger;
    }

    /// <summary>Sweeps every linked account once. Public so it can be unit-tested without the timer.</summary>
    public async Task<StarHarvestResult> HarvestAll()
    {
        AppUser[] users;
        PlexLibrary library;
        try
        {
            users = await _users.GetAll();
            library = await _plex.ResolveLibrary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Star harvest could not enumerate users/library; will retry next interval");
            return new StarHarvestResult(0, 0, 0, 0);
        }

        // The library's own track listing, read once as the app rather than per user: which songs
        // exist is a fact about the library, where a rating is a fact about a person. The archive needs
        // it so an album file can carry a real track listing instead of only the songs someone rated.
        var trackCount = 0;
        try
        {
            var tracks = await _plex.GetMusicTracks(library.Key);
            trackCount = await _tracks.ReplaceAll(tracks
                .Where(t => !string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.Album))
                .Select(t => new LibraryTrack(t.Artist!, t.Album!, t.Title ?? "", t.TrackNumber, t.File))
                .ToList());
        }
        catch (Exception ex)
        {
            // Non-fatal: the ratings below are the harder thing to reconstruct, so a failed listing
            // must not cost us them too. The previous listing stays in place until the next pass.
            _logger.LogError(ex, "Star harvest could not refresh the library track listing");
        }

        var swept = 0;
        var stored = 0;
        var skipped = 0;

        foreach (var user in users)
        {
            try
            {
                var link = await _links.Get(user.Subject);
                if (link is null)
                {
                    // Deliberately *not* an empty replace. An account with no Plex link has no ratings we
                    // can read, which is not the same as having none — and wiping the last good harvest
                    // would throw away the very copy this exists to keep. Unlinking is not a delete.
                    skipped++;
                    continue;
                }

                var tracks = await _plex.GetRatedTracks(library.Key, link.ServerToken);
                var count = await _ratings.ReplaceForUser(user.Subject, tracks.Select(ToRating).ToList());

                swept++;
                stored += count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Star harvest failed for {User}; skipping to the next user", user.Subject);
                skipped++;
            }
        }

        _logger.LogInformation(
            "Star harvest: {Tracks} track(s) listed; {Ratings} rating(s) across {Users} account(s); {Skipped} skipped",
            trackCount, stored, swept, skipped);
        return new StarHarvestResult(swept, stored, skipped, trackCount);
    }

    /// <summary>
    /// Plex's 0–10 becomes 0–5 stars, which is what it means and what any other system would call it.
    /// The halving is exact, so a half-star survives.
    /// </summary>
    private static TrackRating ToRating(PlexRatedTrack track) => new(
        Artist: track.Artist ?? "",
        Album: track.Album ?? "",
        Title: track.Title ?? "",
        TrackNumber: track.TrackNumber,
        File: track.File,
        Stars: track.UserRating / 2);
}
