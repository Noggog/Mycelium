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
    /// <para>A rating passes the new verdict tag as <paramref name="add"/> and <em>every other</em>
    /// verdict tag in <paramref name="remove"/> (see <see cref="ArtistTag.OtherVerdictTags"/>), so the
    /// latest verdict is the only one left on the artist (a like→dislike flip drops "_liked" and leaves
    /// "_disliked"). A cleared/undone rating passes add=null and all of them in
    /// <paramref name="remove"/>, stripping whichever was set.</para>
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
/// <item><b>Verdict</b> — "&lt;username&gt;_liked" / "&lt;username&gt;_disliked" /
/// "&lt;username&gt;_indifferent" (<see cref="For"/>). Current rating state: it flips when the user
/// flips their thumb and disappears when they clear it. At most one is ever on an artist, which is
/// what <see cref="OtherVerdictTags"/> is for.</item>
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
    /// <summary>
    /// The verdict tag for one status, or null when there isn't one — no usable username, or a status
    /// that isn't a verdict at all.
    ///
    /// <para><b>Why the default is null and not a tag.</b> This used to read
    /// <c>status == Liked ? "liked" : "disliked"</c>, which is fine for exactly two verdicts and a trap
    /// for any third: every other status — <see cref="DiscoveryStatus.Pending"/>,
    /// <see cref="DiscoveryStatus.Snoozed"/>, and now <see cref="DiscoveryStatus.Indifferent"/> before
    /// it had a case — folded silently into "disliked". Nothing fails when that happens; the band just
    /// quietly drops out of the Deep Frontier playlist months later. Every caller already null-checks
    /// the result (a blank username has always produced one), so returning null for a non-verdict is
    /// the change that makes this safe by construction rather than by each caller remembering to
    /// pre-filter.</para>
    /// </summary>
    public static string? For(string? username, DiscoveryStatus status)
    {
        var verdict = status switch
        {
            DiscoveryStatus.Liked => "liked",
            DiscoveryStatus.Disliked => "disliked",
            DiscoveryStatus.Indifferent => "indifferent",
            // Pending/Snoozed are not verdicts — there is no tag to write, and writing one anyway is
            // exactly the bug this method used to have.
            _ => null,
        };
        if (verdict is null)
        {
            return null;
        }

        var prefix = Sanitize(username);
        return prefix.Length == 0 ? null : $"{prefix}_{verdict}";
    }

    /// <summary>
    /// The verdict tags that must come <em>off</em> when <paramref name="current"/> goes on — every
    /// verdict tag but that one. Pass null for a cleared rating, which strips all of them.
    ///
    /// <para>The invariant this exists to hold is "an artist carries at most one verdict tag". That was
    /// once expressible as "add the new one, remove the opposite", and with three verdicts it isn't:
    /// an Indifferent&#8594;Liked flip has two tags to strip, not one. Every call site that computed an
    /// "opposite" by ternary was a place where the third tag would have been left behind — and a
    /// leftover verdict tag fails nothing loudly, it just makes a smart playlist match music the user
    /// has moved on from.</para>
    /// </summary>
    public static string[] OtherVerdictTags(string? username, DiscoveryStatus? current) =>
        new[] { DiscoveryStatus.Liked, DiscoveryStatus.Disliked, DiscoveryStatus.Indifferent }
            .Where(s => s != current)
            .Select(s => For(username, s))
            .OfType<string>()
            .ToArray();

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
    /// Whether a tag is a taste verdict of ours — the "_liked"/"_disliked"/"_indifferent" suffix
    /// namespace. This is
    /// the set the dev wipe strips, because it's the set that can be rebuilt afterwards from the stored
    /// ratings; provider moods, hand-applied tags and the "_added" credits are left alone. Coarse on
    /// purpose: any name with that suffix is treated as ours (we can't enumerate every username that
    /// ever rated), which is the right call for a "clean slate" reset.
    /// </summary>
    public static bool IsVerdict(string? label) =>
        label != null
        && (label.EndsWith("_liked", StringComparison.OrdinalIgnoreCase)
            || label.EndsWith("_disliked", StringComparison.OrdinalIgnoreCase)
            || label.EndsWith("_indifferent", StringComparison.OrdinalIgnoreCase));

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
