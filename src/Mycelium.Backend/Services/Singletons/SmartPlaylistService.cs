using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using Mycelium.Plex.Services.Smart;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>Where a stock playlist stands for one user.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockPlaylistState
{
    /// <summary>Nothing on the server selects this, and no playlist holds the name — safe to create.</summary>
    NotCreated,

    /// <summary>A playlist with these exact rules already exists, whatever it is called.</summary>
    Exists,

    /// <summary>A playlist holds the name but selects something else — offer to rewrite its rules.</summary>
    Differs,

    /// <summary>Can't be built for this user yet; <see cref="StockPlaylistStatus.Note"/> says why.</summary>
    Unavailable,
}

/// <summary>One row of the Playlists page.</summary>
public record StockPlaylistStatus(
    string Id,
    string Title,
    string Description,
    StockPlaylistState State,
    string? MatchedTitle = null,
    string? MatchedRatingKey = null,
    int? TrackCount = null,
    string? Note = null,
    string? PlexUrl = null);

/// <summary>The whole page: whether the user has linked Plex, and where each stock playlist stands.</summary>
public record PlaylistSurvey(
    bool Linked,
    string? PlexUsername,
    int FreshMonths,
    bool HalfStars,
    IReadOnlyList<StockPlaylistStatus> Playlists);

/// <summary>
/// Builds the stock smart playlists in the user's <em>own</em> Plex account, and works out which ones
/// they already have.
///
/// <para><b>Existence is decided by rules, not by name.</b> A user who built the same playlist by hand
/// three years ago and called it "car music" should be told they already have it, not handed a
/// duplicate. Both sides are parsed into rule trees, tag ids are resolved back to tag names (ids are
/// per-server, so comparing them directly would be meaningless), and the trees are canonicalised before
/// comparison — see <see cref="PlexFilterCanonicalizer"/>. Name is used for exactly one thing: spotting
/// a playlist that holds a name we want but means something else, so we can offer to fix it rather than
/// silently create a second playlist with the same title.</para>
/// </summary>
public class SmartPlaylistService
{
    private readonly IPlexLinkRepo _links;
    private readonly IPlexPlaylistApi _playlists;
    private readonly IPlexApi _plexApi;
    private readonly IUserRepo _users;
    private readonly ILogger<SmartPlaylistService> _logger;

    public SmartPlaylistService(
        IPlexLinkRepo links,
        IPlexPlaylistApi playlists,
        IPlexApi plexApi,
        IUserRepo users,
        ILogger<SmartPlaylistService> logger)
    {
        _links = links;
        _playlists = playlists;
        _plexApi = plexApi;
        _users = users;
        _logger = logger;
    }

    /// <summary>The default "not played recently" window, used when the caller names none.</summary>
    public const int DefaultFreshMonths = 3;

    public async Task<PlaylistSurvey> Survey(string subject, string? username, int freshMonths)
    {
        var link = await _links.Get(subject);
        if (link is null)
        {
            // No account to survey — but the rating scale is the user's own answer, not the Plex
            // link's, so report it anyway rather than making the page guess before they connect.
            return new PlaylistSurvey(
                Linked: false,
                PlexUsername: null,
                freshMonths,
                HalfStars: await HalfStars(subject),
                Array.Empty<StockPlaylistStatus>());
        }

        var context = await LoadContext(subject, link, username, freshMonths);
        return new PlaylistSurvey(
            Linked: true,
            PlexUsername: link.Username,
            FreshMonths: freshMonths,
            HalfStars: context.Options.HalfStars,
            Playlists: context.Definitions.Select(context.Evaluate).ToArray());
    }

    /// <summary>
    /// Creates one stock playlist. Idempotent by design: if the survey already recognises it, the
    /// existing playlist is returned untouched rather than a second copy being made.
    /// </summary>
    public async Task<StockPlaylistStatus> Create(
        string subject, string? username, string definitionId, int freshMonths)
    {
        var (context, definition) = await Resolve(subject, username, definitionId, freshMonths);
        var status = context.Evaluate(definition);
        if (status.State is StockPlaylistState.Exists or StockPlaylistState.Unavailable)
        {
            return status;
        }

        var created = await _playlists.CreateSmartPlaylist(
            context.Token, definition.Title, context.SectionKey, definition.Filter!);

        _logger.LogInformation(
            "Created stock playlist {Id} as '{Title}' ({Tracks} tracks) for {User}",
            definition.Id, created.Title, created.LeafCount, context.PlexUsername);

        return status with
        {
            State = StockPlaylistState.Exists,
            MatchedTitle = created.Title,
            MatchedRatingKey = created.RatingKey,
            TrackCount = created.LeafCount,
            Note = null,
            PlexUrl = context.LinkTo(created.RatingKey),
        };
    }

