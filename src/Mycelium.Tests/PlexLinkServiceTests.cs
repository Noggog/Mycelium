using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using Mycelium.Plex.Services.Singletons;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The pasted-token link path. Everything here turns on one rule: nothing is written until plex.tv has
/// said who the token belongs to and that they can reach this server — a stored-but-unusable token
/// would fail every later playlist call with no way to tell it from an outage.
/// </summary>
public class PlexLinkServiceTests
{
    private const string Subject = "oidc-subject";
    private const string MachineId = "machine-1";
    private const string Token = "plex-token-abc";

    private readonly IPlexLinkRepo _links = Substitute.For<IPlexLinkRepo>();
    private readonly IPlexAccountApi _accounts = Substitute.For<IPlexAccountApi>();
    private readonly IPlexApi _plexApi = Substitute.For<IPlexApi>();
    private readonly PlexLinkService _sut;

    public PlexLinkServiceTests()
    {
        _sut = new PlexLinkService(_links, _accounts, _plexApi, NullLogger<PlexLinkService>.Instance);
        _plexApi.GetMachineIdentifier().Returns(MachineId);
        _plexApi.AcceptsToken(Arg.Any<string>()).Returns(false);
        _links.Get(Subject).Returns((PlexLink?)null);
    }

    [Fact]
    public async Task ValidToken_StoresTheServerScopedTokenAndReportsLinked()
    {
        _accounts.ResolveAccount(Token, MachineId)
            .Returns(new PlexAccount("42", "kelsey", "k@example.com", "server-scoped"));

        var result = await _sut.LinkWithToken(Subject, Token);

        result.Outcome.Should().Be(PlexLinkOutcome.Linked);
        result.Status.Linked.Should().BeTrue();
        result.Status.Username.Should().Be("kelsey");
        // The pasted token is used once to ask who it belongs to; only the narrower server-scoped one
        // that plex.tv hands back is ever persisted.
        await _links.Received(1).Upsert(Arg.Is<PlexLink>(l =>
            l.Subject == Subject && l.ServerToken == "server-scoped" && l.Username == "kelsey"));
    }

