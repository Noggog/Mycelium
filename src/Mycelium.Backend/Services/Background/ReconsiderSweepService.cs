using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Background;

/// <summary>
/// Periodically re-reads the user's Plex song ratings for every band they've thumbed and flags the
/// ones the ratings contradict, in both directions (see <see cref="ReconsiderPolicy"/>): a dislike the
/// ratings rate highly becomes a "second chance" card, a like they rate poorly a "second thoughts" one.
/// Those flagged rows are what the two discovery categories serve, so each feed is a single Mongo read:
/// all the judgement happens here, out of band.
///
/// The point is to re-litigate verdicts made years ago, so a slow cadence is the feature, not a
/// compromise — a thumb isn't second-guessed seconds after it's made, and a rating made in Plex today
/// surfaces on the next weekly pass. Each pass also *withdraws* flags that no longer hold (the user
/// rated more songs down, say), so the categories can't drift out of sync with the ratings.
///
/// Per-user failures are logged and skipped so one bad user doesn't abort the pass; a failed pass
/// simply retries at the next interval.
/// </summary>
public class ReconsiderSweepService : BackgroundService
{
    private readonly IUserQueueRepo _queue;
    private readonly ILibraryProvider _library;
    private readonly ArtistRatingStatsService _ratings;
    private readonly ReconsiderPolicy _policy;
    private readonly JitterPolicy _jitter;
    private readonly ILogger<ReconsiderSweepService> _logger;

    public ReconsiderSweepService(
        IUserQueueRepo queue,
        ILibraryProvider library,
        ArtistRatingStatsService ratings,
        ReconsiderPolicy policy,
        JitterPolicy jitter,
        ILogger<ReconsiderSweepService> logger)
    {
        _queue = queue;
        _library = library;
        _ratings = ratings;
        _policy = policy;
        _jitter = jitter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _jitter.RunPeriodic(_policy.StartupDelay, _policy.Interval, SweepAll, stoppingToken);

    /// <summary>Sweeps every user once. Public so it can be unit-tested without the timer.</summary>
    public async Task SweepAll()
    {
        string[] userIds;
        // Owned artist -> its catalog art. Only owned artists have songs in Plex to have been rated, so
        // an artist the library doesn't hold (a rejected recommendation, or a like still on the to-buy
        // list) can't produce a contradicting signal. The art comes along because an artist rated
        // straight from the library has none on its queue row, and stamping it while we're here keeps
        // serving the feed to a single query.
        var owned = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            userIds = await _queue.GetAllUserIds();
            foreach (var artist in await _library.GetAllArtistMetadata())
            {
                // Indexer, not ToDictionary: two catalog rows differing only by case would make the
                // latter throw, and which of the pair wins doesn't matter (we only want the art).
                owned[artist.ArtistKey.ArtistName] = artist.ArtistImageUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconsider sweep could not enumerate users/library; will retry next interval");
            return;
        }

        // Plex ratings hang off the single shared server token, not off the app user, so two users who
        // both rejected the same band see identical numbers. Resolve each artist once per pass.
        var statsByArtist = new Dictionary<string, ArtistRatingStats>(StringComparer.OrdinalIgnoreCase);
        var flagged = 0;
        var withdrawn = 0;

        foreach (var userId in userIds)
        {
            try
            {
                // Both directions off the same per-artist stats: a dislike the ratings praise, a like
                // they pan. Confirmed verdicts (the same thumb given twice) never come back from here.
                foreach (var verdict in new[] { DiscoveryStatus.Disliked, DiscoveryStatus.Liked })
                {
                    foreach (var rated in await _queue.GetUnconfirmedVerdicts(userId, verdict))
                    {
                        var name = rated.Artist.ArtistName;
                        if (!owned.TryGetValue(name, out var art))
                        {
                            continue;
                        }

                        if (!statsByArtist.TryGetValue(name, out var stats))
                        {
                            statsByArtist[name] = stats = await _ratings.Get(rated.Artist);
                        }

                        var signal = _policy.Contradicts(stats, verdict)
                            ? new ReconsiderSignal(stats.Average!.Value, stats.RatedCount, stats.TrackCount)
                            : null;

                        // Records compare by value, so this skips the write whenever nothing changed —
                        // including the steady state where the same artists stay flagged week after week.
                        if (signal == rated.Reconsider)
                        {
                            continue;
                        }

                        await _queue.SetReconsider(userId, name, verdict, signal, imageUrl: art);
                        if (signal is null)
                        {
                            withdrawn++;
                        }
                        else
                        {
                            flagged++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconsider sweep failed for {User}; skipping to the next user", userId);
            }
        }

        _logger.LogInformation(
            "Reconsider sweep weighed {Artists} artist(s) across {Users} user(s): {Flagged} flagged, {Withdrawn} withdrawn",
            statsByArtist.Count, userIds.Length, flagged, withdrawn);
    }
}
