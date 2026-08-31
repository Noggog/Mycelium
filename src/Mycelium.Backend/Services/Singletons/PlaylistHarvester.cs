using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>How one pass went.</summary>
/// <param name="Users">Accounts swept.</param>
/// <param name="Playlists">Playlists stored across all of them.</param>
/// <param name="Skipped">Accounts with no Plex link, or whose sweep failed.</param>
public record PlaylistHarvestResult(int Users, int Playlists, int Skipped);

/// <summary>
/// Copies every user's Plex playlists into Mongo, so they exist somewhere other than Plex.
///
/// <para>Playlists have never been stored by this app: the stock-playlist feature creates them in the
/// user's own Plex account and then treats Plex as the owner. That is right for the feature and wrong
/// for durability — together with star ratings, they are the only data here that a Plex failure would
/// destroy outright, and a hand-curated playlist is the least reconstructable thing in the system.</para>
///
/// <para>The two kinds are kept differently, and the distinction matters. A <b>smart</b> playlist is
/// its rules; its membership is only the current answer to them, so the rules are stored and the
/// tracks are not — snapshotting the answer would archive something that goes stale while losing the
/// thing that doesn't. A <b>hand-built</b> playlist has no rules and its ordered track list *is* the
/// work, so that is what gets stored.</para>
/// </summary>
public class PlaylistHarvester
{
    private readonly IUserRepo _users;
    private readonly IPlexLinkRepo _links;
    private readonly IPlexPlaylistApi _playlists;
    private readonly IUserPlaylistRepo _store;
    private readonly ILogger<PlaylistHarvester> _logger;

    public PlaylistHarvester(
        IUserRepo users,
        IPlexLinkRepo links,
        IPlexPlaylistApi playlists,
        IUserPlaylistRepo store,
        ILogger<PlaylistHarvester> logger)
    {
        _users = users;
        _links = links;
        _playlists = playlists;
        _store = store;
        _logger = logger;
    }

    /// <summary>Sweeps every linked account once. Public so it can be unit-tested without the timer.</summary>
    public async Task<PlaylistHarvestResult> HarvestAll()
    {
        AppUser[] users;
        try
        {
            users = await _users.GetAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist harvest could not enumerate users; will retry next interval");
            return new PlaylistHarvestResult(0, 0, 0);
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
                    // As with star ratings: "we can't read your playlists" is not "you have none", and
                    // emptying the mirror on an unlink would discard the only copy that survives Plex.
                    skipped++;
                    continue;
                }

                var found = await _playlists.GetAudioPlaylists(link.ServerToken);
                var mapped = new List<UserPlaylist>(found.Length);

                foreach (var playlist in found)
                {
                    mapped.Add(new UserPlaylist(
                        Title: playlist.Title,
                        Smart: playlist.Smart,
                        Rules: playlist.Smart ? playlist.Content : null,
                        // Membership is only fetched for hand-built playlists — a smart playlist's is a
                        // live query result, and one read per playlist is worth not spending on it.
                        Tracks: playlist.Smart
                            ? []
                            : await Tracks(link.ServerToken, playlist)));
                }

                stored += await _store.ReplaceForUser(user.Subject, mapped);
                swept++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playlist harvest failed for {User}; skipping to the next user", user.Subject);
                skipped++;
            }
        }

        _logger.LogInformation(
            "Playlist harvest: stored {Playlists} playlist(s) across {Users} account(s); {Skipped} skipped",
            stored, swept, skipped);
        return new PlaylistHarvestResult(swept, stored, skipped);
    }

    private async Task<IReadOnlyList<PlaylistTrack>> Tracks(string token, PlexPlaylist playlist)
    {
        var items = await _playlists.GetPlaylistItems(token, playlist.RatingKey);
        return items
            .Select(i => new PlaylistTrack(
                i.Position, i.Artist ?? "", i.Album ?? "", i.Title ?? "", i.File))
            .ToList();
    }
}
