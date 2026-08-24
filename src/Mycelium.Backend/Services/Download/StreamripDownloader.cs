using System.Diagnostics;
using Mycelium.Deezer.Services;
using Mycelium.Interfaces;

namespace Mycelium.Backend.Services.Download;

/// <summary>
/// Acquires music by shelling out to <b>streamrip</b> (https://github.com/nathom/streamrip), a
/// maintained Deezer/Qobuz/Tidal CLI. streamrip owns the fragile parts — the Deezer ARL session,
/// per-track Blowfish decryption, quality selection and tagging — while we own orchestration and
/// throttling (see <c>DownloadService</c>).
///
/// We invoke the configured binary (default <c>rip</c>, resolved via the backend process's PATH, or
/// an absolute path via <c>STREAMRIP_BIN</c>) as:
///   <c>rip --no-db --folder DIR --quality Q [--codec C] url https://www.deezer.com/album/{id}</c>
/// (<c>--no-db</c> bypasses streamrip's download-history DB — we dedup via purchase status, and the DB
/// would otherwise skip tracks it thinks are already downloaded, leaving just the cover.)
///
/// <b>Exit code is not success.</b> streamrip gathers an album's tracks with
/// <c>return_exceptions=True</c> and merely logs each failure, so a pass in which every track was
/// unavailable still exits 0. Released streamrip (2.1.0) also has no per-track quality downgrade —
/// a track with no lossless raises and is dropped — so a FLAC-only request against an album Deezer
/// serves as MP3 quietly produces a folder containing nothing but cover art. Verification is
/// therefore by <i>track count</i>: we ask Deezer how many tracks the album has and compare against
/// the audio files that actually landed.
///
/// To make that check possible each pass writes into its own staging tree (see
/// <see cref="DownloadStaging"/>) rather than straight into the library. A pass that comes up short
/// is retried down the <see cref="DownloaderConfig.FallbackQualities"/> chain, and the results are
/// merged per-track — an album that is 80% lossless keeps its FLAC and gains MP3 only for the tracks
/// that had none. Only the merged, verified tree is promoted, so a half-finished grab never reaches
/// Plex.
///
/// The chain matters because <b>Deezer's formats vary per track, not per album</b>. When a track has
/// no master in the requested format, Deezer's media API returns no URL for it and streamrip 2.1.0
/// falls back to building one on <c>e-cdns-proxy-*.dzcdn.net</c> — a CDN Deezer has since retired,
/// which no longer resolves at all. So that track is simply lost unless something retries it lower.
/// A real example: an album with no FLAC, a 320 master for exactly one track, and only 128 for the
/// other three — FLAC yields 0, 320 yields 1, and only 128 recovers the rest.
///
/// A pass that hangs is killed after <see cref="DownloaderConfig.DownloadTimeout"/>; its staging tree
/// is discarded rather than promoted, because streamrip writes each track directly to its final name
/// with no partial-file marker and a truncated track can't be told from a complete one. We also do
/// <i>not</i> burn a second timeout on the fallback in that case. Only
/// <see cref="FeedKind.MissingAlbum"/> items with a Deezer album id are downloadable ("albums only").
/// This is the one place that knows streamrip's CLI; every attempt logs its command, and
/// timeouts/failures log streamrip's captured stdout+stderr.
/// </summary>
public class StreamripDownloader : IDownloader
{
    private enum RunResult { Success, Failed, TimedOut }

