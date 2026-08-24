using FluentAssertions;
using Mycelium.Backend.Services.Download;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Translating Plex's paths into ones this process can open. Measured against a real server, a music
/// library spans several roots in Plex's namespace (<c>/media/music</c>, <c>/mediadrop/Music</c>)
/// while the app sees whatever was mounted into it — so acting on Plex's path verbatim would target
/// the wrong thing or nothing. Everything here exists to make an unmapped path a refusal rather than
/// a guess.
/// </summary>
public class LibraryPathMapTests
{
    private static LibraryPathMap Map(string? config = "/media/music:/music,/mediadrop/Music:/mediadrop") =>
        new(config);

    [Fact]
    public void A_mapped_path_is_rewritten_onto_the_local_mount()
    {
        Map().ToLocal("/media/music/Alvvays/Blue Rev/01 - Pharmacist.flac")
            .Should().Be("/music/Alvvays/Blue Rev/01 - Pharmacist.flac");
    }

    [Fact]
    public void Each_configured_root_maps_independently()
    {
        Map().ToLocal("/mediadrop/Music/Brennan/100 gecs/10,000 gecs/01.flac")
            .Should().Be("/mediadrop/Brennan/100 gecs/10,000 gecs/01.flac");
    }

    [Fact]
    public void An_unmapped_path_is_refused_rather_than_guessed_at()
    {
        // The whole point: "we can't safely touch this" is a different answer from "it isn't there",
        // and only one of them is safe to act on.
        Map().ToLocal("/media/download/music/something.flac").Should().BeNull();
    }

    [Fact]
    public void Nothing_resolves_without_configuration()
    {
        var unconfigured = Map(null);

        unconfigured.IsConfigured.Should().BeFalse();
        unconfigured.ToLocal("/media/music/anything.flac").Should().BeNull();
    }

    [Fact]
    public void A_prefix_only_matches_on_a_path_boundary()
    {
        // "/media/music" must not swallow "/media/musicals" — the rewritten path would land inside a
        // directory that has nothing to do with it.
        Map().ToLocal("/media/musicals/Cats/01.flac").Should().BeNull();
    }

    [Fact]
    public void The_longest_matching_prefix_wins()
    {
        // A nested mapping is more specific than the parent containing it, so it has to be tried
        // first or the parent would capture everything.
        var nested = Map("/media:/all,/media/music:/music");

        nested.ToLocal("/media/music/a.flac").Should().Be("/music/a.flac");
        nested.ToLocal("/media/other/b.flac").Should().Be("/all/other/b.flac");
    }

    [Fact]
    public void Trailing_slashes_and_separators_are_normalised()
    {
        Map("/media/music/:/music/").ToLocal("/media/music/a.flac").Should().Be("/music/a.flac");
    }

    [Fact]
    public void A_malformed_entry_is_ignored_rather_than_half_applied()
    {
        // A pair with no colon can't say what it maps to; taking a guess would be worse than skipping.
        var partial = Map("nonsense,/media/music:/music");

        partial.PlexPrefixes.Should().Equal("/media/music");
        partial.ToLocal("/media/music/a.flac").Should().Be("/music/a.flac");
    }

    [Fact]
    public void The_root_itself_maps()
    {
        Map().ToLocal("/media/music").Should().Be("/music");
    }

    [Fact]
    public void Blank_input_resolves_to_nothing()
    {
        Map().ToLocal(null).Should().BeNull();
        Map().ToLocal("   ").Should().BeNull();
    }
}
