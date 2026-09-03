using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using Mycelium.Plex.Services.Smart;

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
///
/// <para>The rules are decomposed here rather than kept as Plex's own query string — see
/// <see cref="PlaylistRuleMapper"/>. Doing it at harvest time costs one tag-vocabulary read per sweep
/// and means neither the mirror nor the archive holds anything that needs a live Plex to interpret.
/// </para>
/// </summary>
public class PlaylistHarvester
{
    private readonly IUserRepo _users;
    private readonly IPlexLinkRepo _links;
    private readonly IPlexPlaylistApi _playlists;
    private readonly IPlexApi _plexApi;
    private readonly IUserPlaylistRepo _store;
    private readonly ILogger<PlaylistHarvester> _logger;

    public PlaylistHarvester(
        IUserRepo users,
        IPlexLinkRepo links,
        IPlexPlaylistApi playlists,
        IPlexApi plexApi,
        IUserPlaylistRepo store,
        ILogger<PlaylistHarvester> logger)
    {
        _users = users;
        _links = links;
        _playlists = playlists;
        _plexApi = plexApi;
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

        // A tag renamed in Plex should show up on the next pass, not be pinned for the process's life.
        _tagNames.Clear();
        _sectionKey = null;

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
                        Rules: await Rules(playlist),
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

    /// <summary>
    /// One playlist's rules, decomposed into the portable form. Null for a hand-built playlist, and
    /// also for a smart one whose stored query this build can't parse — the tracks are what matter for
    /// the first, and for the second an unparseable query is better recorded as "no rules we could
    /// read" than as a server-local string nothing downstream could use.
    /// </summary>
    private async Task<PlaylistRules?> Rules(PlexPlaylist playlist)
    {
        if (!playlist.Smart || !playlist.TryGetFilter(out _, out var filter))
        {
            return null;
        }

        return PlaylistRuleMapper.ToPortable(filter, await TagResolver(filter));
    }

    /// <summary>
    /// Resolves the tag ids this filter references back to tag names.
    ///
    /// <para>Only the vocabularies actually referenced are fetched — typically just "mood", and usually
    /// none at all for a rules set built on ratings alone. The maps are cached for the life of the
    /// sweep: a tag vocabulary is shared library metadata, identical for every account, so re-reading
    /// it per user would be the same answer bought repeatedly.</para>
    ///
    /// <para>A vocabulary that can't be read costs the names, not the sweep: the id is written instead,
    /// which is worse than a name and much better than losing the rule.</para>
    /// </summary>
    private async Task<PlaylistRuleMapper.TagResolver> TagResolver(PlexSmartFilter filter)
    {
        foreach (var (field, leaf, type) in PlexTagFields.Referenced([filter.Rules]))
        {
            if (_tagNames.ContainsKey(field))
            {
                continue;
            }

            try
            {
                var entries = await _playlists.GetSectionTags(await SectionKey(), leaf, type);
                _tagNames[field] = entries
                    .GroupBy(e => e.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Playlist harvest could not read the {Field} tag vocabulary; ids will be kept raw",
                    field);
                _tagNames[field] = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        return (field, value) =>
            _tagNames.TryGetValue(field.ToLowerInvariant(), out var map)
            && map.TryGetValue(value, out var name)
                ? name
                : null;
    }

    /// <summary>The music section the tag vocabularies are read from, resolved once per sweep.</summary>
    private async Task<int> SectionKey() => _sectionKey ??= (await _plexApi.ResolveLibrary()).Key;

    /// <summary>
    /// Tag vocabularies, keyed by lower-cased field name. Reset at the start of every sweep so a tag
    /// renamed in Plex is picked up on the next pass rather than being pinned for the process's life.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tagNames =
        new(StringComparer.OrdinalIgnoreCase);

    private int? _sectionKey;

    private async Task<IReadOnlyList<PlaylistTrack>> Tracks(string token, PlexPlaylist playlist)
    {
        var items = await _playlists.GetPlaylistItems(token, playlist.RatingKey);
        return items
            .Select(i => new PlaylistTrack(
                i.Position, i.Artist ?? "", i.Album ?? "", i.Title ?? "", i.File))
            .ToList();
    }
}
