using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Download;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Keeping an upgraded album on the release its ratings are stored against. The case that prompted it:
/// a FLAC upgrade of Hate Crew Deathroll was matched to a different release than the MP3s had been,
/// and every rating on it but one appeared to vanish until the album was rematched by hand.
/// </summary>
public class UpgradeMatchKeeperTests
{
    private const int AlbumKey = 77;
    private const string OldMatch = "plex://album/original-release";
    private const string OtherMatch = "plex://album/remaster";

    private readonly ILibraryQuery _library = Substitute.For<ILibraryQuery>();
    private readonly ILibraryMatcher _matcher = Substitute.For<ILibraryMatcher>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly FakePurchaseRepo _purchases = new();

    public UpgradeMatchKeeperTests()
    {
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Children of Bodom"] = new(StringComparer.OrdinalIgnoreCase) { ["Hate Crew Deathroll"] = AlbumKey },
            });
        Plex(quality: AudioQuality.Lossless, match: OldMatch);
    }

    private UpgradeMatchKeeper Sut() =>
        new(_library, _matcher, _catalog, _purchases, DownloaderConfigForTests.Default,
            NullLogger<UpgradeMatchKeeper>.Instance);

    /// <summary>What Plex currently lists for the album.</summary>
    private void Plex(AudioQuality quality, string? match)
    {
        _library.QueryAlbumQuality(Arg.Any<IReadOnlyCollection<int>>())
            .Returns(new Dictionary<int, AudioQuality?> { [AlbumKey] = quality });
        _library.QueryAlbumMatch(AlbumKey).Returns(match);
    }

    private PurchaseItem Upgrade(string? replacedMatch = OldMatch, DateTimeOffset? sentAt = null)
    {
        var item = new PurchaseItem(
            "album:children of bodom hate crew deathroll", FeedKind.UpgradeAlbum,
            new ArtistKey("Children of Bodom"), "Hate Crew Deathroll", null, 0, Array.Empty<string>(),
            PurchaseStatus.InLibrary, DateTimeOffset.UtcNow, sentAt ?? DateTimeOffset.UtcNow, 1,
            OwnedQuality: AudioQuality.Lossy, ReplacedPlexMatch: replacedMatch);
        _purchases.Seed(item);
        return item;
    }

    private string? Saved() => _purchases.Items.Single().ReplacedPlexMatch;

    [Fact]
    public async Task Remember_saves_the_match_of_the_copy_being_replaced()
    {
        var item = Upgrade(replacedMatch: null);

        await Sut().Remember(item, AlbumKey);

        Saved().Should().Be(OldMatch);
    }

    [Fact]
    public async Task Remember_saves_nothing_for_an_unmatched_album()
    {
        // A local:// id is Plex saying "not matched to anything" — there is no release to go back to.
        Plex(AudioQuality.Lossy, "local://12345");
        var item = Upgrade(replacedMatch: null);

        await Sut().Remember(item, AlbumKey);

        Saved().Should().BeNull();
    }

    [Fact]
    public async Task An_upgrade_that_kept_its_match_is_left_alone()
    {
        Upgrade();

        await Sut().CheckPending();

        await _matcher.DidNotReceiveWithAnyArgs().RematchAlbum(default, default!, default!);
        Saved().Should().BeNull();
    }

    [Fact]
    public async Task An_upgrade_matched_to_another_release_is_rematched_to_the_old_one()
    {
        Upgrade();
        Plex(AudioQuality.Lossless, OtherMatch);
        _matcher.When(m => m.RematchAlbum(AlbumKey, OldMatch, Arg.Any<string>()))
            .Do(_ => _library.QueryAlbumMatch(AlbumKey).Returns(OldMatch));

        await Sut().CheckPending();

        await _matcher.Received(1).RematchAlbum(AlbumKey, OldMatch, "Hate Crew Deathroll");
        Saved().Should().BeNull();
    }

    [Fact]
    public async Task The_old_copy_still_listed_is_not_mistaken_for_the_upgrade()
    {
        // Before Plex rescans, it still lists the MP3s under the old match. "Confirming" that would stop
        // checking before the new copy was ever matched.
        Upgrade();
        Plex(AudioQuality.Lossy, OldMatch);

        await Sut().CheckPending();

        Saved().Should().Be(OldMatch);
    }

    [Fact]
    public async Task A_rematch_that_does_not_take_is_retried_on_the_next_pass()
    {
        Upgrade();
        Plex(AudioQuality.Lossless, OtherMatch);

        await Sut().CheckPending();

        await _matcher.Received(1).RematchAlbum(AlbumKey, OldMatch, "Hate Crew Deathroll");
        Saved().Should().Be(OldMatch);
    }

    [Fact]
    public async Task It_gives_up_after_the_settle_window_rather_than_polling_forever()
    {
        Upgrade(sentAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        Plex(AudioQuality.Lossy, OldMatch);

        await Sut().CheckPending();

        Saved().Should().BeNull();
    }

    [Fact]
    public async Task One_album_failing_does_not_stop_the_check()
    {
        Upgrade();
        _library.QueryAlbumMatch(AlbumKey).Returns<string?>(_ => throw new HttpRequestException("Plex down"));

        var act = () => Sut().CheckPending();

        await act.Should().NotThrowAsync();
        Saved().Should().Be(OldMatch);
    }
}