    /// <summary>
    /// One streamrip invocation. Carries the captured output alongside the outcome because the
    /// interesting failures don't show up in either the exit code or the file count: streamrip logs a
    /// per-track reason ("not available for stream", "Deezer HiFi is required", a geo-block) and then
    /// exits 0. When a pass comes up short, that text is the only account of *why*.
    /// </summary>
    private readonly record struct Pass(RunResult Result, string Stdout, string Stderr)
    {
        /// <summary>
        /// The credential failure this pass hit, or <see cref="DownloadFailure.None"/>. streamrip
        /// reports both as an uncaught Python traceback on stdout and exit 1, so the exception name in
        /// the captured text is the only thing that distinguishes "your ARL is dead" from "this album
        /// wouldn't download" — the exit code is 1 either way. Named exception types, not prose, so a
        /// wording change upstream can't silently turn this back into an unclassified failure.
        /// </summary>
        public DownloadFailure CredentialFailure
        {
            get
            {
                var text = $"{Stdout}\n{Stderr}";
                if (text.Contains("MissingCredentialsError", StringComparison.Ordinal))
                {
                    return DownloadFailure.DeezerCredentialsMissing;
                }
                return text.Contains("AuthenticationError", StringComparison.Ordinal)
                    ? DownloadFailure.DeezerAuth
                    : DownloadFailure.None;
            }
        }

        public string Output =>
            string.IsNullOrWhiteSpace(Stdout) && string.IsNullOrWhiteSpace(Stderr)
                ? "<streamrip printed nothing>"
                : $"stdout: {Stdout}\nstderr: {Stderr}";
    }

    private readonly DownloaderConfig _config;
    private readonly IDeezerApi _deezer;
    private readonly ILogger<StreamripDownloader> _logger;

    public StreamripDownloader(
        DownloaderConfig config,
        IDeezerApi deezer,
        ILogger<StreamripDownloader> logger)
    {
        _config = config;
        _deezer = deezer;
        _logger = logger;
    }

    public string Name => "streamrip (Deezer)";

    /// <summary>
    /// The streamrip <c>--quality</c> to fetch one row at, and the ladder to walk if it comes up
    /// short. A row carries a <see cref="PurchaseItem.TargetQuality"/> when the reconcile knew whose
    /// entitlements were behind it; rows written before tiers existed (and manual pastes, which no
    /// rating stands behind) carry none and use the configured quality, exactly as they always did.
    ///
    /// <para>The configured <see cref="DownloaderConfig.Quality"/> is a <em>ceiling</em>, not just a
    /// default: a target above it is clamped down. Otherwise a user marked lossless on a deployment
    /// deliberately pinned to 320 would quietly override the operator's setting.</para>
    ///
    /// <para>The ladder is derived from the row's own quality rather than the configured one, so a
    /// lossy row falls back 1 -> 0 instead of pretending it started at FLAC. It stays enabled for
    /// every row: Deezer's catalogue has per-track gaps, and an album that is lossless except for
    /// two tracks it will only serve at 320 is still the best copy obtainable.</para>
    /// </summary>
    private (string Quality, IReadOnlyList<string> Fallbacks) QualityFor(PurchaseItem item)
    {
        if (item.TargetQuality is not { } target)
        {
            return (_config.Quality, _config.FallbackQualities);
        }

        var wanted = target.ToStreamripQuality();
        // Both are numeric rungs on streamrip's scale; a non-numeric configured quality means we
        // can't compare, so the configured value simply wins.
        if (!int.TryParse(_config.Quality, out var ceiling) || !int.TryParse(wanted, out var asked))
        {
            return (_config.Quality, _config.FallbackQualities);
        }

        var effective = Math.Min(asked, ceiling);
        return (effective.ToString(), MainModule.ParseQualities(null, effective.ToString()));
    }

    public async Task<DownloadOutcome> Request(PurchaseItem item)
    {
        if (item.Kind != FeedKind.MissingAlbum || item.DeezerAlbumId is null or 0)
        {
            _logger.LogInformation(
                "Skipping {Id}: only Deezer albums are downloadable (artists are wishlist-only)", item.Id);
            return DownloadOutcome.Failed();
        }

        var albumId = item.DeezerAlbumId.Value;
        var url = $"https://www.deezer.com/album/{albumId}";

        if (string.IsNullOrWhiteSpace(_config.DownloadDir))
        {
            // With no library root we have nowhere to stage and no idea where streamrip's own config
            // points it, so the files can't be found afterwards to be counted. Fall back to the old
            // exit-code behaviour — weak, but the alternative is refusing to download at all.
            _logger.LogWarning(
                "MUSIC_DOWNLOAD_DIR is unset — downloading {Artist} — {Album} unstaged, so a pass that "
                + "silently grabs no tracks can't be detected", item.Artist.ArtistName, item.Album);
            return await RunUnverified(url, item);
        }

        var staging = DownloadStaging.PathFor(_config.DownloadDir, albumId);
        try
        {
            return await RunStaged(item, url, staging);
        }
        finally
        {
            if (!DownloadStaging.TryCleanup(_config.DownloadDir, albumId))
            {
                _logger.LogWarning("Could not clear staging directory {Staging} for {Id}", staging, item.Id);
            }
        }
    }