    [Fact]
    public async Task TokenIsTrimmedBeforeUse()
    {
        // Selecting a token in a browser almost always takes trailing whitespace with it.
        _accounts.ResolveAccount(Token, MachineId)
            .Returns(new PlexAccount("42", "kelsey", null, "server-scoped"));

        var result = await _sut.LinkWithToken(Subject, $"  {Token}\n");

        result.Outcome.Should().Be(PlexLinkOutcome.Linked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyToken_IsRefusedWithoutCallingPlex(string? token)
    {
        var result = await _sut.LinkWithToken(Subject, token);

        result.Outcome.Should().Be(PlexLinkOutcome.InvalidToken);
        await _accounts.DidNotReceive().ResolveAccount(Arg.Any<string>(), Arg.Any<string>());
        await _links.DidNotReceive().Upsert(Arg.Any<PlexLink>());
    }

    /// <summary>Makes plex.tv disown the token, which is what sends the link to the server fallback.</summary>
    private void PlexTvRejectsIt(HttpStatusCode status = HttpStatusCode.Unauthorized) =>
        _accounts.ResolveAccount(Token, MachineId)
            .Returns<PlexAccount?>(_ => throw new HttpRequestException("nope", null, status));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RejectedByBothPlexTvAndTheServer_ReportsInvalidTokenAndStoresNothing(
        HttpStatusCode status)
    {
        PlexTvRejectsIt(status);

        var result = await _sut.LinkWithToken(Subject, Token);

        result.Outcome.Should().Be(PlexLinkOutcome.InvalidToken);
        await _links.DidNotReceive().Upsert(Arg.Any<PlexLink>());
    }

    [Fact]
    public async Task ServerTokenPlexTvDisowns_IsStillLinkedIfTheServerAcceptsIt()
    {
        // The case the whole fallback exists for: a Plex *server* access token, which is the only kind
        // a Home / managed user can hand you. plex.tv has never heard of it; the server takes it.
        PlexTvRejectsIt();
        _plexApi.AcceptsToken(Token).Returns(true);

        var result = await _sut.LinkWithToken(Subject, Token, "Kelsey");

        result.Outcome.Should().Be(PlexLinkOutcome.Linked);
        result.Status.Username.Should().Be("Kelsey");
        await _links.Received(1).Upsert(Arg.Is<PlexLink>(l => l.ServerToken == Token));
    }

    [Fact]
    public async Task ServerTokenWithNoLabel_GetsAPlaceholderRatherThanAnInventedIdentity()
    {
        // The server verifies the token but reports the *owner* whatever token asks, so there is no
        // name to be had. Better an obvious placeholder than a plausible-looking wrong one.
        PlexTvRejectsIt();
        _plexApi.AcceptsToken(Token).Returns(true);

        var result = await _sut.LinkWithToken(Subject, Token, "   ");

        result.Outcome.Should().Be(PlexLinkOutcome.Linked);
        result.Status.Username.Should().Be("Plex (token)");
        result.Status.Email.Should().BeNull();
    }

    [Fact]
    public async Task AccountTokenNeverReachesTheServerFallback()
    {
        // plex.tv identified it, so the narrower server-scoped token it returned is what gets stored —
        // the pasted account-wide token must not be persisted just because the server would take it.
        _accounts.ResolveAccount(Token, MachineId)
            .Returns(new PlexAccount("42", "kelsey", null, "server-scoped"));

        await _sut.LinkWithToken(Subject, Token);

        await _plexApi.DidNotReceive().AcceptsToken(Arg.Any<string>());
        await _links.Received(1).Upsert(Arg.Is<PlexLink>(l => l.ServerToken == "server-scoped"));
    }

    [Fact]
    public async Task PlexTvOutage_Throws_RatherThanBlamingTheToken()
    {
        // A 500 from plex.tv is not the same answer as "that token is wrong", and telling the user to
        // re-copy a token that was fine would send them chasing the wrong problem.
        _accounts.ResolveAccount(Token, MachineId).Returns<PlexAccount?>(
            _ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));

        await _sut.Invoking(s => s.LinkWithToken(Subject, Token))
            .Should().ThrowAsync<HttpRequestException>();
        await _links.DidNotReceive().Upsert(Arg.Any<PlexLink>());
    }

    [Fact]
    public async Task AccountWithoutServerAccess_IsRefused()
    {
        // The token is real, but nothing this app creates would ever be visible to that account.
        _accounts.ResolveAccount(Token, MachineId).Returns((PlexAccount?)null);

        var result = await _sut.LinkWithToken(Subject, Token);

        result.Outcome.Should().Be(PlexLinkOutcome.NoServerAccess);
        await _links.DidNotReceive().Upsert(Arg.Any<PlexLink>());
    }

    [Fact]
    public async Task FailedPaste_LeavesAnExistingLinkReportedAsItStands()
    {
        // Re-linking is how you switch accounts; a rejected attempt must not report the link the user
        // still has as gone.
        _links.Get(Subject).Returns(new PlexLink(
            Subject, "7", "existing", null, "existing-token", DateTimeOffset.UnixEpoch));
        _accounts.ResolveAccount(Token, MachineId).Returns((PlexAccount?)null);

        var result = await _sut.LinkWithToken(Subject, Token);

        result.Outcome.Should().Be(PlexLinkOutcome.NoServerAccess);
        result.Status.Linked.Should().BeTrue();
        result.Status.Username.Should().Be("existing");
    }

    [Fact]
    public async Task UnreachableServer_Throws_SinceTheLinkCannotBeVerified()
    {
        _plexApi.GetMachineIdentifier().Returns((string?)null);

        await _sut.Invoking(s => s.LinkWithToken(Subject, Token))
            .Should().ThrowAsync<InvalidOperationException>();
        await _links.DidNotReceive().Upsert(Arg.Any<PlexLink>());
    }
}
