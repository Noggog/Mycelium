using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// How the catalog sync works out what format the owned albums are in. Plex only exposes codecs on
/// tracks, so every answer costs a read — the design question is which reads a given pass is willing
/// to pay for, and the failure mode to design out is a cheap, frequent pass silently erasing what an
/// expensive one learned.
/// </summary>
public class CatalogQualityTests
{
    private readonly ILibraryQuery _library = Substitute.For<ILibraryQuery>();
    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();

    private const string Artist = "Alvvays";

    private readonly CatalogRefresher _sut;

    public CatalogQualityTests()
    {
        _sut = new CatalogRefresher(_library, _catalog, NullLogger<CatalogRefresher>.Instance);
        _library.QueryAllArtistMetadata().Returns(Array.Empty<ArtistMetadata>());
        _catalog.SyncFromLibrary(Arg.Any<IReadOnlyList<ArtistMetadata>>(), Arg.Any<DateTimeOffset>())
            .Returns(new CatalogSyncResult(0, 0, 0, Array.Empty<string>()));
        _catalog.GetOwnedAlbums()
            .Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(StringComparer.OrdinalIgnoreCase));
        _library.QueryAlbumQuality(Arg.Any<IReadOnlyCollection<int>>())
            .Returns(new Dictionary<int, AudioQuality?>());
        _library.QueryAllAlbumQuality().Returns(new Dictionary<int, AudioQuality?>());
    }

    /// <summary>Plex holds one album for our artist, under the given rating key.</summary>
    private void PlexHasAlbum(string title, int ratingKey) =>
        _library.QueryAllAlbums().Returns(new[]
        {
            new ArtistAlbums(new ArtistKey(Artist), new[] { new OwnedAlbum(title, ratingKey) }),
        });

    /// <summary>What the catalog already knows about that album's quality.</summary>
    private void AlreadyStored(string title, AudioQuality? quality) =>
        _catalog.GetOwnedAlbums().Returns(new Dictionary<string, Dictionary<string, AudioQuality?>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [Artist] = new(StringComparer.OrdinalIgnoreCase) { [title] = quality },
        });

    /// <summary>The albums as they were written back, with whatever quality was attached.</summary>
    private async Task<OwnedAlbum[]> Written()
    {
        var calls = _catalog.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IArtistCatalogRepo.SyncAlbums))
            .ToList();
        calls.Should().NotBeEmpty();
        var albums = (IReadOnlyList<ArtistAlbums>)calls.Last().GetArguments()[0]!;
        await Task.CompletedTask;
        return albums.SelectMany(a => a.Albums).ToArray();
    }

    [Fact]
    public async Task An_album_with_no_recorded_quality_is_resolved_with_a_targeted_read()
    {
        PlexHasAlbum("Blue Rev", 42);
        _library.QueryAlbumQuality(Arg.Any<IReadOnlyCollection<int>>())
            .Returns(new Dictionary<int, AudioQuality?> { [42] = AudioQuality.Lossless });

        await _sut.Refresh(CatalogRefresher.QualityRead.GapFill);

        (await Written()).Single().Quality.Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task An_album_we_already_know_about_is_not_read_again()
    {
        PlexHasAlbum("Blue Rev", 42);
        AlreadyStored("Blue Rev", AudioQuality.Lossy);

        await _sut.Refresh(CatalogRefresher.QualityRead.GapFill);

        // The point of gap-fill: steady state costs nothing, because the only albums without an
        // answer are the ones that have just arrived.
        await _library.Received(1).QueryAlbumQuality(Arg.Is<IReadOnlyCollection<int>>(k => k.Count == 0));
    }

    [Fact]
    public async Task A_gap_fill_carries_forward_what_it_did_not_re_read()
    {
        // The whole map is written back on every sync, so anything not re-read has to be carried
        // across — otherwise each cheap pass would erase the previous pass's answers.
        PlexHasAlbum("Blue Rev", 42);
        AlreadyStored("Blue Rev", AudioQuality.Lossy);

        await _sut.Refresh(CatalogRefresher.QualityRead.GapFill);

        (await Written()).Single().Quality.Should().Be(AudioQuality.Lossy);
        await _catalog.Received().SyncAlbums(Arg.Any<IReadOnlyList<ArtistAlbums>>(), true);
    }

    [Fact]
    public async Task A_full_sweep_overrides_what_was_stored()
    {
        // A deliberate re-derivation: the sweep is the authority, so an album that was recorded as
        // lossy and now reads lossless (it was replaced on disk) must come back lossless.
        PlexHasAlbum("Blue Rev", 42);
        AlreadyStored("Blue Rev", AudioQuality.Lossy);
        _library.QueryAllAlbumQuality()
            .Returns(new Dictionary<int, AudioQuality?> { [42] = AudioQuality.Lossless });

        await _sut.Refresh(CatalogRefresher.QualityRead.Full);

        (await Written()).Single().Quality.Should().Be(AudioQuality.Lossless);
        await _library.DidNotReceive().QueryAlbumQuality(Arg.Any<IReadOnlyCollection<int>>());
    }

    [Fact]
    public async Task Skipping_reads_nothing_and_writes_no_quality()
    {
        PlexHasAlbum("Blue Rev", 42);

        await _sut.Refresh(CatalogRefresher.QualityRead.Skip);

        await _library.DidNotReceive().QueryAlbumQuality(Arg.Any<IReadOnlyCollection<int>>());
        await _library.DidNotReceive().QueryAllAlbumQuality();
        // qualityKnown: false — so the repo leaves the stored answers alone rather than overwriting
        // them with this pass's blanks.
        await _catalog.Received().SyncAlbums(Arg.Any<IReadOnlyList<ArtistAlbums>>(), false);
    }

    [Fact]
    public async Task An_album_Plex_returns_no_tracks_for_stays_undetermined()
    {
        // QueryAlbumQuality omits it entirely rather than guessing. Undetermined must not read as
        // "needs upgrading", which is exactly what null gives us.
        PlexHasAlbum("Blue Rev", 42);

        await _sut.Refresh(CatalogRefresher.QualityRead.GapFill);

        (await Written()).Single().Quality.Should().BeNull();
    }
}
