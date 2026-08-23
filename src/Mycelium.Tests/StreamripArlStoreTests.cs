using FluentAssertions;
using Mycelium.Backend.Services.Download;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The ARL rewrite edits a file streamrip owns and reads far more of than we do, so the contract
/// under test is "change exactly one assignment and nothing else". A round-trip through a TOML writer
/// would be the obvious implementation and the dangerous one — dropping a comment or a key here
/// breaks downloads in a way that looks unrelated to having pasted a credential.
/// </summary>
public class StreamripArlStoreTests
{
    // Trimmed from streamrip 2.1.0's shipped config: two sources with credential keys, comments
    // between them, and a [deezer] table that is not the first table in the file.
    private const string Config =
        """
        [qobuz]
        quality = 3
        email_or_userid = "someone@example.com"
        password_or_token = "hunter2"

        [deezer]
        # 0, 1, or 2
        quality = 2
        # An authentication cookie that allows streamrip to use your Deezer account
        arl = "old-token"
        use_deezloader = true

        [soundcloud]
        client_id = ""
        """;

    [Fact]
    public void Replacing_the_arl_changes_that_line_and_nothing_else()
    {
        StreamripArlStore.TryReplaceArl(Config, "fresh-token", out var updated).Should().BeTrue();

        updated.Should().Be(Config.Replace("arl = \"old-token\"", "arl = \"fresh-token\""));
        // Spelled out because the diff above would also pass if the file were rebuilt identically —
        // these are the parts a naive TOML round-trip actually loses.
        updated.Should().Contain("# 0, 1, or 2");
        updated.Should().Contain("use_deezloader = true");
        updated.Should().Contain("[soundcloud]");
    }

    [Fact]
    public void The_qobuz_credentials_are_left_alone()
    {
        // A whole-file substitution would eventually reach for the wrong source's credential key.
        // Anchoring to the [deezer] table is what stops that.
        StreamripArlStore.TryReplaceArl(Config, "fresh-token", out var updated).Should().BeTrue();

        updated.Should().Contain("password_or_token = \"hunter2\"");
        updated.Should().Contain("email_or_userid = \"someone@example.com\"");
    }

    [Fact]
    public void An_arl_outside_the_deezer_table_is_not_mistaken_for_the_real_one()
    {
        const string decoy =
            """
            [tidal]
            arl = "not-the-deezer-one"

            [deezer]
            arl = "old-token"
            """;

        StreamripArlStore.TryReplaceArl(decoy, "fresh-token", out var updated).Should().BeTrue();

        updated.Should().Contain("arl = \"not-the-deezer-one\"");
        updated.Should().Contain("arl = \"fresh-token\"");
    }

    [Fact]
    public void A_config_with_no_deezer_table_is_refused_rather_than_appended_to()
    {
        // Better to tell the user their config is unexpected than to guess at a shape streamrip may
        // not read — a silently ignored credential looks exactly like an expired one.
        StreamripArlStore.TryReplaceArl("[qobuz]\nquality = 3\n", "fresh", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("arl = \"old\"", "old")]
    [InlineData("arl='single-quoted'", "single-quoted")]
    [InlineData("   arl   =   \"spaced\"   ", "spaced")]
    [InlineData("arl = \"\"", "")]
    public void The_current_arl_is_read_back_whatever_the_spacing_or_quote_style(string line, string expected)
    {
        // The file may have been hand-edited, so the reader can't assume the shipped formatting.
        StreamripArlStore.FindArl($"[deezer]\n{line}\n").Should().Be(expected);
    }

    [Fact]
    public void The_last_table_in_the_file_still_resolves()
    {
        // The [deezer] table ends at EOF rather than at another header — an off-by-one here would make
        // the app report "no arl setting found" on a perfectly valid config.
        StreamripArlStore.FindArl("[qobuz]\nquality = 3\n\n[deezer]\narl = \"tail\"\n").Should().Be("tail");
    }
}
