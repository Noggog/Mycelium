using FluentAssertions;
using Mycelium.Backend.Services.Singletons;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// Resolving "how good a copy is this user allowed?". Three callers depend on these answers agreeing
/// — the reconcile that stamps a row's target, the feed that decides whether to offer an upgrade, and
/// the sync that diffs against the best tier anyone holds — so the defaulting rules live in one place
/// and are pinned here.
/// </summary>
public class UserQualityServiceTests
{
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();

    private UserQualityService Sut(AudioQuality defaultQuality = AudioQuality.Lossy)
    {
        _users.GetAll().Returns(Array.Empty<AppUser>());
        return new UserQualityService(_users, defaultQuality);
    }

    private void User(string subject, AudioQuality? quality)
    {
        var user = new AppUser(subject, subject, null, subject, default, default, quality);
        _users.Get(subject).Returns(user);
        _users.GetAll().Returns(new[] { user });
    }

    [Fact]
    public async Task A_user_with_no_tier_gets_the_deployment_default()
    {
        var sut = Sut(AudioQuality.Lossy);
        User("kelsey", null);

        (await sut.For("kelsey")).Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public async Task An_explicit_tier_beats_the_default()
    {
        var sut = Sut(AudioQuality.Lossy);
        User("justin", AudioQuality.Lossless);

        (await sut.For("justin")).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task An_unknown_subject_falls_back_rather_than_resolving_to_nothing()
    {
        // A stray id should be un-privileged, not un-downloadable.
        var sut = Sut(AudioQuality.Lossy);

        (await sut.For("nobody")).Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public async Task A_shared_album_is_fetched_at_the_best_entitlement_among_its_likers()
    {
        var sut = Sut(AudioQuality.Lossy);
        var kelsey = new AppUser("kelsey", "kelsey", null, null, default, default, AudioQuality.Lossy);
        var justin = new AppUser("justin", "justin", null, null, default, default, AudioQuality.Lossless);
        _users.Get("kelsey").Returns(kelsey);
        _users.Get("justin").Returns(justin);

        // The purchase list is global: the album is downloaded once. Taking the best entitlement
        // means the lossy user rides along for free, where taking the worst would quietly cheat the
        // lossless one out of what they're entitled to.
        (await sut.BestOf(new[] { "kelsey", "justin" })).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task Nobody_wanting_it_falls_back_to_the_default()
    {
        var sut = Sut(AudioQuality.Lossy);

        (await sut.BestOf(Array.Empty<string>())).Should().Be(AudioQuality.Lossy);
    }

    [Fact]
    public async Task The_ceiling_is_the_best_tier_anyone_holds()
    {
        // What the missing-album sync diffs against: it walks Deezer once for the whole library and
        // can't ask per-user, so it must produce a superset the per-user feed can filter down.
        var sut = Sut(AudioQuality.Lossy);
        _users.GetAll().Returns(new[]
        {
            new AppUser("kelsey", "kelsey", null, null, default, default, AudioQuality.Lossy),
            new AppUser("justin", "justin", null, null, default, default, AudioQuality.Lossless),
        });

        (await sut.Ceiling()).Should().Be(AudioQuality.Lossless);
    }

    [Fact]
    public async Task The_ceiling_never_drops_below_the_default()
    {
        // Everyone explicitly lossy, but the deployment default is lossless: a user created tomorrow
        // would out-rank all of them, so the sync still has to have diffed for it.
        var sut = Sut(AudioQuality.Lossless);
        _users.GetAll().Returns(new[]
        {
            new AppUser("kelsey", "kelsey", null, null, default, default, AudioQuality.Lossy),
        });

        (await sut.Ceiling()).Should().Be(AudioQuality.Lossless);
    }
}
