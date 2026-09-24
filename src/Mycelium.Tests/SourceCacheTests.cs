using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mycelium.Backend.Services.Singletons;
using Xunit;

namespace Mycelium.Tests;

public class SourceCacheTests
{
    private const string Key = "musicbrainz:release-groups:vulture";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly FakeSourceCacheStore _store = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
    private readonly SourceCache _sut;

    private int _fetches;
    private Answer? _next = new("first");

    public SourceCacheTests()
    {
        _sut = NewCache();
    }

    /// <summary>A cache over the same store — what the app has after a restart.</summary>
    private SourceCache NewCache() => new(_store, NullLogger<SourceCache>.Instance, _clock);

    public record Answer(string Value);

    private Task<Answer?> Fetch()
    {
        _fetches++;
        return Task.FromResult(_next);
    }

    private Task<Answer?> Get(SourceCache cache, bool fresh = false) =>
        cache.GetOrFetch(Key, Fetch, _ => Lifetime, fresh);

    [Fact]
    public async Task A_miss_fetches_once_and_persists_the_answer_with_its_expiry()
    {
        (await Get(_sut))!.Value.Should().Be("first");
        (await Get(_sut))!.Value.Should().Be("first");

        _fetches.Should().Be(1);
        var entry = _store.Entries[Key];
        entry.FetchedAt.Should().Be(_clock.Now);
        entry.ExpiresAt.Should().Be(_clock.Now + Lifetime);
    }

    [Fact]
    public async Task After_a_restart_the_answer_comes_from_the_store_without_fetching()
    {
        await Get(_sut);

        var restarted = NewCache();
        (await Get(restarted))!.Value.Should().Be("first");

        _fetches.Should().Be(1);
    }

    [Fact]
    public async Task An_expired_answer_is_still_served_while_a_refresh_runs_behind_it()
    {
        await Get(_sut);
        _clock.Advance(Lifetime + TimeSpan.FromHours(1));
        _next = new Answer("second");

        (await Get(_sut))!.Value.Should().Be("first");
        await _sut.WhenIdle();

        _fetches.Should().Be(2);
        (await Get(_sut))!.Value.Should().Be("second");
        _store.Entries[Key].ExpiresAt.Should().Be(_clock.Now + Lifetime);
    }

    [Fact]
    public async Task Expiry_carries_across_a_restart_on_each_entrys_own_clock()
    {
        await Get(_sut);
        _clock.Advance(Lifetime + TimeSpan.FromHours(1));
        _next = new Answer("second");

        var restarted = NewCache();
        (await Get(restarted))!.Value.Should().Be("first");
        await restarted.WhenIdle();

        (await Get(restarted))!.Value.Should().Be("second");
    }

    [Fact]
    public async Task A_source_that_does_not_answer_never_replaces_the_cached_answer()
    {
        await Get(_sut);
        _clock.Advance(Lifetime + TimeSpan.FromHours(1));
        _next = null;

        (await Get(_sut))!.Value.Should().Be("first");
        await _sut.WhenIdle();
        (await Get(_sut, fresh: true))!.Value.Should().Be("first");

        _store.Entries[Key].Json.Should().Contain("first");
    }

    [Fact]
    public async Task Nothing_cached_and_no_answer_is_null_and_stores_nothing()
    {
        _next = null;

        (await Get(_sut)).Should().BeNull();
        _store.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fresh_read_asks_the_source_even_when_the_answer_has_not_expired()
    {
        await Get(_sut);
        _next = new Answer("corrected");

        (await Get(_sut, fresh: true))!.Value.Should().Be("corrected");
        _fetches.Should().Be(2);
    }

    [Fact]
    public async Task Expiring_an_entry_serves_it_once_more_and_refreshes_it()
    {
        await Get(_sut);
        _next = new Answer("second");

        await _sut.Expire(Key);
        (await Get(_sut))!.Value.Should().Be("first");
        await _sut.WhenIdle();

        (await Get(_sut))!.Value.Should().Be("second");
    }

    [Fact]
    public async Task Concurrent_reads_of_a_stale_entry_refresh_it_once()
    {
        await Get(_sut);
        _clock.Advance(Lifetime + TimeSpan.FromHours(1));
        var release = new TaskCompletionSource<Answer?>();
        var refreshes = 0;
        Task<Answer?> Slow()
        {
            refreshes++;
            return release.Task;
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _sut.GetOrFetch(Key, Slow, _ => Lifetime)));
        release.SetResult(new Answer("second"));
        await _sut.WhenIdle();

        refreshes.Should().Be(1);
    }
}
