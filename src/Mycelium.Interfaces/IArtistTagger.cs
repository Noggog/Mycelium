using System.Text;

namespace Mycelium.Interfaces;

/// <summary>
/// The seam for stamping a user's taste verdict onto an artist in the library backend (Plex) as a
/// tag — e.g. a thumbs-up writes "&lt;user&gt;_liked". Best-effort and additive: it merges with any
/// existing tags and never throws, so a tagging failure can't break the rating it accompanies.
/// </summary>
public interface IArtistTagger
{
    /// <summary>
    /// Reconciles the managed tags on <paramref name="artistName"/> in a single pass: ensures
    /// <paramref name="add"/> is present (when non-null) and every tag in <paramref name="remove"/> is
    /// absent, leaving all other tags (other users' verdicts, hand-applied tags) untouched.
    ///
    /// <para>A rating passes the new verdict tag as <paramref name="add"/> and the opposite verdict's tag
    /// in <paramref name="remove"/>, so the latest verdict is the one left on the artist (a like→dislike
    /// flip drops "_liked" and leaves "_disliked"). A cleared/undone rating passes add=null and both
    /// verdict tags in <paramref name="remove"/>, stripping whichever was set.</para>
    ///
    /// <para>Best-effort: failures are logged, never thrown — tagging is a side effect of rating and must
    /// not fail the rating itself.</para>
    /// </summary>
    Task SetTags(string artistName, string? add, IReadOnlyCollection<string> remove);
}

/// <summary>
/// Builds the per-user tags this app writes into the library's Mood field. Three kinds, and the
/// difference between them is what the tag <em>means</em>:
///
/// <list type="bullet">
/// <item><b>Verdict</b> — "&lt;username&gt;_liked" / "&lt;username&gt;_disliked" (<see cref="For"/>).
/// Current rating state: it flips when the user flips their thumb and disappears when they clear it.</item>
/// <item><b>Credit</b> — "&lt;username&gt;_added" (<see cref="Added"/>). A record of history, not of
/// taste: this is the person who asked for the record and put it in the library. It is written once,
/// when the download finally lands, and is never rewritten or removed — someone who later sours on a
/// record still added it.</item>
/// <item><b>Suggestion</b> — "&lt;username&gt;_recommended" (<see cref="Recommended"/>). Not a
/// decision at all: it marks an artist the library already has that the user's <em>liked</em> artists
/// point at, so "what should I put on next" is answerable from Plex. It is derived state — a periodic
/// sweep (<c>RecommendedArtistTagger</c>) puts it on and takes it off as the frontier moves, and
/// thumbing the artist either way retires it.</item>
/// </list>
///
/// <para>The username is trimmed of any email domain and reduced to [a-z0-9_] so the tag is clean and
/// collision-resistant; every builder returns null when there's no usable username (so the caller skips
/// tagging).</para>
/// </summary>
public static class ArtistTag
{
    public static string? For(string? username, DiscoveryStatus status)
    {
        var prefix = Sanitize(username);
        if (prefix.Length == 0)
        {
            return null;
        }

        var verdict = status == DiscoveryStatus.Liked ? "liked" : "disliked";
        return $"{prefix}_{verdict}";
    }

    /// <summary>
    /// The permanent "this is who brought the record in" credit — "&lt;username&gt;_added". Stamped on
    /// the <em>album</em> once an acquisition lands in the library (see the purchase reconcile), so a
    /// smart playlist can ask for "everything noggog added" through Plex's "Album Mood" field.
    /// </summary>
    public static string? Added(string? username)
    {
        var prefix = Sanitize(username);
        return prefix.Length == 0 ? null : $"{prefix}_added";
    }

    /// <summary>
    /// Whether a tag is a taste verdict of ours — the "_liked"/"_disliked" suffix namespace. This is
    /// the set the dev wipe strips, because it's the set that can be rebuilt afterwards from the stored
    /// ratings; provider moods, hand-applied tags and the "_added" credits are left alone. Coarse on
    /// purpose: any name with that suffix is treated as ours (we can't enumerate every username that
    /// ever rated), which is the right call for a "clean slate" reset.
    /// </summary>
    public static bool IsVerdict(string? label) =>
        label != null
        && (label.EndsWith("_liked", StringComparison.OrdinalIgnoreCase)
            || label.EndsWith("_disliked", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a tag is an "added" credit — the "_added" suffix namespace.</summary>
    public static bool IsAdded(string? label) =>
        label != null && label.EndsWith("_added", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The "your taste points here" marker — "&lt;username&gt;_recommended". Stamped on <em>owned</em>
    /// artists the user hasn't thumbed yet that at least one of their liked artists recommends, so a
    /// smart playlist can play the library the discovery feed is nudging them towards. Unlike a verdict
    /// this is nobody's decision: it is recomputed wholesale by the sweep, which is also what removes it
    /// once the artist is thumbed (a rated artist leaves that section of the feed).
    /// </summary>
    public static string? Recommended(string? username)
    {
        var prefix = Sanitize(username);
        return prefix.Length == 0 ? null : $"{prefix}_recommended";
    }

    /// <summary>Whether a tag is a suggestion marker — the "_recommended" suffix namespace.</summary>
    public static bool IsRecommended(string? label) =>
        label != null && label.EndsWith("_recommended", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a tag is one this app owns — verdict, credit or suggestion. This is the set the tag
    /// editor hides and refuses to write: all of it is state the app maintains, and offering a second,
    /// desynced way to change it would be a bug either way (a hand-added "_recommended" would simply be
    /// swept off again at the next pass). Not the same set as <see cref="IsVerdict"/> — a wipe that took
    /// the "_added" credits with it could never put them back.
    /// </summary>
    public static bool IsManaged(string? label) =>
        IsVerdict(label) || IsAdded(label) || IsRecommended(label);

    private static string Sanitize(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "";
        }

        // Email-style usernames trim to the local part before '@'.
        var at = username.IndexOf('@');
        var local = at >= 0 ? username[..at] : username;

        var sb = new StringBuilder(local.Length);
        foreach (var c in local.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
