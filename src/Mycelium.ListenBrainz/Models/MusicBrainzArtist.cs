using Newtonsoft.Json;

namespace Mycelium.ListenBrainz.Models;

/// <summary>
/// One artist hit from a MusicBrainz search. <see cref="Id"/> is the MBID — the stable identifier
/// the ListenBrainz similarity endpoint is keyed by. <see cref="Score"/> is MusicBrainz's search
/// relevance (0-100); <see cref="Disambiguation"/> is the parenthetical that tells two same-named
/// acts apart (handy when surfacing a "wrong artist" correction later).
/// </summary>
public class MusicBrainzArtist
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("score")]
    public int Score { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }
}

/// <summary>Envelope of a MusicBrainz <c>/ws/2/artist</c> search: <c>{ "artists": [ ... ] }</c>.</summary>
public class MusicBrainzSearchResult
{
    [JsonProperty("artists")]
    public List<MusicBrainzArtist> Artists { get; set; } = new();
}

/// <summary>
/// One release-group hit — an album as a <em>work</em> rather than as a particular pressing.
///
/// <para>The release group is the right level for an archive. A release is one edition (the 2009
/// Japanese remaster, the vinyl reissue), and which of those a library happens to hold is an accident
/// of acquisition; the release group is "the album", and is what survives someone replacing their
/// copy. <see cref="Id"/> is its MBID.</para>
/// </summary>
public class MusicBrainzReleaseGroup
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("score")]
    public int Score { get; set; }

    [JsonProperty("primary-type")]
    public string? PrimaryType { get; set; }
}

/// <summary>Envelope of a <c>/ws/2/release-group</c> search: <c>{ "release-groups": [ ... ] }</c>.</summary>
public class MusicBrainzReleaseGroupSearchResult
{
    [JsonProperty("release-groups")]
    public List<MusicBrainzReleaseGroup> ReleaseGroups { get; set; } = new();
}
