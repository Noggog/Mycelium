using Newtonsoft.Json;

namespace Mycelium.ListenBrainz.Models;

/// <summary>
/// One release — a particular edition of a <see cref="MusicBrainzReleaseGroup"/>: the CD, the vinyl,
/// the digital release. Browsed for what the release group itself doesn't carry: the
/// <see cref="Barcode"/> (which Deezer answers as a UPC) and the links to store pages
/// (<see cref="Relations"/>).
/// </summary>
public class MusicBrainzRelease
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    /// <summary>"Official", "Promotion", "Bootleg", "Pseudo-Release", or null when unset.</summary>
    [JsonProperty("status")]
    public string? Status { get; set; }

    /// <summary>
    /// The barcode exactly as printed — leading zeros matter, since Deezer's UPC lookup matches the
    /// string. Empty when the release is known to have none, null when nobody has entered it.
    /// </summary>
    [JsonProperty("barcode")]
    public string? Barcode { get; set; }

    [JsonProperty("date")]
    public string? Date { get; set; }

    [JsonProperty("country")]
    public string? Country { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    /// <summary>The album this is an edition of. Present when <c>inc=release-groups</c> was asked for.</summary>
    [JsonProperty("release-group")]
    public MusicBrainzReleaseGroup? ReleaseGroup { get; set; }

    /// <summary>Links to other entities and URLs. Present when a <c>*-rels</c> include was asked for.</summary>
    [JsonProperty("relations")]
    public List<MusicBrainzRelation>? Relations { get; set; }
}

/// <summary>
/// One page of a <c>/ws/2/release?artist={mbid}</c> browse: <c>{ "release-count": n, "releases":
/// [ ... ] }</c>. The count is across every page.
/// </summary>
public class MusicBrainzReleaseBrowse
{
    [JsonProperty("release-count")]
    public int Count { get; set; }

    [JsonProperty("releases")]
    public List<MusicBrainzRelease> Releases { get; set; } = new();
}

/// <summary>
/// One relationship. Which of <see cref="Url"/>, <see cref="Artist"/> or <see cref="Release"/> is set
/// follows <see cref="TargetType"/>. <see cref="Type"/> is the kind of link — for a store page,
/// "free streaming", "streaming" or "purchase for download".
/// </summary>
public class MusicBrainzRelation
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    /// <summary>"url", "artist", "release", ...</summary>
    [JsonProperty("target-type")]
    public string? TargetType { get; set; }

    /// <summary>
    /// Set when the link has ended (a store page taken down, say). An ended link is history, not
    /// evidence of where something is now.
    /// </summary>
    [JsonProperty("ended")]
    public bool Ended { get; set; }

    [JsonProperty("url")]
    public MusicBrainzUrlTarget? Url { get; set; }

    [JsonProperty("artist")]
    public MusicBrainzArtist? Artist { get; set; }

    [JsonProperty("release")]
    public MusicBrainzRelease? Release { get; set; }
}

/// <summary>The URL end of a relationship: <c>{ "id": mbid, "resource": "https://..." }</c>.</summary>
public class MusicBrainzUrlTarget
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("resource")]
    public string? Resource { get; set; }
}

/// <summary>
/// A <c>/ws/2/url?resource=...</c> lookup: which MusicBrainz entities link to a given URL — the reverse
/// of <see cref="MusicBrainzRelease.Relations"/>. Asked of a Deezer artist page it answers "which
/// MusicBrainz artist is this", exactly, with no name matching involved.
/// </summary>
public class MusicBrainzUrl
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("resource")]
    public string? Resource { get; set; }

    /// <summary>Everything linked to the URL. Empty when MusicBrainz has never heard of it.</summary>
    [JsonProperty("relations")]
    public List<MusicBrainzRelation> Relations { get; set; } = new();
}
