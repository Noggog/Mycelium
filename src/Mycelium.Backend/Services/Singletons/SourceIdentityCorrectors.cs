using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// One correctable external-identity source for the Artists-page "Sources" tab (Deezer, MusicBrainz,
/// …). Each owns a <see cref="Source"/> tag and knows how to read the artist's current identity,
/// search for candidates, and pin/clear a sticky override. Endpoints dispatch by <see cref="Source"/>,
/// so adding a source is just adding an implementation (it auto-registers via the assembly scan).
/// </summary>
public interface ISourceIdentityCorrector
{
    string Source { get; }

    /// <summary>The artist's currently-resolved identity on this source, or null if not resolved yet.</summary>
    Task<SourceIdentity?> GetCurrent(ArtistKey artist);

    /// <summary>Candidate matches for a free-text query, in relevance order.</summary>
    Task<IReadOnlyList<SourceCandidate>> Search(string query, int limit);

    /// <summary>Pin the artist to a specific id (sticky override); returns the pinned identity, or null if the id is invalid.</summary>
    Task<SourceIdentity?> Pin(ArtistKey artist, string id);

    /// <summary>Drop the pin (or unlinked flag) so the artist re-resolves from a name search next time.</summary>
    Task Clear(ArtistKey artist);

    /// <summary>
    /// Stickily detach the artist from this source: it has no match here, so resolution returns null
    /// (no name search) until the pin is cleared. For artists this source genuinely doesn't list.
    /// </summary>
    Task Unlink(ArtistKey artist);
}

/// <summary>Deezer identity corrector — wraps <see cref="DeezerArtistResolver"/> + the catalog.</summary>
public class DeezerIdentityCorrector : ISourceIdentityCorrector
{
    public string Source => "deezer";

    private readonly DeezerArtistResolver _resolver;
    private readonly IArtistCatalogRepo _catalog;

    public DeezerIdentityCorrector(DeezerArtistResolver resolver, IArtistCatalogRepo catalog)
    {
        _resolver = resolver;
        _catalog = catalog;
    }

    public async Task<SourceIdentity?> GetCurrent(ArtistKey artist)
    {
        if (await _catalog.IsDeezerUnlinked(artist)) return Unlinked("deezer");
        var stored = await _catalog.GetDeezer(artist);
        if (stored == null) return null;
        var (id, isOverride) = stored.Value;
        return ToIdentity(id, isOverride);
    }

    public async Task<IReadOnlyList<SourceCandidate>> Search(string query, int limit) =>
        (await _resolver.SearchArtists(query, limit)).Select(ToCandidate).ToArray();

    public async Task<SourceIdentity?> Pin(ArtistKey artist, string id)
    {
        if (!long.TryParse(id, out var deezerId)) return null;
        var identity = await _resolver.SetOverride(artist.ArtistName, deezerId);
        return identity is null ? null : ToIdentity(identity, isOverride: true);
    }

    public Task Clear(ArtistKey artist) => _resolver.ClearOverride(artist.ArtistName);

    public Task Unlink(ArtistKey artist) => _resolver.SetUnlinked(artist.ArtistName);

    private static SourceIdentity ToIdentity(DeezerIdentity d, bool isOverride) => new(
        Source: "deezer",
        Id: d.Id.ToString(),
        Name: d.Name,
        Detail: Fans(d.Fans),
        Link: d.Link ?? $"https://www.deezer.com/artist/{d.Id}",
        ImageUrl: d.ImageUrl,
        IsOverride: isOverride,
        Correctable: true);

    private static SourceCandidate ToCandidate(DeezerIdentity d) => new(
        Id: d.Id.ToString(),
        Name: d.Name,
        Detail: Fans(d.Fans),
        Link: d.Link ?? $"https://www.deezer.com/artist/{d.Id}",
        ImageUrl: d.ImageUrl,
        Popularity: d.Fans);

    private static string? Fans(int? fans) => fans.HasValue ? $"{fans.Value:N0} fans" : null;

    internal static SourceIdentity Unlinked(string source) => new(
        Source: source, Id: null, Name: null, Detail: null, Link: null, ImageUrl: null,
        IsOverride: true, Correctable: true, Unlinked: true);
}

/// <summary>MusicBrainz identity corrector — wraps <see cref="MusicBrainzArtistResolver"/> + the catalog.</summary>
public class MusicBrainzIdentityCorrector : ISourceIdentityCorrector
{
    public string Source => "musicbrainz";

    private readonly MusicBrainzArtistResolver _resolver;
    private readonly IArtistCatalogRepo _catalog;

    public MusicBrainzIdentityCorrector(MusicBrainzArtistResolver resolver, IArtistCatalogRepo catalog)
    {
        _resolver = resolver;
        _catalog = catalog;
    }

    public async Task<SourceIdentity?> GetCurrent(ArtistKey artist)
    {
        if (await _catalog.IsMusicBrainzUnlinked(artist)) return DeezerIdentityCorrector.Unlinked("musicbrainz");
        var stored = await _catalog.GetMusicBrainz(artist);
        if (stored == null) return null;
        var (id, isOverride) = stored.Value;
        return ToIdentity(id, isOverride);
    }

    public async Task<IReadOnlyList<SourceCandidate>> Search(string query, int limit) =>
        (await _resolver.SearchArtists(query, limit)).Select(ToCandidate).ToArray();

    public async Task<SourceIdentity?> Pin(ArtistKey artist, string id)
    {
        var identity = await _resolver.SetOverride(artist.ArtistName, id);
        return identity is null ? null : ToIdentity(identity, isOverride: true);
    }

    public Task Clear(ArtistKey artist) => _resolver.ClearOverride(artist.ArtistName);

    public Task Unlink(ArtistKey artist) => _resolver.SetUnlinked(artist.ArtistName);

    private static SourceIdentity ToIdentity(MusicBrainzIdentity m, bool isOverride) => new(
        Source: "musicbrainz",
        Id: m.Mbid,
        Name: m.Name,
        Detail: m.Disambiguation,
        Link: $"https://musicbrainz.org/artist/{m.Mbid}",
        ImageUrl: null,
        IsOverride: isOverride,
        Correctable: true);

    private static SourceCandidate ToCandidate(MusicBrainzIdentity m) => new(
        Id: m.Mbid,
        Name: m.Name,
        Detail: m.Disambiguation,
        Link: $"https://musicbrainz.org/artist/{m.Mbid}",
        ImageUrl: null);
}
