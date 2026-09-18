namespace Mycelium.Interfaces;

/// <summary>
/// Durable memo of a release's track titles, keyed by Deezer album id. Deezer's discography listing
/// names no tracks, so learning them costs a paged <c>/album/{id}/tracks</c> call each — and the
/// single audit (<c>StandaloneSingleAuditor</c>) has to compare a single's songs against every album
/// and EP the artist has, which without a memo would be that call per release per sweep.
///
/// <para>A released album's track listing doesn't change, so this is a pure memo in the same sense as
/// <see cref="IDeezerAlbumArtistRepo"/>: written once, read forever, and in Mongo rather than in
/// process memory so a restart doesn't re-spend a rate-limited call per release.</para>
///
/// <para>Only a non-empty listing is ever recorded. <c>IDeezerApi.GetAlbumTracks</c> returns empty for
/// both "Deezer didn't answer" and "no tracks", and an album that came out of a discography listing has
/// tracks by construction — so memoising an empty result would pin a release to "no songs" for good on
/// the strength of one rate-limit blip, and the audit reads "no songs" as "holds nothing", which is the
/// answer that lets a teaser single through.</para>
/// </summary>
public interface IDeezerAlbumTrackRepo
{
    /// <summary>
    /// The track titles for each of these album ids we've already learned, in track order. Ids we
    /// haven't are simply absent — a missing entry means "not looked up yet", never "no tracks".
    /// </summary>
    Task<Dictionary<long, IReadOnlyList<string>>> Get(IReadOnlyCollection<long> albumIds);

    /// <summary>Records what a batch of lookups learned. Idempotent.</summary>
    Task Put(IReadOnlyDictionary<long, IReadOnlyList<string>> titlesByAlbumId);
}