    /// <summary>
    /// Rewrites the rules of the playlist currently holding this definition's name, bringing a drifted
    /// or hand-edited playlist back in line. Only meaningful from <see cref="StockPlaylistState.Differs"/>.
    /// </summary>
    public async Task<StockPlaylistStatus> UpdateRules(
        string subject, string? username, string definitionId, int freshMonths)
    {
        var (context, definition) = await Resolve(subject, username, definitionId, freshMonths);
        var status = context.Evaluate(definition);
        if (status.State != StockPlaylistState.Differs || status.MatchedRatingKey is null)
        {
            return status;
        }

        var updated = await _playlists.UpdateSmartPlaylistFilter(
            context.Token, status.MatchedRatingKey, context.SectionKey, definition.Filter!);

        _logger.LogInformation(
            "Rewrote rules of '{Title}' to stock playlist {Id} ({Tracks} tracks) for {User}",
            updated.Title, definition.Id, updated.LeafCount, context.PlexUsername);

        return status with
        {
            State = StockPlaylistState.Exists,
            MatchedTitle = updated.Title,
            TrackCount = updated.LeafCount,
            Note = null,
        };
    }

    private async Task<(SurveyContext Context, StockPlaylistDefinition Definition)> Resolve(
        string subject, string? username, string definitionId, int freshMonths)
    {
        var link = await _links.Get(subject)
                   ?? throw new InvalidOperationException("No Plex account is linked to this user.");

        var context = await LoadContext(subject, link, username, freshMonths);
        var definition = context.Definitions.FirstOrDefault(d => d.Id == definitionId)
                         ?? throw new ArgumentException($"Unknown playlist '{definitionId}'.", nameof(definitionId));

        return (context, definition);
    }

