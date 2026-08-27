using System.Security.Claims;
using Autofac;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycelium.Backend;
using Mycelium.Backend.Services.Auth;
using Mycelium.Interfaces;
using NSubstitute;
using Xunit;

namespace Mycelium.Tests;

/// <summary>
/// The long-lived API tokens that let the seeding and acquisition scripts run without a human at a
/// browser. Everything asserted here is a security property, and each one fails silently rather than
/// loudly if it regresses — which is why they are pinned:
///
/// <list type="bullet">
/// <item>the principal a token builds must carry <c>preferred_username</c>, because the Plex mood
/// tags are derived from it and <c>ArtistTag.For</c> answers null (not an error) without one — a
/// token missing it would rate happily and tag nothing;</item>
/// <item>a token must not reach the dev endpoints just because its owner is a dev, since those
/// include stripping every verdict tag off the whole library;</item>
/// <item>the stored form must not be the token, and no rejection path may write the token anywhere.</item>
/// </list>
/// </summary>
public class ApiTokenTests
{
    private const string Subject = "oidc-sub-1";
    private const string Username = "noggog";

    private readonly FakeApiTokenRepo _repo = new();
    private readonly IUserRepo _users = Substitute.For<IUserRepo>();
    private readonly CapturingLogger<ApiTokenService> _log = new();
    private readonly ApiTokenService _sut;

    public ApiTokenTests()
    {
        _sut = new ApiTokenService(_repo, _users, _log);
        User(Subject, Username);
    }

    private void User(string subject, string? username, string? email = null, string? displayName = null) =>
        _users.Get(subject).Returns(new AppUser(subject, username, email, displayName, default, default));

    /// <summary>Mints a token for the default user and hands back the string a client would send.</summary>
    private async Task<string> Mint(bool dev = false, TimeSpan? lifetime = null)
    {
        var result = await _sut.Mint(Subject, "seed script", dev, lifetime);
        result.Error.Should().BeNull();
        return result.Minted!.Token;
    }

    // ---- The happy path, and the claims everything downstream reads ----

    [Fact]
    public async Task A_valid_token_authenticates_as_the_user_it_was_minted_for()
    {
        var verdict = await _sut.Verify(await Mint());

        verdict.Failure.Should().BeNull();
        verdict.Principal.Should().NotBeNull();
        // GetSubject() is how every per-user endpoint in the app finds out whose ratings it is
        // writing. If this were absent the endpoints would null-deref on their `!` rather than 401.
        verdict.Principal!.GetSubject().Should().Be(Subject);
    }

    [Fact]
    public async Task The_principal_carries_the_preferred_username_the_Plex_mood_tags_are_built_from()
    {
        User(Subject, Username, email: "someone@example.com", displayName: "Justin");

        var principal = (await _sut.Verify(await Mint())).Principal!;

        principal.FindFirst("preferred_username")?.Value.Should().Be(Username);
        // The claim on its own isn't the property that matters — this is. A principal that reached
        // ArtistTag.For without a username would return null, and a null tag is skipped in silence:
        // the script would rate for weeks and stamp nothing, and the first symptom would be an empty
        // smart playlist.
        ArtistTag.For(principal.FindFirst("preferred_username")?.Value, DiscoveryStatus.Liked)
            .Should().Be("noggog_liked");
        // Parity with the cookie identity, which the SPA reads off /auth/me.
        principal.FindFirst("email")?.Value.Should().Be("someone@example.com");
        principal.FindFirst("name")?.Value.Should().Be("Justin");
    }

    [Fact]
    public async Task The_claims_track_the_users_current_profile_rather_than_mint_time()
    {
        var token = await Mint();

        // The user renames themselves at the IdP and signs in, refreshing the store.
        User(Subject, "noggog2");

        (await _sut.Verify(token)).Principal!
            .FindFirst("preferred_username")?.Value.Should().Be("noggog2");
    }

    // ---- Everything that must be a clean refusal ----

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        // Well-formed, but minted nowhere — the shape a token from another deployment would have.
        var stranger = ApiTokenDefaults.Generate().Token;

