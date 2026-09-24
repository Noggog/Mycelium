using FluentAssertions;
using MongoDB.Bson;
using Mycelium.Interfaces;
using Mycelium.MongoDB.Services.Data;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The verdict store's case-insensitive key, and the one-time merge of rows written before it:
/// "the Beaches" (Deezer), "The Beaches" (Plex) and "THE BEACHES" (a playlist) are one artist.
/// </summary>
public class UserQueueCaseMergeTests
{
    private static readonly DateTime Aug10 = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Aug22 = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_spelling_of_an_artist_shares_one_row()
    {
        UserQueueRepo.DocId("u", "the Beaches").Should().Be(UserQueueRepo.DocId("u", "The Beaches"))
            .And.Be(UserQueueRepo.DocId("u", "THE BEACHES"));
    }

    [Fact]
    public void Rows_stay_per_user()
    {
        UserQueueRepo.DocId("a", "Fisher").Should().NotBe(UserQueueRepo.DocId("b", "Fisher"));
    }

    [Fact]
    public void The_latest_verdict_wins_a_conflict()
    {
        var merged = UserQueueRepo.MergeDocs(new[]
        {
            Row("the Beaches", "Liked", decidedAt: Aug10),
            Row("The Beaches", "Disliked", decidedAt: Aug22),
        });

        merged["status"].AsString.Should().Be("Disliked");
        merged["artist"].AsString.Should().Be("The Beaches");
        merged["decidedAt"].ToUniversalTime().Should().Be(Aug22);
    }

    [Fact]
    public void A_decided_row_beats_a_pending_one_however_it_scores()
    {
        var merged = UserQueueRepo.MergeDocs(new[]
        {
            Row("NAO", "Pending", score: 50),
            Row("Nao", "Liked", decidedAt: Aug10, score: 1),
        });

        merged["status"].AsString.Should().Be("Liked");
    }

    [Fact]
    public void Flags_travel_with_the_winning_verdict()
    {
        var winner = Row("Fisher", "Liked", decidedAt: Aug22);
        winner["likeConfirmed"] = true;
        var loser = Row("FISHER", "Disliked", decidedAt: Aug10);
        loser["dislikeConfirmed"] = true;

        var merged = UserQueueRepo.MergeDocs(new[] { loser, winner });

        merged.Contains("likeConfirmed").Should().BeTrue();
        merged.Contains("dislikeConfirmed").Should().BeFalse();
    }

    [Fact]
    public void Sightings_are_pooled_across_every_spelling()
    {
        var merged = UserQueueRepo.MergeDocs(new[]
        {
            Row("NAO", "Pending", score: 1, depth: 2, sources: ["A"], addedAt: Aug22),
            Row("Nao", "Pending", score: 3, depth: 1, sources: ["B", "A"], addedAt: Aug10),
        });

        merged["artist"].AsString.Should().Be("Nao", "the higher-scored pending row leads");
        merged["score"].ToDouble().Should().Be(4);
        merged["depth"].ToInt32().Should().Be(1);
        merged["sources"].AsBsonArray.Select(s => s.AsString).Should().BeEquivalentTo("A", "B");
        merged["addedAt"].ToUniversalTime().Should().Be(Aug10);
    }

    [Fact]
    public void A_missing_image_is_filled_from_another_spelling()
    {
        var withImage = Row("Emlyn", "Pending", score: 1);
        withImage["imageUrl"] = "https://img";

        var merged = UserQueueRepo.MergeDocs(new[] { Row("emlyn", "Liked", decidedAt: Aug10), withImage });

        merged["imageUrl"].AsString.Should().Be("https://img");
    }

    [Fact]
    public void Spellings_in_one_batch_fold_into_one_sighting()
    {
        var folded = UserQueueRepo.MergeSpellings(new[]
        {
            new DiscoveryCandidate(new ArtistKey("Haerts"), null, 1, ["A"], 2),
            new DiscoveryCandidate(new ArtistKey("HAERTS"), "https://img", 2, ["B"], 1),
            new DiscoveryCandidate(new ArtistKey("Lanks"), null, 5, ["A"], 1),
        }).ToList();

        folded.Should().HaveCount(2);
        var haerts = folded.Single(c => c.Artist.ArtistName == "Haerts");
        haerts.Score.Should().Be(3);
        haerts.Depth.Should().Be(1);
        haerts.ImageUrl.Should().Be("https://img");
        haerts.Sources.Should().BeEquivalentTo("A", "B");
    }

    private static BsonDocument Row(
        string artist, string status, DateTime? decidedAt = null, double score = 0, int depth = 0,
        string[]? sources = null, DateTime? addedAt = null)
    {
        var doc = new BsonDocument
        {
            ["_id"] = $"u:{artist}",
            ["userId"] = "u",
            ["artist"] = artist,
            ["status"] = status,
            ["score"] = score,
            ["depth"] = depth,
        };
        if (decidedAt is { } d) doc["decidedAt"] = d;
        if (sources != null) doc["sources"] = new BsonArray(sources);
        if (addedAt is { } a) doc["addedAt"] = a;
        return doc;
    }
}
