using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>What the Download page needs to know about the Deezer credential.</summary>
/// <param name="Configured">Whether an ARL is set at all.</param>
/// <param name="ConfigPath">The file it lives in, so the page can name it when the app can't write it.</param>
public record DeezerCredentialStatus(bool Configured, string ConfigPath);

/// <summary>The result of a user submitting a new ARL.</summary>
/// <param name="Saved">Whether it validated and was written.</param>
/// <param name="AccountName">The Deezer account it authenticates as, when Deezer told us.</param>
/// <param name="Lossless">Whether that account can stream lossless — surfaced because an account that
/// can't is the usual reason downloads keep landing as MP3, and this is the moment to say so.</param>
/// <param name="Requeued">How many blocked downloads were returned to the queue.</param>
/// <param name="Error">Why it wasn't saved, in terms the user can act on.</param>
public record ArlUpdateResult(
    bool Saved,
    string? AccountName = null,
    bool Lossless = false,
    int Requeued = 0,
    string? Error = null);

/// <summary>
/// Replacing the Deezer ARL from the UI. streamrip authenticates by ARL only (its Deezer client has
/// no email/password path, and the underlying gateway login needs a reCAPTCHA token), so the token
/// expiring is a recurring manual chore — this exists to make that chore a paste into the page that
/// reported the problem rather than an SSH session and a TOML edit.
///
/// Validate-then-write, never write-then-hope: a mistyped ARL saved blindly would look like success
/// and then fail every download exactly as the expired one did, which is the confusion this whole
/// feature exists to end. Only a token Deezer positively accepts is written.
/// </summary>
public class DeezerCredentialService
{
    private readonly IDeezerSessionCheck _session;
    private readonly StreamripArlStore _store;
    private readonly IPurchaseRepo _purchases;
    private readonly ILogger<DeezerCredentialService> _logger;

    public DeezerCredentialService(
        IDeezerSessionCheck session,
        StreamripArlStore store,
        IPurchaseRepo purchases,
        ILogger<DeezerCredentialService> logger)
    {
        _session = session;
        _store = store;
        _purchases = purchases;
        _logger = logger;
    }

    public DeezerCredentialStatus Status() =>
        new(_store.HasArl(), StreamripArlStore.ConfigPath);

    /// <summary>
    /// Checks the ARL with Deezer, writes it into streamrip's config if it's good, and releases the
    /// downloads that were blocked by the old one. Returns why not, when it declines.
    /// </summary>
    public async Task<ArlUpdateResult> Update(string arl)
    {
        var check = await _session.Check(arl);
        if (!check.Valid)
        {
            // A transport failure is reported as itself. Saying "invalid" when Deezer was simply
            // unreachable would send the user off to re-copy a token that was fine.
            return new ArlUpdateResult(
                false,
                Error: check.Error
                       ?? "Deezer rejected that ARL. Copy it again from a logged-in browser session — "
                       + "it must be the full cookie value.");
        }

        var saved = _store.Save(arl);
        if (!saved.Saved)
        {
            return new ArlUpdateResult(false, Error: saved.Error);
        }

        var requeued = await ReleaseBlocked();
        _logger.LogInformation(
            "Deezer ARL replaced (account {Account}); returned {Requeued} blocked download(s) to the queue",
            check.AccountName ?? "unknown", requeued);
        return new ArlUpdateResult(true, check.AccountName, check.Lossless, requeued);
    }

    /// <summary>
    /// Returns every download that failed on the old credential to <see cref="PurchaseStatus.Pending"/>.
    /// Without this the page would still show the blocked banner (which is derived from those rows)
    /// after a successful fix, and the user would have to retry each album by hand for a failure that
    /// was never about the album. Pending rather than Queued so the drainer's own pacing still
    /// applies, and so the manual-mode switch keeps meaning what it says.
    /// </summary>
    private async Task<int> ReleaseBlocked()
    {
        var blocked = (await _purchases.GetAll())
            .Where(p => p.Status == PurchaseStatus.Failed && p.Failure.IsSystemic())
            .ToList();

        foreach (var row in blocked)
        {
            await _purchases.SetStatus(row.Id, PurchaseStatus.Pending);
        }

        return blocked.Count;
    }
}
