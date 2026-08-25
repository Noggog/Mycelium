namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// The one rule for merging this app's verdict moods into a Plex item's existing ones, shared by the
/// artist tagger and the album tagger so the two can't drift.
///
/// <para>Plex's tag edit is additive unless removals are spelled out, and hand-applied moods share the
/// field with ours — so a write is always a delta computed against what the item currently carries,
/// never a replace.</para>
/// </summary>
public static class MoodTags
{
    /// <summary>
    /// The mood set an item should end up with: every tag in <paramref name="remove"/> dropped
    /// (case-insensitively) and <paramref name="add"/> present, with all other moods left untouched.
    /// Returns <c>null</c> when the item is already in that state — the signal to write nothing.
    /// </summary>
    public static IReadOnlyList<string>? Reconcile(
        string[] existing, string? add, IReadOnlyCollection<string> remove)
    {
        var removedAny = existing.Any(l => remove.Contains(l, StringComparer.OrdinalIgnoreCase));
        var needAdd = add != null && !existing.Contains(add, StringComparer.OrdinalIgnoreCase);
        if (!removedAny && !needAdd)
        {
            return null;
        }

        var next = existing.Where(l => !remove.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        if (needAdd)
        {
            next.Add(add!);
        }

        return next;
    }
}