        var verdict = await _sut.Verify(stranger);

        verdict.Principal.Should().BeNull();
        verdict.Failure.Should().Be(ApiTokenFailure.Unknown);
    }

    [Fact]
    public async Task A_token_that_names_a_real_id_with_the_wrong_secret_is_refused()
    {
        var real = await Mint();
        var id = real[ApiTokenDefaults.Prefix.Length..real.IndexOf('.')];
        var forged = $"{ApiTokenDefaults.Prefix}{id}.{ApiTokenDefaults.Generate().Token.Split('.')[1]}";

        (await _sut.Verify(forged)).Failure.Should().Be(ApiTokenFailure.BadSecret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    // A session cookie value pasted where the token goes — the exact mistake this feature invites.
    [InlineData("myc.auth=CfDJ8Fake")]
    [InlineData("myc_")]
    [InlineData("myc_abc")]
    [InlineData("myc_abc.")]
    [InlineData("myc_.secret")]
    // Not base64url, so decoding it would throw if the parse weren't defensive.
    [InlineData("myc_abc.not valid base64!!")]
    public async Task A_malformed_token_is_a_clean_refusal_and_never_an_exception(string presented)
    {
        var verdict = await _sut.Verify(presented);

        // The requirement is specifically that this is a 401 and not a 500: an exception escaping
        // here surfaces through the authentication middleware as a server error, which tells an
        // operator their deployment is broken when in fact a client sent rubbish.
        verdict.Principal.Should().BeNull();
        verdict.Failure.Should().Be(ApiTokenFailure.Malformed);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var result = await _sut.Mint(Subject, "seed script", devScope: false, lifetime: null);
        var token = result.Minted!.Token;
        (await _sut.Verify(token)).Failure.Should().BeNull("it works before being revoked");

        (await _sut.Revoke(Subject, result.Minted.Id)).Should().BeTrue();

        (await _sut.Verify(token)).Failure.Should().Be(ApiTokenFailure.Revoked);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        // Minted with a lifetime, then walked past it by writing the row's expiry into the past —
        // the store is the clock's only input here, so this is the same state a real lapse produces.
        var result = await _sut.Mint(Subject, "seed script", devScope: false, lifetime: TimeSpan.FromDays(30));
        var row = _repo.Rows[result.Minted!.Id];
        _repo.Rows[row.Id] = row with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        (await _sut.Verify(result.Minted.Token)).Failure.Should().Be(ApiTokenFailure.Expired);
    }

    [Fact]
    public async Task A_token_inside_its_lifetime_still_works()
    {
        // The other half of the expiry test: an expiry that hasn't arrived must not be treated as one
        // that has, or every token with a lifetime would be dead on arrival.
        (await _sut.Verify(await Mint(lifetime: TimeSpan.FromDays(30)))).Failure.Should().BeNull();
    }

    [Fact]
    public async Task A_token_for_a_user_who_is_no_longer_in_the_store_is_refused()
    {
        var token = await Mint();
        _users.Get(Subject).Returns((AppUser?)null);

        (await _sut.Verify(token)).Failure.Should().Be(ApiTokenFailure.NoSuchUser);
    }

    [Fact]
    public async Task A_token_whose_user_lost_their_username_fails_closed()
    {
        // Failing closed rather than authenticating: a principal with no username writes no mood
        // tags, and does so silently. A 401 is debuggable; weeks of untagged ratings are not.
        var token = await Mint();
        User(Subject, username: null);

        (await _sut.Verify(token)).Failure.Should().Be(ApiTokenFailure.NoUsername);
    }

    // ---- Storage: the token itself must not survive minting ----

    [Fact]
    public async Task The_token_is_stored_hashed_and_the_value_itself_is_kept_nowhere()
    {
        var result = await _sut.Mint(Subject, "seed script", devScope: false, lifetime: null);
        var token = result.Minted!.Token;
        var row = _repo.Rows.Values.Single();

        row.SecretHash.Should().HaveCount(32, "a SHA-256 digest");
        // Nothing on the stored row may contain the credential — not the hash read as text, not the
        // label, not the id. A dump of this collection has to be worthless to whoever takes it.
        var stored = string.Join('|', row.Id, row.Subject, row.Name, Convert.ToHexString(row.SecretHash));
        stored.Should().NotContain(token);
        stored.Should().NotContain(token.Split('.')[1], "the secret half is the part worth stealing");
    }

    [Fact]
    public async Task Two_tokens_are_never_the_same()
    {
        (await Mint()).Should().NotBe(await Mint());
    }

    [Fact]
    public async Task Rejecting_a_token_never_writes_its_value_to_the_log()
    {
        // Deliberately across every rejection path, because the failure path is where a credential
        // most plausibly leaks — someone logs "couldn't authenticate {Token}" while debugging and it
        // is never taken back out.
        var live = await Mint();
        var result = await _sut.Mint(Subject, "second", devScope: false, lifetime: null);
        await _sut.Revoke(Subject, result.Minted!.Id);

        var presented = new[] { live, result.Minted.Token, ApiTokenDefaults.Generate().Token, "garbage" };
        foreach (var token in presented)
        {
            await _sut.Verify(token);
        }

        var everythingLogged = string.Join('\n', _log.Lines);
        foreach (var token in presented)
        {
            everythingLogged.Should().NotContain(token);
            everythingLogged.Should().NotContain(token.Split('.').Last());
        }

        // ...and the log is still useful: a rejection names the token's public id, which is what
        // makes "which of my three tokens is 401ing?" answerable at all.
        everythingLogged.Should().Contain(result.Minted.Id);
    }

    // ---- Minting rules ----

    [Fact]
    public async Task Minting_is_refused_for_a_user_with_no_username()
    {
        // Refused at the one moment there is a person present to read why. See ApiTokenService.Mint.
        User("ghost", username: null);

        var result = await _sut.Mint("ghost", "script", devScope: false, lifetime: null);

        result.Minted.Should().BeNull();
        result.Error.Should().Contain("mood tags");
        _repo.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Minting_is_refused_for_a_subject_that_is_not_a_user()
    {
        _users.Get("nobody").Returns((AppUser?)null);

        (await _sut.Mint("nobody", "script", devScope: false, lifetime: null))
            .Minted.Should().BeNull();
    }

    [Fact]
    public async Task Minting_is_refused_for_an_expiry_in_the_past()
    {
        (await _sut.Mint(Subject, "script", devScope: false, lifetime: TimeSpan.FromDays(-1)))
            .Minted.Should().BeNull("a token dead on arrival is a client bug, not a credential");
    }

    // ---- Listing and revocation ----

    [Fact]
    public async Task Revoking_someone_elses_token_does_nothing()
    {
        var result = await _sut.Mint(Subject, "seed script", devScope: false, lifetime: null);

        // The id is the public half and shows up in logs, so guessing one must not be enough.
        (await _sut.Revoke("someone-else", result.Minted!.Id)).Should().BeFalse();
        (await _sut.Verify(result.Minted.Token)).Failure.Should().BeNull();
    }

    [Fact]
    public async Task Revoking_twice_reports_that_the_second_call_changed_nothing()
    {
        var result = await _sut.Mint(Subject, "seed script", devScope: false, lifetime: null);

        (await _sut.Revoke(Subject, result.Minted!.Id)).Should().BeTrue();
        (await _sut.Revoke(Subject, result.Minted.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_list_shows_a_users_tokens_with_no_secret_and_a_live_flag()
    {
        var live = await _sut.Mint(Subject, "playlist script", devScope: false, lifetime: null);
        var dead = await _sut.Mint(Subject, "old script", devScope: false, lifetime: null);
        await _sut.Revoke(Subject, dead.Minted!.Id);
        await _sut.Mint("other-user", "not mine", devScope: false, lifetime: null);
        User("other-user", "kelsey");

        var listed = await _sut.List(Subject);

        listed.Select(t => t.Id).Should().BeEquivalentTo(new[] { live.Minted!.Id, dead.Minted.Id });
        listed.Single(t => t.Id == live.Minted.Id).Active.Should().BeTrue();
        listed.Single(t => t.Id == dead.Minted.Id).Active.Should().BeFalse();
    }

    // ---- The dev gate: a token is not a dev session ----

    private static ClaimsPrincipal BrowserSession(string username) =>
        new(new ClaimsIdentity(new[] { new Claim("preferred_username", username) }, "Cookies"));

    [Fact]
    public async Task A_token_does_not_satisfy_the_dev_policy_by_default()
    {
        // The user IS a dev — that is the whole point of the test. Before tokens existed the policy
        // asked only that question, and answering it alone would hand this credential the route that
        // wipes every verdict tag in the library.
        var devUsers = new DevUsers(Username);
        var principal = (await _sut.Verify(await Mint())).Principal!;

        devUsers.Includes(principal).Should().BeTrue("the token authenticates as a dev user");
        devUsers.AllowsDevTools(principal).Should().BeFalse("but the credential was not granted dev scope");
    }

    [Fact]
    public async Task A_token_minted_with_dev_scope_does_satisfy_the_dev_policy()
    {
        var devUsers = new DevUsers(Username);
        var principal = (await _sut.Verify(await Mint(dev: true))).Principal!;

        devUsers.AllowsDevTools(principal).Should().BeTrue();
    }

    [Fact]
    public async Task Dev_scope_on_a_token_cannot_promote_a_user_who_is_not_a_dev()
    {
        // Scope is a ceiling, not a grant: dropping someone out of DEV_USERNAMES has to take their
        // tokens' dev access with it, or revoking a maintainer would mean hunting their tokens down.
        var devUsers = new DevUsers("someone-else");
        var principal = (await _sut.Verify(await Mint(dev: true))).Principal!;

        devUsers.AllowsDevTools(principal).Should().BeFalse();
    }

    [Fact]
    public void An_interactive_session_is_unaffected_by_the_token_dev_gate()
    {
        // The gate must only ever subtract from tokens. A dev at a browser is exactly who the panel
        // is for, and this is the regression that would lock them out of their own app.
        var devUsers = new DevUsers(Username);

        devUsers.AllowsDevTools(BrowserSession(Username)).Should().BeTrue();
        devUsers.AllowsDevTools(BrowserSession("kelsey")).Should().BeFalse();
    }

    // ---- What a client actually sends ----

    [Theory]
    [InlineData("Authorization", "Bearer myc_abc.def", "myc_abc.def")]
    // Case-insensitive scheme: some clients normalise it, and a 401 over capitalisation would be a
    // miserable thing to debug down a pipe.
    [InlineData("Authorization", "bearer myc_abc.def", "myc_abc.def")]
    // The fallback, for a reverse proxy that has claimed Authorization for its own handshake.
    [InlineData("X-Mycelium-Token", "myc_abc.def", "myc_abc.def")]
    public void A_token_is_read_from_either_header(string header, string value, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[header] = value;

        ApiTokenDefaults.TryRead(request, out var token).Should().BeTrue();
        token.Should().Be(expected);
    }

    [Theory]
    [InlineData("Authorization", "Basic dXNlcjpwYXNz")]
    [InlineData("Authorization", "Bearer ")]
    [InlineData("Cookie", "myc.auth=whatever")]
    public void A_request_carrying_no_bearer_token_is_left_to_the_cookie_scheme(string header, string value)
    {
        // This is the selector's question on *every* request in the app, browser ones included. A
        // false positive here would route a signed-in browser to the token handler and log it out.
        var request = new DefaultHttpContext().Request;
        request.Headers[header] = value;

        ApiTokenDefaults.HasToken(request).Should().BeFalse();
    }

    /// <summary>Captures formatted log lines so a test can assert what is — and isn't — in them.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        private sealed class NoScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

/// <summary>
/// How the two credentials are composed. Adding a second authentication scheme to a working app is
/// the kind of change that breaks the <em>first</em> one, quietly and only in production: the OIDC
/// callback signs in against <c>DefaultSignInScheme</c>, and pointing the default at a policy scheme
/// — which can't sign anyone in — turns every login into a 500 at the last step. Nothing else here
/// exercises that, so it is pinned.
/// </summary>
public class BffAuthenticationSchemeTests
{
    private static async Task<(AuthenticationScheme? Authenticate, AuthenticationScheme? SignIn,
        AuthenticationScheme? SignOut, AuthenticationScheme? Challenge)> Defaults()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddBffAuthentication();
        await using var provider = builder.Services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        return (
            await schemes.GetDefaultAuthenticateSchemeAsync(),
            await schemes.GetDefaultSignInSchemeAsync(),
            await schemes.GetDefaultSignOutSchemeAsync(),
            await schemes.GetDefaultChallengeSchemeAsync());
    }

    [Fact]
    public async Task Requests_are_authenticated_through_the_selector_scheme()
    {
        // What makes every existing RequireAuthorization() accept a token without being edited.
        (await Defaults()).Authenticate?.Name.Should().Be(BffAuthentication.SelectorScheme);
    }

    [Fact]
    public async Task Sign_in_and_sign_out_still_name_the_cookie_scheme()
    {
        // The OIDC handler inherits its SignInScheme from this. Left to fall through to the default,
        // it would inherit the policy scheme and the login callback would fail on the way back.
        var defaults = await Defaults();

        defaults.SignIn?.Name.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        defaults.SignOut?.Name.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task A_challenge_never_starts_an_OIDC_redirect_on_its_own()
    {
        // Preserved from before tokens existed: an OIDC challenge plants a correlation and a nonce
        // cookie that a fetch() can never consume, and a polling SPA accumulated them until the
        // request header outgrew Kestrel's limit and the site answered 431. Login is started only by
        // /auth/login, which names the scheme itself.
        (await Defaults()).Challenge?.Name.Should().NotBe(OpenIdConnectDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task Both_credentials_are_registered_as_schemes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddBffAuthentication();
        await using var provider = builder.Services.BuildServiceProvider();

        var all = (await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync())
            .Select(s => s.Name);

        all.Should().Contain(new[]
        {
            BffAuthentication.SelectorScheme,
            ApiTokenDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme,
        });
    }
}

/// <summary>
/// The token store is reached by <c>MongoDbDataModule</c>'s assembly scan, not by a hand-written
/// registration — so a missing registration compiles fine and fails at the first request, on someone
/// else's deployment. Same reasoning as <see cref="PlaylistWiringTests"/>.
/// </summary>
public class ApiTokenWiringTests : IDisposable
{
    private readonly IContainer _container;

    public ApiTokenWiringTests()
    {
        // MainModule reads these at registration time and throws without them; neither is dialled.
        Environment.SetEnvironmentVariable("PLEX_ENDPOINT", "http://plex.invalid:32400");
        Environment.SetEnvironmentVariable("MONGO_URI", "mongodb://mongo.invalid:27017");

        var builder = new ContainerBuilder();
        builder.RegisterModule<MainModule>();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterInstance<IDistributedCache>(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        // ApiTokenService is registered on the host's ServiceCollection (in AddBffAuthentication)
        // rather than by a scan, because Services.Auth isn't in MainModule's scan. Registering it
        // here mirrors that so its dependencies can be checked.
        builder.RegisterType<ApiTokenService>().AsSelf().SingleInstance();
        _container = builder.Build();
    }

    public void Dispose() => _container.Dispose();

    [Theory]
    [InlineData(typeof(IApiTokenRepo))]
    [InlineData(typeof(ApiTokenService))]
    public void Api_token_services_resolve(Type service)
    {
        _container.Invoking(c => c.Resolve(service))
            .Should().NotThrow($"{service.Name} is what stands between the scripts and a 401");
    }
}
