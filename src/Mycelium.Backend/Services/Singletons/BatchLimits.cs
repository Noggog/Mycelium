namespace Mycelium.Backend.Services.Singletons;

/// <summary>
/// How much of the API a single request may ask for at once.
///
/// <para>One number, deliberately, for both the rating batches and the <c>?ids=</c> filter on the
/// purchase queue: the client these exist for submits a batch and then polls for exactly the rows it
/// submitted, so a poll that couldn't name everything it had just queued would force it back into the
/// per-item loop the batch was added to replace. Raising one without the other would silently
/// reintroduce that.</para>
///
/// <para>The value is a ceiling on damage, not a throughput target. A playlist's worth of albums is
/// 15–40, so 50 clears the real workload with room to spare while keeping a malformed or hostile body
/// from turning one request into an unbounded amount of server work. Over the cap is a 400 rather
/// than a silent truncation: a caller that queued 60 albums and was told "OK" about 50 of them would
/// wait forever on ten that were never accepted.</para>
/// </summary>
public static class BatchLimits
{
    /// <summary>Most items one batch request — or one <c>?ids=</c> filter — may carry.</summary>
    public const int MaxItems = 50;

    /// <summary>
    /// Throws when <paramref name="count"/> is over the cap. An <see cref="ArgumentException"/> because
    /// that is what the routes in this app already translate into a 400 with the message shown to the
    /// caller (see the tag-edit and stock-playlist endpoints) — the size of a body is the client's
    /// mistake to fix, and the message names both numbers so it can see by how much.
    /// </summary>
    public static void Guard(int count, string what = "batch")
    {
        if (count > MaxItems)
        {
            throw new ArgumentException($"A {what} is capped at {MaxItems} items; got {count}.");
        }
    }
}