    /// <summary>
    /// The verified path: preferred quality into its own tree, a fallback pass merged in per-track if
    /// that came up short, then promote whatever survives into the library.
    /// </summary>
    private async Task<DownloadOutcome> RunStaged(PurchaseItem item, string url, string staging)
    {
        // 0 = Deezer couldn't tell us (flaky call, unknown album). We then fall back to the weaker
        // "did anything at all land" test, which still beats trusting the exit code.
        var expected = (await _deezer.GetAlbumTracks(item.DeezerAlbumId!.Value)).Length;

        var preferredDir = Path.Combine(staging, "preferred");
        DownloadStaging.Reset(staging);

        var (quality, fallbacks) = QualityFor(item);
        var first = await RunAt(quality, url, item, preferredDir);
        if (first.Result == RunResult.TimedOut)
        {
            _logger.LogWarning(
                "Discarding staged files for {Artist} — {Album}: the pass timed out, and a killed "
                + "streamrip leaves truncated tracks under their final names",
                item.Artist.ArtistName, item.Album);
            return DownloadOutcome.Failed();
        }

        // Deezer refused the session before it fetched anything. Every fallback would repeat the same
        // login and fail identically, so stop here: one clear reason beats three copies of a traceback,
        // and the reason is what the Download page needs to say "a retry won't fix this".
        if (first.CredentialFailure is var credential && credential != DownloadFailure.None)
        {
            LogCredentialFailure(credential, item, first);
            return DownloadOutcome.Failed(credential);
        }

        var got = DownloadStaging.AudioFiles(preferredDir).Count;
        var last = first;

        // Walk down the quality chain until the album is whole. Deezer's formats are per-track, so a
        // single downgrade isn't enough: an album with no FLAC can still serve a 320 master for one
        // track and only 128 for the rest, and stopping at 320 abandons those.
        var step = 0;
        foreach (var fallback in fallbacks)
        {
            if (IsComplete(got, expected) || fallback == quality)
            {
                continue;
            }

            _logger.LogInformation(
                "Quality {Q} has {Got}/{Expected} tracks for {Artist} — {Album}; trying {Fallback} "
                + "and keeping only the tracks the earlier passes missed",
                quality, got, expected > 0 ? expected.ToString() : "?",
                item.Artist.ArtistName, item.Album, fallback);

            // Indexed, not named by quality: the value comes from an env var and must never be able
            // to steer the path we hand streamrip.
            var fallbackDir = Path.Combine(staging, $"fallback-{++step}");
            last = await RunAt(fallback, url, item, fallbackDir);

            // A hang is systemic (bad ARL, network) and later passes would only burn more timeouts —
            // but whatever earlier passes wrote came from processes that exited on their own, so it's
            // complete and worth keeping. A rejected credential is systemic for the same reason; it can
            // surface here rather than on the first pass if the session lapses mid-album.
            if (last.Result == RunResult.TimedOut)
            {
                break;
            }

            if (last.CredentialFailure != DownloadFailure.None)
            {
                LogCredentialFailure(last.CredentialFailure, item, last);
                break;
            }

            var grafted = DownloadStaging.Graft(preferredDir, fallbackDir);
            got = DownloadStaging.AudioFiles(preferredDir).Count;
            _logger.LogInformation(
                "Fallback {Fallback} supplied {Grafted} track(s) for {Artist} — {Album}; now at {Got}",
                fallback, grafted, item.Artist.ArtistName, item.Album, got);
        }

        var landed = DownloadStaging.AudioFiles(preferredDir).Count;
        if (landed == 0)
        {
            // A credential failure that surfaced mid-ladder is reported as itself; the generic
            // "nothing arrived" message below would bury the one detail that tells the user what to do.
            if (last.CredentialFailure != DownloadFailure.None)
            {
                return DownloadOutcome.Failed(last.CredentialFailure);
            }

            // streamrip exits 0 having logged a reason per track, so its output is the only record of
            // why nothing arrived — dump it here rather than leaving the log saying merely "0 tracks".
            _logger.LogWarning(
                "No tracks downloaded for {Artist} — {Album} at quality {Q} or fallbacks {Fallbacks} — "
                + "nothing promoted to the library. Last streamrip pass said:\n{Output}",
                item.Artist.ArtistName, item.Album, quality,
                string.Join(", ", fallbacks), last.Output);
            return DownloadOutcome.Failed(DownloadFailure.NoTracksAvailable);
        }

        if (expected > 0 && landed < expected)
        {
            // No quality helps a track Deezer won't serve at all (geo-blocking, a pulled master), so
            // retrying forever is pointless — take the partial album and say so loudly. The output is
            // attached for the same reason as above: the per-track reasons live only in there.
            _logger.LogWarning(
                "Promoting a PARTIAL album: {Artist} — {Album} landed {Landed} of {Expected} tracks. "
                + "Last streamrip pass said:\n{Output}",
                item.Artist.ArtistName, item.Album, landed, expected, last.Output);
        }

        DownloadStaging.Promote(preferredDir, _config.DownloadDir);
        _logger.LogInformation(
            "Downloaded {Artist} — {Album}: {Landed} track(s) promoted to {Dir}",
            item.Artist.ArtistName, item.Album, landed, _config.DownloadDir);
        return DownloadOutcome.Success();
    }

