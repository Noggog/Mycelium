using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The one place a discovery verdict is turned into writes, now that the single-item route and the
/// batch both go through it. Two things are worth asserting here and nothing much else: that a batch
/// records every item it can and reports the ones it can't <em>individually</em> (a caller told only
/// "some of that failed" has to re-read its ratings to find out which), and that a batched verdict
/// produces the same Plex mood writes a single one does — the divergence that would fail nothing at
/// the time and surface months later as a smart playlist matching rejected music.
/// </summary>
public class DiscoveryRatingServiceTests
{
    private const string User = "user-1";
    private const string Username = "noggog";
    private const string Liked = "noggog_liked";
    private const string Disliked = "noggog_disliked";

    private readonly IUserQueueRepo _queue = Substitute.For<IUserQueueRepo>();
    private readonly IRelatedArtistReader _related = Substitute.For<IRelatedArtistReader>();
    private readonly ILibraryProvider _library = Substitute.For<ILibraryProvider>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IMissingAlbumRepo _missing = Substitute.For<IMissingAlbumRepo>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly IAlbumMatchOverrideRepo _overrides = Substitute.For<IAlbumMatchOverrideRepo>();
    private readonly FakeDeezerAlbumArtistRepo _albumArtists = new();
    private readonly IUserAlbumRatingRepo _albumRatings = Substitute.For<IUserAlbumRatingRepo>();
    private readonly IAlbumBlockRepo _blocks = Substitute.For<IAlbumBlockRepo>();
    private readonly IDeezerApi _deezer = Substitute.For<IDeezerApi>();
    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();

    // The follow-up worker is the real thing rather than a substitute: the Plex mood writes this
    // service is judged on are queued, not awaited, so nothing can be asserted about them without a
    // worker to drain the queue. These two taggers are where they land.
    private readonly IArtistTagger _tagger = Substitute.For<IArtistTagger>();
    private readonly IAlbumTagger _albumTagger = Substitute.For<IAlbumTagger>();
    private readonly IVerdictFollowUp _verdicts = Substitute.For<IVerdictFollowUp>();
    private readonly ArtistFollowUpService _followUps;

    private readonly DiscoveryRatingService _sut;

    public DiscoveryRatingServiceTests()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var resolver = new DeezerArtistResolver(_deezer, cache, _catalog);
        _overrides.GetAll().Returns(Array.Empty<AlbumMatchOverride>());
        _users.GetAll().Returns(Array.Empty<AppUser>());
        var quality = new UserQualityService(_users, AudioQuality.Lossless);
        var refresher = new MissingAlbumRefresher(
            _catalog, resolver, _deezer, _missing, _overrides, _albumArtists, quality,
            NullLogger<MissingAlbumRefresher>.Instance);
        var engine = new DiscoveryEngine(
            _queue, _related, _library, _catalog, _missing, _albumRatings, _blocks, refresher,
            quality, NullLogger<DiscoveryEngine>.Instance);

        _followUps = new ArtistFollowUpService(
            _verdicts, _related, _tagger, _albumTagger, NullLogger<ArtistFollowUpService>.Instance);

        var collections = new CollectionService(
            _deezer, _missing, _albumRatings, _catalog, _overrides, _plex, _followUps, _followUps,
            NullLogger<CollectionService>.Instance);

        _sut = new DiscoveryRatingService(engine, _followUps, collections);

