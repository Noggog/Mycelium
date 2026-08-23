using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Background;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The worker that runs the half of a rate/seed/correction the user isn't waiting for. What matters
/// here is that queued work actually runs, that it runs in the order it was queued (a like followed by
/// a clear must not land backwards), and that one failing item doesn't stop everything queued after it.
/// </summary>
public class ArtistFollowUpServiceTests
{
    private const string User = "user-1";

    private readonly IVerdictFollowUp _engine = Substitute.For<IVerdictFollowUp>();
    private readonly IRelatedArtistReader _related = Substitute.For<IRelatedArtistReader>();
    private readonly IArtistTagger _tagger = Substitute.For<IArtistTagger>();

    private ArtistFollowUpService Sut() =>
        new(_engine, _related, _tagger, NullLogger<ArtistFollowUpService>.Instance);

    /// <summary>
    /// Runs the worker until <paramref name="done"/> holds (or the wait times out, failing the test
    /// rather than hanging the suite), then stops it.
    /// </summary>
    private static async Task Drain(ArtistFollowUpService sut, Func<bool> done)
    {
        await sut.StartAsync(CancellationToken.None);
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
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_queued_verdict_expands_the_frontier_and_writes_the_plex_tag()
    {
        var sut = Sut();
        var tagged = false;
        _tagger.When(t => t.SetTags("Feist", "noggog_liked", Arg.Any<IReadOnlyCollection<string>>()))
            .Do(_ => tagged = true);

        sut.QueueVerdictFollowUp(
            User, "Feist", DiscoveryStatus.Liked, depth: 2,
            addTag: "noggog_liked", removeTags: new[] { "noggog_disliked" });

        await Drain(sut, () => tagged);
        await _engine.Received(1).ApplyVerdictFollowUp(User, "Feist", DiscoveryStatus.Liked, 2);
    }

    [Fact]
    public async Task Work_runs_in_the_order_it_was_queued()
    {
        var sut = Sut();
        var order = new List<string>();
        _engine.ApplyVerdictFollowUp(User, "Feist", Arg.Any<DiscoveryStatus?>(), Arg.Any<int>())
            .Returns(call =>
            {
                order.Add(call.ArgAt<DiscoveryStatus?>(2) == DiscoveryStatus.Liked ? "like" : "clear");
                return Task.CompletedTask;
            });

        sut.QueueVerdictFollowUp(User, "Feist", DiscoveryStatus.Liked, 1, addTag: null, removeTags: Array.Empty<string>());
        sut.QueueVerdictFollowUp(User, "Feist", status: null, depth: 0, addTag: null, removeTags: Array.Empty<string>());

        await Drain(sut, () => order.Count == 2);
        order.Should().Equal("like", "clear");
    }

    [Fact]
    public async Task A_failing_item_is_skipped_and_the_queue_keeps_draining()
    {
        var sut = Sut();
        _engine.ApplyVerdictFollowUp(User, "Feist", Arg.Any<DiscoveryStatus?>(), Arg.Any<int>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Deezer is down")));

        sut.QueueVerdictFollowUp(User, "Feist", DiscoveryStatus.Liked, 1, addTag: null, removeTags: Array.Empty<string>());
        sut.QueueIdentityRefresh(User, "The Postal Service");

        await Drain(sut, () => _engine.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IVerdictFollowUp.Rebuild)));
        await _related.Received(1).GetRelated(new ArtistKey("The Postal Service"), true, Arg.Any<bool>());
    }

    [Fact]
    public async Task A_verdict_with_no_tags_to_write_never_touches_plex()
    {
        var sut = Sut();

        // No username on the session ⇒ no tag to stamp; the expansion still has to happen.
        sut.QueueVerdictFollowUp(User, "Feist", DiscoveryStatus.Liked, 1, addTag: null, removeTags: Array.Empty<string>());

        await Drain(sut, () => _engine.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IVerdictFollowUp.ApplyVerdictFollowUp)));
        await _tagger.DidNotReceive().SetTags(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>>());
    }
}
