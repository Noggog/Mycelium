using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

public class AlbumTitleMatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_normalizes_to_empty(string? title)
    {
        AlbumTitleMatcher.Normalize(title).Should().BeEmpty();
    }

    [Fact]
    public void Casing_and_surrounding_whitespace_are_folded_away()
    {
        AlbumTitleMatcher.Normalize("  Radiance  ").Should().Be("radiance");
    }

    [Fact]
    public void Internal_whitespace_is_collapsed_to_single_spaces()
    {
        AlbumTitleMatcher.Normalize("Radiance \t and\n\nSubmission").Should().Be("radiance and submission");
    }

    [Theory]
    [InlineData("Don’t Look Now")]  // curly apostrophe
    [InlineData("Donʼt Look Now")]  // modifier letter apostrophe
    [InlineData("Don′t Look Now")]  // prime
    public void Apostrophe_variants_fold_to_a_straight_quote(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("don't look now");
    }

    [Fact]
    public void Curly_double_quotes_fold_to_straight_quotes()
    {
        AlbumTitleMatcher.Normalize("“Heroes”").Should().Be("\"heroes\"");
    }

    [Theory]
    [InlineData("Live – 1975")]
    [InlineData("Live — 1975")]
    public void En_and_em_dashes_fold_to_a_hyphen(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("live - 1975");
    }

    [Fact]
    public void Zero_width_characters_are_stripped()
    {
        AlbumTitleMatcher.Normalize("﻿Rad​iance‍").Should().Be("radiance");
    }

    // The CFCF case: Plex and Deezer disagree on the ampersand convention, which used to make an
    // owned album look missing.
    [Theory]
    [InlineData("Radiance & Submission")]
    [InlineData("Radiance and Submission")]
    [InlineData("Radiance And Submission")]
    [InlineData("Radiance&Submission")]
    [InlineData("Radiance ＆ Submission")]
    public void Ampersand_and_the_word_and_normalize_to_the_same_title(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("radiance and submission");
    }

    [Theory]
    [InlineData("R&B Classics")]
    [InlineData("R & B Classics")]
    [InlineData("R and B Classics")]
    public void Ampersand_inside_a_word_is_padded_so_both_conventions_agree(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("r and b classics");
    }

    [Fact]
    public void A_leading_ampersand_gains_no_leading_space()
    {
        AlbumTitleMatcher.Normalize("& Then There Were Two").Should().Be("and then there were two");
    }

    [Fact]
    public void A_trailing_ampersand_gains_no_trailing_space()
    {
        AlbumTitleMatcher.Normalize("Me &").Should().Be("me and");
    }

    // Plex writes the EP designator one way, Deezer another — and one of them often leaves it off.
    [Theory]
    [InlineData("The Burgh Island EP")]
    [InlineData("The Burgh Island E.P.")]
    [InlineData("The Burgh Island e.p.")]
    [InlineData("The Burgh Island")]
    public void Ep_designators_and_their_dots_fold_away(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("the burgh island");
    }

    [Theory]
    [InlineData("The Old Pine E.P.")]
    [InlineData("The Old Pine")]
    public void A_trailing_ep_matches_the_bare_title(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("the old pine");
    }

    [Fact]
    public void A_dotted_initialism_inside_a_title_collapses()
    {
        AlbumTitleMatcher.Normalize("M.I.A. Sessions").Should().Be("mia sessions");
    }

    [Fact]
    public void A_lone_initial_keeps_its_dot()
    {
        AlbumTitleMatcher.Normalize("Mr. Bungle").Should().Be("mr. bungle");
    }

    // Reissue decoration the sources disagree on: the record is the same one either way.
    [Theory]
    [InlineData("I Forgot Where We Were (10th Anniversary Deluxe)")]
    [InlineData("I Forgot Where We Were [10th Anniversary Deluxe]")]
    [InlineData("I Forgot Where We Were")]
    public void An_anniversary_edition_matches_the_plain_release(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("i forgot where we were");
    }

    [Theory]
    [InlineData("Every Kingdom (Deluxe Edition)")]
    [InlineData("Every Kingdom (Bonus Track Version)")]
    [InlineData("Every Kingdom - Remastered")]
    [InlineData("Every Kingdom (Deluxe Edition) [Remastered]")]
    [InlineData("Every Kingdom")]
    public void Edition_qualifiers_fold_away(string title)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be("every kingdom");
    }

    // A qualifier word is only decoration when the whole tail is decoration — otherwise it is title.
    [Theory]
    [InlineData("Sound Kapital (Clean Slate)", "sound kapital (clean slate)")]
    [InlineData("Celebration Rock (Live)", "celebration rock (live)")]
    [InlineData("Live - 1975", "live - 1975")]
    [InlineData("Post-Nothing", "post-nothing")]
    public void A_tail_that_carries_meaning_is_kept(string title, string expected)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be(expected);
    }

    // Stripping never eats the whole name: these albums really are called this.
    [Theory]
    [InlineData("EP", "ep")]
    [InlineData("Deluxe", "deluxe")]
    [InlineData("(Deluxe Edition)", "(deluxe edition)")]
    public void A_title_that_is_only_a_qualifier_survives(string title, string expected)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be(expected);
    }

    // Two pressings of one record: the same album to own, two rows in an artist's discography.
    [Fact]
    public void Edition_keys_tell_pressings_of_one_record_apart()
    {
        AlbumTitleMatcher.NormalizeEdition("Both Sides (Deluxe Edition)")
            .Should().NotBe(AlbumTitleMatcher.NormalizeEdition("Both Sides (2015 Remaster)"));
        // ...while the record they're both pressings of is still one album.
        AlbumTitleMatcher.Normalize("Both Sides (Deluxe Edition)")
            .Should().Be(AlbumTitleMatcher.Normalize("Both Sides (2015 Remaster)"));
    }

    [Theory]
    [InlineData("Don’t Look Now [Deluxe Edition]")]
    [InlineData("  Don't  Look  Now  [Deluxe Edition]  ")]
    public void An_edition_key_still_folds_typography(string title)
    {
        // Keeping the decoration is not the same as taking the title verbatim: a source writing the
        // same pressing with a curly apostrophe hasn't listed a second release.
        AlbumTitleMatcher.NormalizeEdition(title).Should().Be("don't look now [deluxe edition]");
    }

    [Fact]
    public void Distinct_titles_still_normalize_differently()
    {
        AlbumTitleMatcher.Normalize("Radiance")
            .Should().NotBe(AlbumTitleMatcher.Normalize("Radiance & Submission"));
    }

    [Fact]
    public void Override_keys_agree_across_the_ampersand_swap()
    {
        // The purchase reconcile and the missing-album diff key off the same normalized form, so a
        // merge recorded under one convention has to be honoured under the other.
        AlbumOverrideKey.For("CFCF", "Radiance & Submission")
            .Should().Be(AlbumOverrideKey.For("cfcf", "Radiance and Submission"));
    }

    [Theory]
    [InlineData("Animal (Expanded Edition)", "animal")]
    [InlineData("Settle (Special Edition)", "settle")]
    [InlineData("I Forgot Where We Were (Tenth Anniversary Edition)", "i forgot where we were")]
    [InlineData("I Forgot Where We Were [Twentieth Anniversary Edition]", "i forgot where we were")]
    [InlineData("I Forgot Where We Were - Twenty-Fifth Anniversary", "i forgot where we were")]
    public void Spelled_out_ordinals_fold_away_like_their_digit_form(string title, string expected)
    {
        AlbumTitleMatcher.Normalize(title).Should().Be(expected);
    }

    [Fact]
    public void Override_keys_agree_across_an_edition_suffix()
    {
        AlbumOverrideKey.For("Ben Howard", "Every Kingdom (Deluxe Edition)")
            .Should().Be(AlbumOverrideKey.For("ben howard", "Every Kingdom"));
    }
}
