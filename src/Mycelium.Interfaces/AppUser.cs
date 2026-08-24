namespace Mycelium.Interfaces;

/// <summary>
/// A user of the app, identified by the OIDC <paramref name="Subject"/> (the stable "sub" claim).
/// Profile fields mirror the IdP and are refreshed on each login; the app owns only taste/seeds,
/// never credentials — authentication lives entirely in the OIDC provider.
///
/// <para><paramref name="MaxQuality"/> is the one thing here the IdP does <em>not</em> own: how good
/// a copy this user's requests are allowed to pull down. It caps what one person's likes cost on the
/// shared library volume — lossless runs about 3x the size of 320kbps for the same album. Null means
/// it has never been set for this user, in which case the deployment default applies; it is set from
/// the dev panel and deliberately never touched by <see cref="IUserRepo.UpsertOnLogin"/>, or every
/// login would undo it.</para>
/// </summary>
public record AppUser(
    string Subject,
    string? Username,
    string? Email,
    string? DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastLoginAt,
    AudioQuality? MaxQuality = null);