    /// <summary>
    /// Reports a rejected/absent Deezer credential once, as a plain instruction rather than a wall of
    /// Python traceback. This is the failure a user is least equipped to diagnose from streamrip's own
    /// output and the one with the most specific fix, so it gets its own message naming the file to
    /// edit — and it's logged once per album rather than once per quality pass.
    /// </summary>
    private void LogCredentialFailure(DownloadFailure failure, PurchaseItem item, Pass pass)
    {
        var reason = failure == DownloadFailure.DeezerCredentialsMissing
            ? "no Deezer ARL is configured"
            : "Deezer rejected the configured ARL (expired, revoked, or invalidated by a password change)";
        _logger.LogError(
            "Deezer login failed while fetching {Artist} — {Album}: {Reason}. Downloads stay blocked "
            + "until the ARL is replaced in streamrip's config (see DEPLOYMENT.md §4); skipping the "
            + "remaining quality passes, which would fail identically. streamrip said:\n{Output}",
            item.Artist.ArtistName, item.Album, reason, pass.Output);
    }

    /// <summary>
    /// Whether a pass got the whole album. Without a track count from Deezer all we can say is that
    /// something arrived — still a far better test than the exit code, which is always 0.
    /// </summary>
    private static bool IsComplete(int got, int expected) => expected > 0 ? got >= expected : got > 0;

    /// <summary>
    /// The unstaged path used only when no library root is configured. Walks the same quality chain on
    /// a clean failure, but blind: with nowhere to stage, the exit code is the only signal, and it
    /// can't see a pass that "succeeded" while fetching nothing.
    /// </summary>
    private async Task<DownloadOutcome> RunUnverified(string url, PurchaseItem item)
    {
        var (quality, fallbacks) = QualityFor(item);
        var pass = await RunAt(quality, url, item, folder: null);
        if (pass.Result == RunResult.Success)
        {
            return DownloadOutcome.Success();
        }

        if (pass.CredentialFailure is var credential && credential != DownloadFailure.None)
        {
            LogCredentialFailure(credential, item, pass);
            return DownloadOutcome.Failed(credential);
        }

        foreach (var fallback in fallbacks)
        {
            // A hang/timeout is systemic (bad ARL, network) — downgrading would just burn another
            // timeout. Only keep going while the previous quality cleanly failed.
            if (pass.Result != RunResult.Failed || fallback == quality)
            {
                continue;
            }

            _logger.LogInformation(
                "Quality {Q} pass failed for {Artist} — {Album}; retrying at fallback {Fallback}",
                quality, item.Artist.ArtistName, item.Album, fallback);
            pass = await RunAt(fallback, url, item, folder: null);
            if (pass.Result == RunResult.Success)
            {
                return DownloadOutcome.Success();
            }

            if (pass.CredentialFailure != DownloadFailure.None)
            {
                LogCredentialFailure(pass.CredentialFailure, item, pass);
                return DownloadOutcome.Failed(pass.CredentialFailure);
            }
        }

        return DownloadOutcome.Failed();
    }