        // Sensible empty defaults; individual cases override what they need.
        _missing.GetAll().Returns(Array.Empty<MissingAlbum>());
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _plex.GetMachineIdentifier().Returns((string?)null);
    }

    private static DiscoveryRateItem Artist(string name, string verdict = "up") =>
        new(name, Album: null, AlbumArt: null, verdict);

    private static DiscoveryRateItem Album(string artist, string album, string verdict = "up") =>
        new(artist, album, AlbumArt: null, verdict);

    /// <summary>
    /// Runs the follow-up worker until <paramref name="done"/> holds (or fails the test rather than
    /// hanging the suite), then stops it. The queued work is what carries a verdict into Plex, so a
    /// case about tagging has to actually let it run.
    /// </summary>
    private async Task Drain(Func<bool> done)
    {
        await _followUps.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!done() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            done().Should().BeTrue("the queued follow-up work should have run");
        }
        finally
        {
            await _followUps.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The case the batch exists to survive: a playlist's worth of verdicts where one write fails. The
    /// others must still land — a batch that gave up at the first failure would leave the set
    /// half-applied with no record of where it stopped — and the failure must be attributed to the item
    /// that caused it, so the caller retries one artist rather than the whole playlist.
    /// </summary>
    [Fact]
    public async Task A_batch_records_what_it_can_and_attributes_each_failure_to_its_item()
    {
        _queue.Rate(User, "Broken Act", Arg.Any<DiscoveryStatus>(), Arg.Any<string?>())
            .Returns<DiscoveryCandidate?>(_ => throw new InvalidOperationException("mongo is down"));

        var response = await _sut.RateMany(User, Username, new[]
        {
            Artist("Autechre"),
            Artist("Broken Act"),
            Artist("Boards of Canada"),
        });

        response.Total.Should().Be(3);
        response.Succeeded.Should().Be(2);
        response.Failed.Should().Be(1);
        response.Results.Select(r => r.Index).Should().Equal(0, 1, 2);

        var failure = response.Results[1];
        failure.Ok.Should().BeFalse();
        failure.Artist.Should().Be("Broken Act", "the caller retries by name, so the row has to name one");
        failure.Error.Should().Contain("mongo is down");

        response.Results[0].Ok.Should().BeTrue();
        response.Results[2].Ok.Should().BeTrue();
        response.Results[2].Error.Should().BeNull();

        // The point of not aborting: the verdict after the failure was still recorded.
        await _queue.Received(1).Rate(User, "Boards of Canada", DiscoveryStatus.Liked, Arg.Any<string?>());
    }

    /// <summary>
    /// The divergence this whole refactor exists to prevent. A like stamps "&lt;user&gt;_liked" and
    /// strips "_disliked"; the opposite verdict does the reverse. If the batch path got the
    /// <em>stripping</em> wrong an artist would end up carrying both moods, nothing would fail, and the
    /// only symptom would be a "My Library" playlist quietly matching music the user had rejected.
    /// </summary>
    [Fact]
    public async Task A_batched_artist_verdict_stamps_the_same_moods_the_single_route_does()
    {
        await _sut.RateMany(User, Username, new[]
        {
            Artist("Autechre"),
            Artist("Coldplay", "down"),
        });

        await Drain(() => _tagger.ReceivedCalls().Count() >= 2);

        await _tagger.Received(1).SetTags(
            "Autechre", Liked, Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Disliked })));
        await _tagger.Received(1).SetTags(
            "Coldplay", Disliked, Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { Liked })));
    }

    /// <summary>
    /// An album verdict in a batch takes the album branch, exactly as the single route does: the rating
    /// is recorded against the album and no artist verdict is invented for it. Batching must not turn a
    /// thumb on one record into a thumb on the act that made it.
    /// </summary>
    [Fact]
    public async Task A_batched_album_verdict_rates_the_album_and_not_its_artist()
    {
        await _sut.RateMany(User, Username, new[]
        {
            Album("Boards of Canada", "Geogaddi"),
        });

        await _albumRatings.Received(1).Rate(
            User, "Boards of Canada", "Geogaddi", Arg.Any<string?>(), DiscoveryStatus.Liked);
        await _queue.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>(), Arg.Any<string?>());
    }

    /// <summary>
    /// Over the cap the batch is refused whole and nothing is written. Truncating would be worse than
    /// refusing: the caller is told it succeeded and then waits forever on the items past the cut.
    /// </summary>
    [Fact]
    public async Task An_overlong_batch_is_refused_whole_rather_than_truncated()
    {
        var tooMany = Enumerable.Range(0, BatchLimits.MaxItems + 1)
            .Select(i => Artist($"Act {i}"))
            .ToArray();

        var rate = () => _sut.RateMany(User, Username, tooMany);

        (await rate.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain(BatchLimits.MaxItems.ToString())
            .And.Contain(tooMany.Length.ToString(), "the caller needs to see by how much it overshot");
        await _queue.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>(), Arg.Any<string?>());
    }

    /// <summary>A batch exactly at the cap is accepted — the limit is inclusive.</summary>
    [Fact]
    public async Task A_batch_at_the_cap_is_accepted()
    {
        var atCap = Enumerable.Range(0, BatchLimits.MaxItems)
            .Select(i => Artist($"Act {i}"))
            .ToArray();

        var response = await _sut.RateMany(User, Username, atCap);

        response.Total.Should().Be(BatchLimits.MaxItems);
        response.Failed.Should().Be(0);
    }

    /// <summary>An empty batch is a no-op, not an error — a client with nothing to send isn't wrong.</summary>
    [Fact]
    public async Task An_empty_batch_is_accepted_and_writes_nothing()
    {
        var response = await _sut.RateMany(User, Username, Array.Empty<DiscoveryRateItem>());

        response.Total.Should().Be(0);
        response.Results.Should().BeEmpty();
        await _queue.DidNotReceive().Rate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DiscoveryStatus>(), Arg.Any<string?>());
    }
}
