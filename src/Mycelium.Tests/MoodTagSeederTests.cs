using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The seed that gives Plex a "&lt;user&gt;_disliked" tag id to point a rule at before the user has
/// rejected anything. These assert the two things that make it safe to run unattended every night: it
/// writes only to the anchor record, and it never argues with a verdict the user actually cast.
/// </summary>
public class MoodTagSeederTests
{
    private const int LibraryKey = 3;
    private const int AnchorArtistKey = 4100;
    private const int AnchorAlbumKey = 4200;

    /// <summary>The credit the fixtures put in the library — the list's first entry.</summary>
    private static readonly string Anchor = MoodTagSeeder.AnchorCredits[0];

    private readonly IArtistCatalogRepo _catalog = Substitute.For<IArtistCatalogRepo>();
    private readonly IArtistTagger _tagger = Substitute.For<IArtistTagger>();
    private readonly IPlexApi _plex = Substitute.For<IPlexApi>();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly MoodTagSeeder _sut;

    public MoodTagSeederTests()
    {
        _sut = new MoodTagSeeder(
            _catalog, _tagger, _plex, _users, NullLogger<MoodTagSeeder>.Instance);

        // Nothing in the library and nobody known: each case opts in to what it needs.
        _catalog.GetPlexRatingKeys(Arg.Any<ArtistKey>()).Returns(Array.Empty<int>());
        _catalog.GetAlbumPlexRatingKeys(Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new Dictionary<string, Dictionary<string, int>>());
        _users.GetAll().Returns(Array.Empty<AppUser>());
        _plex.ResolveLibrary().Returns(new PlexLibrary { Key = LibraryKey, Title = "Music", Type = "artist" });
    }

    /// <summary>Puts the anchor act in the catalog under <paramref name="credit"/>, with its moods.</summary>
    private void AnchorInLibrary(string credit, params string[] moods)
    {
        _catalog.GetPlexRatingKeys(new ArtistKey(credit)).Returns(new[] { AnchorArtistKey });
        _plex.GetMusicArtist(AnchorArtistKey).Returns(new PlexMusicArtist
        {
            RatingKey = AnchorArtistKey,
            Title = credit,
            Mood = moods.Select(m => new PlexTag { Tag = m }).ToArray(),
        });
    }

    /// <summary>Gives the anchor act albums, keyed by library title exactly as the catalog stores them.</summary>
    private void AnchorAlbums(string credit, params (string Title, int Key)[] albums)
    {
        _catalog.GetAlbumPlexRatingKeys(Arg.Is<IReadOnlyCollection<string>>(a => a.Contains(credit)))
            .Returns(new Dictionary<string, Dictionary<string, int>>
            {
                [credit] = albums.ToDictionary(a => a.Title, a => a.Key),
            });

        foreach (var album in albums)
        {
            _plex.GetMusicAlbum(album.Key).Returns(new PlexMusicAlbum
            {
                RatingKey = album.Key,
                Title = album.Title,
                ParentTitle = credit,
                Mood = Array.Empty<PlexTag>(),
            });
        }
    }

