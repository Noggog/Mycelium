using System.Text.Json.Serialization;

namespace Mycelium.Interfaces;

/// <summary>
/// Where an upgrade stands on keeping the album's Plex match, which is what its star ratings are
/// stored against. See <c>UpgradeMatchKeeper</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpgradeMatchCheck
{
    /// <summary>Nothing to check: the swap hasn't happened, or was refused.</summary>
    None,

    /// <summary>The old copy's match is saved; waiting for Plex to list the new copy.</summary>
    Waiting,

    /// <summary>Plex matched the new copy to the same release, so ratings carried over by themselves.</summary>
    Kept,

    /// <summary>Plex matched the new copy to another release; it was rematched to the old one.</summary>
    Rematched,

    /// <summary>
    /// The new copy is matched to another release and rematching didn't take (or Plex never showed the
    /// new copy in time). Its ratings are likely missing until it's fixed by hand in Plex.
    /// </summary>
    NeedsFixMatch,

    /// <summary>The old copy wasn't matched to anything in Plex, so there was no release to keep.</summary>
    NotMatched,
}

/// <summary>
/// What happened when an upgrade tried to replace the copy already in the library — kept on the row
/// so the Download page can show it, rather than it living only in the server log.
/// </summary>
/// <param name="At">When the swap ran, or was refused.</param>
/// <param name="RefusalDetail">Why the swap was refused ("got 9 of 10 tracks"); null when it went ahead.</param>
/// <param name="ReplacedQuality">What the old copy was.</param>
/// <param name="NewQuality">What replaced it.</param>
/// <param name="FilesMoved">How many of the old copy's files were moved aside.</param>
/// <param name="MovedTo">Where they went, to recover or delete them by hand.</param>
/// <param name="AlbumFolder">The library folder the new copy was promoted into.</param>
/// <param name="Match">How keeping the Plex match has gone.</param>
/// <param name="OldMatch">What Plex had the old copy matched to (e.g. <c>plex://album/…</c>).</param>
/// <param name="NewMatch">What Plex matched the new copy to, when that differed.</param>
/// <param name="MatchCheckSince">When the current round of checking began; bounds how long it keeps trying.</param>
/// <param name="Dismissed">Cleared off the Download page by hand. Display only: the match is still kept.</param>
public record UpgradeReport(
    DateTimeOffset At,
    string? RefusalDetail = null,
    AudioQuality? ReplacedQuality = null,
    AudioQuality? NewQuality = null,
    int FilesMoved = 0,
    string? MovedTo = null,
    string? AlbumFolder = null,
    UpgradeMatchCheck Match = UpgradeMatchCheck.None,
    string? OldMatch = null,
    string? NewMatch = null,
    DateTimeOffset? MatchCheckSince = null,
    bool Dismissed = false);
