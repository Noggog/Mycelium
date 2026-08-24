using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// Summarises <em>one user's</em> per-song Plex ratings for one artist — highest, lowest and average
/// across the songs they've actually rated — for the discovery readout. Plex only has songs for artists
/// already in the library, so an artist the catalog has no Plex rating keys for reports
/// <see cref="ArtistRatingStats.Present"/> false and the UI shows nothing. Ratings come back on Plex's
/// 0–10 scale; we halve them to the 0–5 stars the user sees in Plex. A name can map to several Plex
/// rating keys (split collaborators / recurring names), so tracks are unioned across all of them.
/// Auto-registers via the assembly scan, like <see cref="LibrarySourcesService"/>.
///
/// <para><b>Whose ratings.</b> Star ratings are per-Plex-account, so every read here goes through the
/// asking user's own linked token (<see cref="IPlexLinkRepo"/>). Reading with the app's server token
/// instead — which is what this did originally — reports the server owner's stars to everyone, so a
/// second user sees the owner's taste labelled as their own. A user with no Plex account connected has
/// no ratings to summarise and gets <see cref="ArtistRatingStats.Present"/> false, the same "nothing to
/// show" the UI already hides.</para>
/// </summary>
public class ArtistRatingStatsService
{
    private readonly IArtistCatalogRepo _catalog;
    private readonly IPlexApi _plex;
    private readonly IPlexLinkRepo _links;
    private readonly ILogger<ArtistRatingStatsService> _logger;

    public ArtistRatingStatsService(
        IArtistCatalogRepo catalog,
        IPlexApi plex,
        IPlexLinkRepo links,
        ILogger<ArtistRatingStatsService> logger)
    {
        _catalog = catalog;
        _plex = plex;
        _links = links;
        _logger = logger;
    }

    /// <summary>Nothing to show: not in Plex, or nobody's account to read it as.</summary>
    private static ArtistRatingStats Absent(ArtistKey artist) =>
        new(artist, Present: false, null, null, null, RatedCount: 0, TrackCount: 0);

    /// <summary>
    /// The asking user's own ratings, resolving their linked Plex account first. Answers "nothing to
    /// show" when they haven't connected one — there is no sensible fallback, since the only other
    /// token available is the server owner's and that is precisely the wrong answer.
    /// </summary>
    public async Task<ArtistRatingStats> ForUser(string subject, ArtistKey artist)
    {
        var link = await _links.Get(subject);
        return link is null ? Absent(artist) : await ForToken(link.ServerToken, artist);
    }

    /// <summary>
    /// The same summary for a token already in hand — the background sweep resolves each user's link
    /// once and then walks their whole thumbed list, rather than re-reading it per artist.
    /// </summary>
    public async Task<ArtistRatingStats> ForToken(string serverToken, ArtistKey artist)
    {
        var keys = await _catalog.GetPlexRatingKeys(artist);
        if (keys.Count == 0)
        {
            // Not in Plex (e.g. a brand-new recommended artist) — there are no songs to summarise.
            return Absent(artist);
        }

        var tracks = new List<PlexTrack>();
        foreach (var key in keys)
        {
            try
            {
                tracks.AddRange(await _plex.GetArtistTracks(key, serverToken));
            }
            catch (Exception ex)
            {
                // A flaky/unreachable Plex shouldn't fail the readout: report presence without stats.
                _logger.LogWarning(ex, "Couldn't fetch Plex tracks for {Artist} (key {Key})", artist.ArtistName, key);
            }
        }

        // Plex leaves an unrated song's userRating null (some server versions report 0); count only real
        // ratings, and convert the 0–10 scale to the 0–5 stars shown in the Plex UI.
        var ratings = tracks
            .Where(t => t.UserRating is > 0)
            .Select(t => t.UserRating!.Value / 2.0)
            .ToArray();

        if (ratings.Length == 0)
        {
            return new ArtistRatingStats(artist, Present: true, null, null, null, RatedCount: 0, TrackCount: tracks.Count);
        }

        return new ArtistRatingStats(
            artist,
            Present: true,
            Highest: ratings.Max(),
            Lowest: ratings.Min(),
            Average: Math.Round(ratings.Average(), 2),
            RatedCount: ratings.Length,
            TrackCount: tracks.Count);
    }
}