    private void KnownUsers(params string?[] usernames) =>
        _users.GetAll().Returns(usernames
            .Select((u, i) => new AppUser(
                $"user-{i}", u, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
            .ToArray());

    /// <summary>
    /// The whole point: after a seed the tag is on a real Plex item, which is the only thing that makes
    /// Plex mint an id for it — and the id is what Deep Frontier's exclusion rule stores.
    /// </summary>
    [Fact]
    public async Task Stamps_the_rejection_on_the_anchor_artist()
    {
        AnchorInLibrary(Anchor);

        (await _sut.Seed("noggog")).Should().BeTrue();

        await _tagger.Received(1).SetTags(
            Anchor, "noggog_disliked", Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 0));
    }

    /// <summary>
    /// Plex keys tags per metadata type, so the same name has a different id on albums than on artists
    /// and Deep Frontier subtracts both. Seeding only the artist would leave the album rule to appear
    /// months later, the first time the user rejects a compilation — which is exactly the definition
    /// change that flips their existing playlist to "name taken".
    /// </summary>
    [Fact]
    public async Task Stamps_the_rejection_on_an_anchor_album_too()
    {
        AnchorInLibrary(Anchor);
        AnchorAlbums(Anchor, ("Lamb Chop's Sing-Along", AnchorAlbumKey));

        await _sut.Seed("noggog");

        await _plex.Received(1).SetAlbumMoods(
            LibraryKey, AnchorAlbumKey,
            Arg.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(new[] { "noggog_disliked" })),
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 0));
    }

    /// <summary>
    /// One album, deterministically the same one on every pass — otherwise a nightly run would smear
    /// the tag across the anchor's whole discography one record at a time.
    /// </summary>
    [Fact]
    public async Task Picks_one_anchor_album_and_keeps_picking_it()
    {
        AnchorInLibrary(Anchor);
        AnchorAlbums(Anchor, ("Sing-Along", 4300), ("Play-Along", AnchorAlbumKey));

        await _sut.Seed("noggog");
        await _sut.Seed("noggog");

        // "Play-Along" sorts first, and both passes chose it.
        await _plex.Received(2).SetAlbumMoods(
            LibraryKey, AnchorAlbumKey,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
        await _plex.DidNotReceive().SetAlbumMoods(
            LibraryKey, 4300,
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// Already-seeded is the steady state, and it must cost no write: <c>MoodTags.Reconcile</c> is the
    /// same rule a real thumb goes through, so the album half no-ops once the tag is there.
    /// </summary>
    [Fact]
    public async Task Writes_nothing_when_the_album_already_carries_the_tag()
    {
        AnchorInLibrary(Anchor);
        AnchorAlbums(Anchor, ("Play-Along", AnchorAlbumKey));
        _plex.GetMusicAlbum(AnchorAlbumKey).Returns(new PlexMusicAlbum
        {
            RatingKey = AnchorAlbumKey,
            Title = "Play-Along",
            Mood = new[] { new PlexTag { Tag = "noggog_disliked" } },
        });

        await _sut.Seed("noggog");

        await _plex.DidNotReceive().SetAlbumMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// A library that doesn't hold the record is not an error — it just isn't seeded, and behaves
    /// exactly as it did before this existed. Nothing may be written, least of all to some other artist
    /// that happens to be lying around.
    /// </summary>
    [Fact]
    public async Task Does_nothing_at_all_when_the_anchor_is_not_in_the_library()
    {
        (await _sut.Seed("noggog")).Should().BeFalse();

        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>());
        await _plex.DidNotReceive().SetAlbumMoods(
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// The credit on this record isn't standardised, so the list is tried in order and the first
    /// spelling the catalog knows wins — a library that files it under the puppet is still seeded.
    /// </summary>
    [Fact]
    public async Task Takes_whichever_credit_the_library_files_the_record_under()
    {
        var fallback = MoodTagSeeder.AnchorCredits[^1];
        AnchorInLibrary(fallback);

        await _sut.Seed("noggog");

        await _tagger.Received(1).SetTags(
            fallback, "noggog_disliked", Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// A real verdict outranks a seed. Without this the nightly pass and the thumb would take turns
    /// overwriting each other forever — and the user would find a record they said they liked being
    /// quietly re-rejected every morning.
    /// </summary>
    [Fact]
    public async Task Leaves_the_seed_off_an_anchor_the_user_actually_thumbed_up()
    {
        AnchorInLibrary(Anchor, "noggog_liked");

        (await _sut.Seed("noggog")).Should().BeFalse();

        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// ...but only for the user who cast it. Another user's like on the same item says nothing about
    /// this one, and the moods on a shared library are full of other people's verdicts.
    /// </summary>
    [Fact]
    public async Task Another_users_verdict_on_the_anchor_is_none_of_our_business()
    {
        AnchorInLibrary(Anchor, "someoneelse_liked");

        (await _sut.Seed("noggog")).Should().BeTrue();

        await _tagger.Received(1).SetTags(
            Anchor, "noggog_disliked", Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// The nightly pass covers everyone, including users who predate the seeder — the login hook only
    /// ever fires once per account, so this is the path that repairs the rest.
    /// </summary>
    [Fact]
    public async Task Seeds_every_known_user_on_a_full_pass()
    {
        AnchorInLibrary(Anchor);
        KnownUsers("noggog", "someoneelse");

        var result = await _sut.SeedAll();

        result.Should().Be(new MoodTagSeeder.SeedResult(Anchor, 2));
        await _tagger.Received(1).SetTags(
            Anchor, "noggog_disliked", Arg.Any<IReadOnlyCollection<string>>());
        await _tagger.Received(1).SetTags(
            Anchor, "someoneelse_disliked", Arg.Any<IReadOnlyCollection<string>>());
    }

    /// <summary>
    /// A user with no username has no tag to build (<see cref="ArtistTag.For"/> returns null), and is
    /// skipped rather than counted — the dev panel's number has to mean what it says.
    /// </summary>
    [Fact]
    public async Task Skips_a_user_with_no_usable_username()
    {
        AnchorInLibrary(Anchor);
        KnownUsers("noggog", null, "   ");

        (await _sut.SeedAll()).Seeded.Should().Be(1);
    }

    /// <summary>
    /// The null anchor is how the dev endpoint distinguishes "seeded nobody because there are no users"
    /// from "seeded nobody because this library hasn't got the record".
    /// </summary>
    [Fact]
    public async Task A_full_pass_reports_no_anchor_when_the_library_lacks_the_record()
    {
        KnownUsers("noggog");

        (await _sut.SeedAll()).Should().Be(default(MoodTagSeeder.SeedResult));
    }

    /// <summary>
    /// Best-effort by contract: this runs inside the nightly sync and off the back of a login, and
    /// neither may be taken down by a Plex that is having a bad day.
    /// </summary>
    [Fact]
    public async Task A_failing_plex_is_logged_rather_than_thrown()
    {
        AnchorInLibrary(Anchor);
        AnchorAlbums(Anchor, ("Play-Along", AnchorAlbumKey));
        KnownUsers("noggog");
        _plex.ResolveLibrary().Returns<PlexLibrary>(_ => throw new InvalidOperationException("Plex is down"));

        (await _sut.Seed("noggog")).Should().BeFalse();
        (await _sut.SeedAll()).Should().Be(default(MoodTagSeeder.SeedResult));
    }
}