    /// <summary>
    /// Gathers everything a survey needs in one pass: the library section, the user's existing smart
    /// playlists with their rules, the stock definitions, and the tag vocabularies both sides reference.
    /// </summary>
    private async Task<SurveyContext> LoadContext(
        string subject, PlexLink link, string? username, int freshMonths)
    {
        var section = (await _plexApi.ResolveLibrary()).Key;
        var existing = await _playlists.GetSmartAudioPlaylists(link.ServerToken);

        // Only used to build "open it in Plex" links, so a server that won't say who it is costs the
        // links, not the survey.
        var machineId = await MachineIdentifier();

        // The same tag the thumbs write, derived the same way, so the rule can't drift from the tagger.
        // Looked up twice because Plex keys its tag vocabularies per metadata type: the identical name
        // has one id on artists and a different one on albums, and a "My Library" playlist has to match
        // both — the artist tag for ordinary likes, the album tag for collections, which have no act to
        // carry one.
        var likedTag = ArtistTag.For(username, DiscoveryStatus.Liked);
        var likedArtistTagId = likedTag is null
            ? null
            : await FindTagId(section, "mood", likedTag, PlexSmartFilter.ArtistType);
        var likedAlbumTagId = likedTag is null
            ? null
            : await FindTagId(section, "mood", likedTag, PlexSmartFilter.AlbumType);

        // The marker the discovery sweep writes onto owned-but-unrated artists the user's likes point
        // at, which the Frontier rule unions with the likes. Artists only — the sweep never puts it on
        // an album — so there is no album-vocabulary lookup to match the pair above.
        var recommendedTag = ArtistTag.Recommended(username);
        var recommendedArtistTagId = recommendedTag is null
            ? null
            : await FindTagId(section, "mood", recommendedTag, PlexSmartFilter.ArtistType);

        var options = new StockPlaylistOptions(
            likedArtistTagId,
            likedAlbumTagId,
            recommendedArtistTagId,
            freshMonths,
            await HalfStars(subject));
        var definitions = SmartPlaylistCatalog.Build(options);

        // Only the tag vocabularies actually referenced get fetched — one request each, and typically
        // just "mood".
        var trees = definitions.Select(d => d.Filter?.Rules)
            .Concat(existing.Select(p => p.TryGetFilter(out _, out var f) ? f.Rules : null));
        var maps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, leaf, type) in PlexTagFields.Referenced(trees))
        {
            var tags = await _playlists.GetSectionTags(section, leaf, type);
            maps[field] = tags
                .GroupBy(t => t.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        }

        return new SurveyContext(
            link.ServerToken, link.Username, section, machineId, options, definitions, existing, maps);
    }

    /// <summary>
    /// Whether this user rates in half stars. Plex can't be asked — half-star support is a per-client
    /// capability, not a setting — so this is the answer they gave on the Playlists page, falling back
    /// to the catalog default while they haven't given one.
    /// </summary>
    private async Task<bool> HalfStars(string subject) =>
        (await _users.Get(subject))?.HalfStarRatings ?? SmartPlaylistCatalog.DefaultHalfStars;

    /// <summary>The server id the deep links are built from, or null when Plex can't be reached.</summary>
    private async Task<string?> MachineIdentifier()
    {
        try
        {
            return await _plexApi.GetMachineIdentifier();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't fetch Plex machineIdentifier for playlist links");
            return null;
        }
    }

    private async Task<string?> FindTagId(int section, string field, string tagName, int type)
    {
        var tags = await _playlists.GetSectionTags(section, field, type);
        return tags.FirstOrDefault(t => string.Equals(t.Title, tagName, StringComparison.OrdinalIgnoreCase))?.Key;
    }

    /// <summary>One survey's worth of loaded state, so each definition can be judged without more I/O.</summary>
    private sealed record SurveyContext(
        string Token,
        string PlexUsername,
        int SectionKey,
        string? MachineId,
        StockPlaylistOptions Options,
        IReadOnlyList<StockPlaylistDefinition> Definitions,
        IReadOnlyList<PlexPlaylist> Existing,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TagNames)
    {
        public StockPlaylistStatus Evaluate(StockPlaylistDefinition definition)
        {
            var status = new StockPlaylistStatus(
                definition.Id, definition.Title, definition.Description, StockPlaylistState.NotCreated);

            if (definition.Filter is null)
            {
                return status with { State = StockPlaylistState.Unavailable, Note = definition.Unavailable };
            }

            foreach (var playlist in Existing)
            {
                if (playlist.TryGetFilter(out var section, out var filter)
                    && section == SectionKey
                    && PlexFilterCanonicalizer.AreEquivalent(definition.Filter, filter, ResolveTag))
                {
                    return status with
                    {
                        State = StockPlaylistState.Exists,
                        MatchedTitle = playlist.Title,
                        MatchedRatingKey = playlist.RatingKey,
                        TrackCount = playlist.LeafCount,
                        PlexUrl = LinkTo(playlist.RatingKey),
                    };
                }
            }

            // No rule match. If something already holds the name, creating would leave two playlists
            // called the same thing — offer to rewrite that one instead.
            var clash = Existing.FirstOrDefault(
                p => string.Equals(p.Title, definition.Title, StringComparison.OrdinalIgnoreCase));

            return clash is null
                ? status
                : status with
                {
                    State = StockPlaylistState.Differs,
                    MatchedTitle = clash.Title,
                    MatchedRatingKey = clash.RatingKey,
                    TrackCount = clash.LeafCount,
                    Note = "This name is taken by a different playlist.",
                    PlexUrl = LinkTo(clash.RatingKey),
                };
        }

        /// <summary>An app.plex.tv link to one of the user's playlists, when the server named itself.</summary>
        public string? LinkTo(string ratingKey) =>
            string.IsNullOrEmpty(MachineId) ? null : PlexDeepLink.ToPlaylist(MachineId, ratingKey);

        private string? ResolveTag(string field, string value) =>
            TagNames.TryGetValue(field, out var map) && map.TryGetValue(value, out var name) ? name : null;
    }
}