    private async Task<Pass> RunAt(string quality, string url, PurchaseItem item, string? folder)
    {
        var args = new List<string>();
        // We own dedup (purchase status), so streamrip's download-history DB must never skip a track —
        // otherwise re-downloading after a deleted/partial grab silently fetches only the cover.
        args.Add("--no-db");
        // We capture stdout rather than showing it, and streamrip draws rich progress bars there even
        // with no TTY attached. Left on, the redraws bury the per-track error lines that are the whole
        // point of logging this output when a pass comes up short.
        args.Add("--no-progress");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            args.Add("--folder");
            args.Add(folder);
        }
        if (!string.IsNullOrWhiteSpace(quality))
        {
            args.Add("--quality");
            args.Add(quality);
        }
        if (!string.IsNullOrWhiteSpace(_config.Codec))
        {
            args.Add("--codec");
            args.Add(_config.Codec);
        }
        args.Add("url");
        args.Add(url);

        var cmd = $"{_config.RipBinary} {string.Join(' ', args)}";
        _logger.LogInformation(
            "streamrip start: {Artist} — {Album} (quality {Quality}) → {Cmd}",
            item.Artist.ArtistName, item.Album, quality, cmd);

        var psi = new ProcessStartInfo
        {
            FileName = _config.RipBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read both pipes concurrently so a full buffer can't deadlock the child.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(_config.DownloadTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "streamrip TIMED OUT after {Timeout} for {Artist} — {Album}; killing it. {Cmd}",
                    _config.DownloadTimeout, item.Artist.ArtistName, item.Album, cmd);
                TryKill(process);
                var (to, te) = await Capture(stdoutTask, stderrTask);
                _logger.LogWarning("streamrip output before timeout for {Id}:\nstdout: {Out}\nstderr: {Err}",
                    item.Id, to, te);
                return new Pass(RunResult.TimedOut, to, te);
            }

            var (stdout, stderr) = await Capture(stdoutTask, stderrTask);
            if (process.ExitCode == 0)
            {
                // Note: this only means the process ran to completion — it says nothing about whether
                // any track was actually fetched. RunStaged counts files; see the class summary.
                _logger.LogInformation(
                    "streamrip finished for {Artist} — {Album} (quality {Quality})",
                    item.Artist.ArtistName, item.Album, quality);
                return new Pass(RunResult.Success, stdout, stderr);
            }

            _logger.LogWarning(
                "streamrip exited {Code} for {Artist} — {Album}. {Cmd}\nstdout: {Out}\nstderr: {Err}",
                process.ExitCode, item.Artist.ArtistName, item.Album, cmd, stdout, stderr);
            return new Pass(RunResult.Failed, stdout, stderr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch streamrip ({Bin}) for {Id}", _config.RipBinary, item.Id);
            return new Pass(RunResult.Failed, "", ex.Message);
        }
    }

    /// <summary>Awaits the captured output streams, tolerating either one faulting.</summary>
    private static async Task<(string Out, string Err)> Capture(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            return (await stdout, await stderr);
        }
        catch
        {
            return ("<unavailable>", "<unavailable>");
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill streamrip process {Pid}", SafePid(process));
        }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; }
        catch { return -1; }
    }
}
