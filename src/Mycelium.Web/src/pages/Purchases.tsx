import { useEffect, useState } from 'react'
import type { CSSProperties, ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  addManualPurchase,
  clearRating,
  downloadPurchase,
  getDownloadStatus,
  getPurchases,
  rate,
  recheckUpgrade,
  dismissUpgrade,
  removeManualPurchase,
  setDeezerArl,
  setDownloadsAutomatic,
  setDownloadsFast,
  unsendPurchase,
} from '../api/discovery'
import { useArtAccent } from '../art/artColors'
import type {
  ArlUpdateResult,
  DownloadFailure,
  DownloadSnapshot,
  FeedItem,
  AudioQuality,
  ManualAddResult,
  PurchaseItem,
} from '../types'
import { useAuth } from '../auth/AuthContext'
import { MergeAlbumPane } from '../components/MergeAlbumPane'
import { IconClear, IconDownload, IconUndo, IconWrench } from '../components/icons'

function Avatar({ item }: { item: PurchaseItem }) {
  const label = item.album ?? item.artist.artistName
  if (item.imageUrl) {
    return <img className="disc-avatar" src={item.imageUrl} alt={label} width={48} height={48} />
  }
  return (
    <div className="disc-avatar disc-avatar-fallback" style={{ width: 48, height: 48, fontSize: 20 }}>
      {label.charAt(0).toUpperCase()}
    </div>
  )
}

// A queue row, themed from its album/artist art (same `--art-accent` plumbing as the Discover feed):
// the shared `.disc-row` styling turns that into the tinted background + border + glow automatically.
// What each failure means, in the user's terms. `note` is the short tag shown on the row; `banner`
// is the fuller explanation raised once for a systemic failure, and says what to actually do —
// otherwise a dead ARL reads as "the downloader is broken" with Retry as the only affordance, and
// retrying is precisely what cannot work.
const FAILURE_COPY: Record<
  DownloadFailure,
  { note: string; banner?: { title: string; detail: string } } | undefined
> = {
  None: undefined,
  Unknown: { note: "Couldn't download" },
  NoTracksAvailable: { note: 'Deezer served no tracks' },
  // Not really failures — the library is fine, the upgrade just didn't happen. Worded so neither
  // reads as "something broke", because in both cases the existing copy is untouched.
  NoBetterQualityAvailable: { note: 'Deezer has nothing better' },
  UpgradeNotPossible: {
    note: "Couldn't replace the existing copy",
    banner: {
      title: "An upgrade couldn't be swapped in",
      detail:
        'A better copy downloaded, but the files it would replace could not be located from here — '
        + 'so nothing was moved and the library is unchanged. This is almost always PLEX_PATH_MAP: '
        + 'Plex reports paths in its own namespace, and each one needs a mapping onto the paths '
        + 'mounted into this container (e.g. "/media/music:/music"). See DEPLOYMENT.md.',
    },
  },
  DeezerAuth: {
    note: 'Deezer login rejected',
    banner: {
      title: 'Deezer login expired — downloads are blocked',
      detail:
        'Deezer rejected the saved session token (ARL). It expires on its own, and is also '
        + 'invalidated by logging out or changing your password. Downloads will keep failing '
        + 'identically until it is replaced: put a fresh ARL in streamrip\u2019s config.toml under '
        + '[deezer], then retry. Nothing needs restarting.',
    },
  },
  DeezerCredentialsMissing: {
    note: 'No Deezer login configured',
    banner: {
      title: 'No Deezer login configured — downloads cannot run',
      detail:
        'streamrip has no ARL set, so it can\u2019t reach Deezer at all. Set one in its config.toml '
        + 'under [deezer] to enable downloads.',
    },
  },
}

// Where to get an ARL. Written out in the banner rather than linked, because the moment a user reads
// this is the moment downloads are broken, and "go read the deployment doc" is a worse answer than
// four lines they can follow immediately. The cookie is per-browser-session, hence the emphasis on
// being logged in — the commonest mistake is copying it from a signed-out tab.
const ARL_STEPS = [
  'Open deezer.com in a browser and make sure you are logged in.',
  'Open DevTools (F12) → Application (Chrome) or Storage (Firefox).',
  'Under Cookies → https://www.deezer.com, find the row named "arl".',
  'Copy its Value — a long string of letters and numbers — and paste it below.',
]

// The blocked-downloads banner. Deliberately part of the download panel rather than a page-level
// alert: it describes the state of the drainer, and sits next to the counts that would otherwise be
// the only hint that every attempt is failing the same way.
//
// It also carries the fix. An ARL expires on its own and is the only credential streamrip accepts, so
// this is a recurring chore — putting the paste box in the banner that reports the problem is the
// difference between a 30-second fix and an SSH session against a TOML file.
function BlockedBanner({ failure, onFixed }: { failure: DownloadFailure; onFixed: () => void }) {
  const [arl, setArl] = useState('')
  const [done, setDone] = useState<ArlUpdateResult | null>(null)
  const banner = FAILURE_COPY[failure]?.banner

  const save = useMutation({
    mutationFn: () => setDeezerArl(arl.trim()),
    onSuccess: (result) => {
      setDone(result)
      setArl('')
      onFixed()
    },
  })

  if (!banner) return null

  // After a successful save the snapshot refetch clears `blocking`, unmounting this — but the refetch
  // is a round trip, so confirm inline first. Naming the account is what proves the right cookie was
  // pasted; a valid ARL for the wrong Deezer login would otherwise look identical to the right one.
  if (done?.saved) {
    return (
      <div className="dl-blocked fixed" role="status">
        <strong className="dl-blocked-title">
          Deezer login updated{done.accountName ? ` — signed in as ${done.accountName}` : ''}
        </strong>
        <span className="dl-blocked-detail">
          {done.requeued > 0
            ? `${done.requeued} blocked download${done.requeued === 1 ? '' : 's'} returned to the queue.`
            : 'Downloads are unblocked.'}
          {!done.lossless
            && ' This account has no lossless entitlement, so FLAC requests will fall back to MP3.'}
        </span>
      </div>
    )
  }

  return (
    <div className="dl-blocked" role="alert">
      <strong className="dl-blocked-title">{banner.title}</strong>
      <span className="dl-blocked-detail">{banner.detail}</span>
      <details className="dl-arl-help">
        <summary>Where do I find the ARL?</summary>
        <ol className="dl-arl-steps">
          {ARL_STEPS.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
      </details>
      <form
        className="dl-arl-form"
        onSubmit={(e) => {
          e.preventDefault()
          if (arl.trim()) save.mutate()
        }}
      >
        <input
          className="dl-arl-input"
          type="password"
          value={arl}
          spellCheck={false}
          autoComplete="off"
          placeholder="Paste the new ARL cookie value"
          aria-label="New Deezer ARL"
          disabled={save.isPending}
          onChange={(e) => setArl(e.target.value)}
        />
        <button className="disc-btn up" type="submit" disabled={save.isPending || !arl.trim()}>
          {save.isPending ? 'Checking…' : 'Save'}
        </button>
      </form>
      {/* The server checks the token with Deezer before writing it, so a failure here means the
          token itself is wrong — worth saying plainly, since the alternative is saving something
          broken and watching the same downloads fail again. */}
      {save.isError && <span className="dl-arl-error">{(save.error as Error).message}</span>}
    </div>
  )
}

function PurchaseRow({ item, actions }: { item: PurchaseItem; actions: ReactNode }) {
  const accent = useArtAccent(item.imageUrl)
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  return (
    <div className="disc-row" style={accentStyle}>
      <Avatar item={item} />
      {/* The name/provenance block deep-links into Browse, opened + filtered to this artist
          (the same /browse?artist= mechanism the Discover "Go to artist" link uses) so you can
          jump from a queued album to the artist's full readout. */}
      <Link
        className="disc-row-main disc-row-link"
        to={`/browse?artist=${encodeURIComponent(item.artist.artistName)}`}
        title={`Go to ${item.artist.artistName} in Browse`}
      >
        <div className="disc-name">{item.album ?? item.artist.artistName}</div>
        <span className="disc-provenance">
          {item.album
            ? `Album · ${item.artist.artistName}`
            : item.sources.length > 0
              ? `Artist · via ${item.sources.slice(0, 3).join(', ')}`
              : 'Artist'}
          {/* Nothing rated this one, so it has no provenance to show and it won't disappear when a
              rating changes — say where it came from, or it reads as an unexplained row. */}
          {item.manual && <span className="disc-provenance"> · added by link</span>}
          {/* Why this row failed, inline with its provenance. The banner covers the systemic case in
              full; this is what distinguishes "this album wasn't available" from "nothing is". */}
          {item.status === 'Failed' && FAILURE_COPY[item.failure] && (
            <span className="dl-fail-note"> · {FAILURE_COPY[item.failure]!.note}</span>
          )}
        </span>
      </Link>
      <div className="disc-actions">{actions}</div>
    </div>
  )
}

// How each tier reads. The app's vocabulary is Lossy/Lossless; these are the names a listener uses
// (same labels as the Discover upgrade card).
const QUALITY_LABEL: Record<AudioQuality, string> = {
  Lossy: 'MP3',
  Lossless: 'FLAC',
}

type Tone = 'good' | 'bad' | 'dim'

// Where an upgrade stands, in one line. Plex keeps star ratings against the release an album is
// matched to, so "did it keep its match" is the part of an upgrade most worth reporting: a copy
// matched to a different release comes up unrated, and nothing else on screen would say so.
function upgradeStatus(item: PurchaseItem): { text: string; tone: Tone } {
  const u = item.upgrade
  switch (item.status) {
    case 'Pending':
      return { text: 'Waiting to download', tone: 'dim' }
    case 'Queued':
      return { text: 'Queued…', tone: 'dim' }
    case 'Downloading':
      return { text: 'Downloading…', tone: 'dim' }
    case 'Failed': {
      const note = FAILURE_COPY[item.failure]?.note ?? "Couldn't download"
      // The refusal detail is the specific answer ("got 9 of 10 tracks"); the note alone only says
      // which gate refused. Only these two failures come from the swap, so only they can own the
      // detail — a download that failed before reaching it would otherwise show a stale reason.
      const fromSwap = item.failure === 'UpgradeNotPossible' || item.failure === 'NoBetterQualityAvailable'
      const detail = fromSwap ? u?.refusalDetail : null
      return { text: detail ? `${note} — ${detail}` : note, tone: 'bad' }
    }
  }

  // Sent or InLibrary: downloaded, and — if the report says so — swapped in.
  if (!u || u.refusalDetail || u.filesMoved === 0) {
    return { text: 'Downloaded — waiting for Plex to pick it up', tone: 'dim' }
  }
  switch (u.match) {
    case 'Waiting':
      return { text: 'Swapped in — checking Plex kept its ratings…', tone: 'dim' }
    case 'Kept':
      return { text: 'Upgraded · Plex kept its match, ratings carried over', tone: 'good' }
    case 'Rematched':
      return {
        text: 'Upgraded · Plex matched a different release, so it was rematched and ratings restored',
        tone: 'good',
      }
    case 'NeedsFixMatch':
      return {
        text: 'Upgraded, but Plex matched a different release and rematching didn\u2019t take — ratings '
          + 'may be missing. Use Fix Match in Plex, then Recheck.',
        tone: 'bad',
      }
    case 'NotMatched':
      return { text: 'Upgraded · the old copy wasn\u2019t matched in Plex, so there were no ratings to carry', tone: 'dim' }
    default:
      return { text: 'Upgraded', tone: 'good' }
  }
}

// Wants a person to act: a failed download, or ratings Plex may have dropped.
const needsAttention = (item: PurchaseItem) =>
  item.status === 'Failed' || item.upgrade?.match === 'NeedsFixMatch'

// Rank for the section: what needs attention, then what's moving, then what's waiting, then history.
function upgradeRank(item: PurchaseItem) {
  if (needsAttention(item)) return 0
  if (item.status === 'Downloading' || item.status === 'Queued' || item.status === 'Sent') return 1
  if (item.upgrade?.match === 'Waiting') return 1
  if (item.status === 'Pending') return 2
  return 3
}

function UpgradeRow({ item, actions }: { item: PurchaseItem; actions: ReactNode }) {
  const accent = useArtAccent(item.imageUrl)
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  const u = item.upgrade
  const from = u?.replacedQuality ?? item.ownedQuality
  const to = u?.newQuality ?? item.targetQuality
  const status = upgradeStatus(item)
  // Only a swap that actually moved something has anything to show underneath.
  const swapped = u && !u.refusalDetail && u.filesMoved > 0
  return (
    <div className="disc-row up-row" style={accentStyle}>
      <Avatar item={item} />
      <div className="disc-row-main">
        <Link
          className="disc-row-link"
          to={`/browse?artist=${encodeURIComponent(item.artist.artistName)}`}
          title={`Go to ${item.artist.artistName} in Browse`}
        >
          <div className="disc-name">{item.album}</div>
          <span className="disc-provenance">
            {item.artist.artistName}
            {from && to && (
              <span className="up-quality">
                {' · '}{QUALITY_LABEL[from]} → {QUALITY_LABEL[to]}
              </span>
            )}
          </span>
        </Link>
        <div className={`up-status ${status.tone}`}>{status.text}</div>
        {swapped && (
          <details className="up-details">
            <summary>Details</summary>
            <dl>
              <dt>Old copy</dt>
              <dd>
                {u.filesMoved} file{u.filesMoved === 1 ? '' : 's'} moved to <code>{u.movedTo}</code>
                {u.previousFolder && (
                  <> — out of <code>{u.previousFolder}</code></>
                )}
              </dd>
              {u.albumFolder && (
                <>
                  <dt>New copy</dt>
                  <dd><code>{u.albumFolder}</code></dd>
                </>
              )}
              {u.oldMatch && (
                <>
                  <dt>Plex match</dt>
                  <dd>
                    <code>{u.oldMatch}</code>
                    {u.newMatch && (
                      <> — Plex picked <code>{u.newMatch}</code> for the new copy</>
                    )}
                  </dd>
                </>
              )}
              <dt>Swapped</dt>
              <dd>{new Date(u.at).toLocaleString()}</dd>
            </dl>
          </details>
        )}
      </div>
      <div className="disc-actions">{actions}</div>
    </div>
  )
}

// Re-renders once a second so a countdown ticks smoothly. The snapshot itself is only polled every
// few seconds — the deadline it carries is absolute, so the remaining time is derived locally.
function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    const id = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(id)
  }, [active])
  return now
}

// "in 47s" / "in 12m 30s" / "any moment" once the deadline passes (the server acts on its own clock,
// and a jittered wait means the exact instant was never a promise).
function countdown(iso: string, now: number) {
  const remaining = Math.round((new Date(iso).getTime() - now) / 1000)
  if (remaining <= 0) return 'any moment'
  if (remaining < 60) return `in ${remaining}s`
  const minutes = Math.floor(remaining / 60)
  const seconds = remaining % 60
  return minutes < 10 ? `in ${minutes}m ${seconds}s` : `in ${minutes}m`
}

// "58m left" / "45s left" — how much of the fast-mode hour is still running. Unlike `countdown` this
// counts down a window rather than to an event, so it stays in whole minutes until the last one.
function remaining(iso: string, now: number) {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000)
  if (seconds <= 0) return 'ending'
  return seconds < 60 ? `${seconds}s left` : `${Math.ceil(seconds / 60)}m left`
}

function Monitor({
  s,
  onToggleAutomatic,
  onToggleFast,
  onFixed,
  busy,
}: {
  s: DownloadSnapshot
  onToggleAutomatic: (automatic: boolean) => void
  onToggleFast: (fast: boolean) => void
  // Called after the Deezer credential is replaced: the snapshot's `blocking` flag and the failed
  // rows both change server-side, so both queries have to be re-read for the page to settle.
  onFixed: () => void
  busy: boolean
}) {
  const current = s.current[0]
  // Nothing to say while idle: the counts below already show what's queued.
  const activity = current
    ? `⬇ Downloading: ${current.album ?? current.artist.artistName} — ${current.artist.artistName}`
    : s.queued > 0 && !s.automatic
      ? `${s.queued} album${s.queued === 1 ? '' : 's'} queued — use Download now`
      : null

  // What the drainer does next. The wait between two albums wins when there is one, since it's the
  // nearer event; otherwise it's the next automatic sweep — which only means anything on auto, as the
  // pass is a no-op in manual mode. Nothing to show while an album is actively downloading: the
  // activity line already says so, and the next wait hasn't been scheduled yet.
  const next = s.nextItemAt
    ? { label: 'Next album', at: s.nextItemAt }
    : s.automatic && s.nextBatchAt
      ? { label: s.queued > 0 ? 'Next batch' : 'Next check', at: s.nextBatchAt }
      : null
  const now = useNow(next !== null || s.fastUntil !== null)

  return (
    <div className="dl-monitor">
      <div className="dl-monitor-head">
        {/* The drainer switch. Server-side state (shared, and persisted across redeploys), so this
            reflects what the backend will actually do rather than a local preference. */}
        <button
          className={s.automatic ? 'dl-switch on' : 'dl-switch off'}
          role="switch"
          aria-checked={s.automatic}
          disabled={busy}
          title={
            s.automatic
              ? 'Downloading automatically — switch to manual'
              : 'Manual only — switch to automatic'
          }
          onClick={() => onToggleAutomatic(!s.automatic)}
        >
          <span className="dl-switch-track">
            <span className="dl-switch-knob" />
          </span>
          <span className={s.automatic ? 'dl-badge on' : 'dl-badge off'}>
            {s.automatic ? 'auto' : 'manual'}
          </span>
        </button>
        {/* Fast mode: a one-hour burst that queues the whole backlog instead of one batch per sweep.
            Time-boxed server-side, so this is a button rather than a switch — pressing it again while
            it runs ends the burst early. Only the automatic pass honours it, so in manual mode it says
            so instead of pretending to be armed. */}
        <button
          className={s.fastUntil ? 'dl-fast on' : 'dl-fast'}
          disabled={busy}
          title={
            s.fastUntil
              ? 'Fast mode on — queueing everything. Click to end it now.'
              : s.automatic
                ? 'Queue every pending album now, for one hour'
                : 'Queue every pending album for one hour — takes effect once the drainer is on automatic'
          }
          onClick={() => onToggleFast(!s.fastUntil)}
        >
          ⚡ {s.fastUntil ? remaining(s.fastUntil, now) : 'fast'}
        </button>
        <span className="dl-backend">backend: {s.backend}</span>
      </div>
      {activity && <div className={current ? 'dl-activity active' : 'dl-activity'}>{activity}</div>}
      {next && <div className="dl-next">{next.label} <strong>{countdown(next.at, now)}</strong></div>}
      {s.blocking !== 'None' && <BlockedBanner failure={s.blocking} onFixed={onFixed} />}
      <div className="dl-counts">
        <span>Queued <strong>{s.queued}</strong></span>
        <span>Downloading <strong>{s.downloading}</strong></span>
        <span>Complete <strong>{s.complete}</strong></span>
        <span>Failed <strong>{s.failed}</strong></span>
      </div>
    </div>
  )
}

// What each outcome of a pasted link says back. The refusals are the interesting half: "already
// owned" and "already queued" are successes from the user's point of view (the album is handled),
// while a bad link is the only one that means "try again with something else".
type AddCopy = { text: string; tone: 'ok' | 'note' | 'error' }

const ADD_COPY: Record<ManualAddResult, AddCopy> = {
  Added: { text: 'Queued to download.', tone: 'ok' },
  AlreadyQueued: { text: 'Already on the list.', tone: 'note' },
  AlreadyOwned: {
    text: 'Already in the library — delete it in Plex and run “Refresh from Plex” on the Other tab '
      + 'if you want it fetched again.',
    tone: 'note',
  },
  NotFound: { text: "Deezer doesn't have an album with that id.", tone: 'error' },
  BadLink: { text: 'That isn\'t a Deezer album link — copy the URL from an album page.', tone: 'error' },
}

// Every answer the server can give has to put words on the page, including one this build has no copy
// for. That case is not hypothetical: ManualAddResult once shipped as an ordinal rather than its name,
// so every refusal missed the map above and rendered nothing at all — the button looked broken rather
// than refusing, and the reason (already owned) was sitting in the response the whole time. The map
// stays exhaustive over the union so a new case is a compile error; this is the guard for the answers
// the type system can't see.
function addCopy(result: ManualAddResult): AddCopy {
  return (
    ADD_COPY[result] ?? {
      text: `The server refused this paste (${String(result)}), and this page has no message for that `
        + 'answer — check the backend log for the reason.',
      tone: 'error',
    }
  )
}

// Paste a Deezer album link to queue it directly. The way in for releases the artist-rooted sync can
// never reach: a various-artists compilation is in no contributor's discography (and Deezer's own
// "Various Artists" artist lists no albums at all), so nothing can surface it in the feed. Same blind
// spot covers regional reissues and releases credited to a differently-spelled act.
//
// Deliberately queue-only — it doesn't rate anything or touch the similarity graph. A compilation
// isn't a taste anchor, so this is an acquisition, not a preference.
function PasteAlbum({ onAdded }: { onAdded: () => void }) {
  const [url, setUrl] = useState('')
  const [outcome, setOutcome] = useState<ManualAddResult | null>(null)

  const add = useMutation({
    mutationFn: (u: string) => addManualPurchase(u),
    onSuccess: (result) => {
      setOutcome(result.result)
      // Keep the box populated on a refusal so a near-miss paste can be edited rather than retyped.
      if (result.result === 'Added') setUrl('')
      onAdded()
    },
  })

  const submit = () => {
    const trimmed = url.trim()
    if (trimmed.length === 0 || add.isPending) return
    setOutcome(null)
    add.mutate(trimmed)
  }

  const copy = outcome ? addCopy(outcome) : null

  return (
    <div className="dl-section paste-album">
      <h2 className="feed-section-title">Add an album by link</h2>
      <div className="paste-album-row">
        <input
          type="text"
          className="paste-album-input"
          placeholder="https://www.deezer.com/album/…"
          value={url}
          onChange={(e) => {
            setUrl(e.target.value)
            setOutcome(null)
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') submit()
          }}
          disabled={add.isPending}
        />
        <button
          className="disc-btn up"
          title="Queue this album"
          disabled={add.isPending || url.trim().length === 0}
          onClick={submit}
        >
          <IconDownload />
        </button>
      </div>
      {add.isError && <p className="error">{(add.error as Error).message}</p>}
      {copy && (
        <p className={copy.tone === 'error' ? 'error' : 'disc-sub'}>
          {copy.tone === 'error' ? copy.text : <em>{copy.text}</em>}
        </p>
      )}
    </div>
  )
}

export default function Purchases() {
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [mergingId, setMergingId] = useState<string | null>(null)

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['purchases'],
    queryFn: getPurchases,
    enabled: !!user,
    refetchInterval: 5000, // keep the list moving as the drainer works
  })
  const { data: status } = useQuery({
    queryKey: ['download-status'],
    queryFn: getDownloadStatus,
    enabled: !!user,
    refetchInterval: 3000,
  })

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['purchases'] })
    queryClient.invalidateQueries({ queryKey: ['download-status'] })
  }
  const download = useMutation({ mutationFn: (id: string) => downloadPurchase(id), onSuccess: invalidate })
  const unsend = useMutation({ mutationFn: (id: string) => unsendPurchase(id), onSuccess: invalidate })
  const setAutomatic = useMutation({
    mutationFn: (automatic: boolean) => setDownloadsAutomatic(automatic),
    onSuccess: invalidate,
  })
  // Turning fast mode on enqueues on the server before it answers, so both queries are re-read: the
  // panel's counts and the list's rows both move in the same click.
  const setFast = useMutation({
    mutationFn: (fast: boolean) => setDownloadsFast(fast),
    onSuccess: invalidate,
  })

  // "Nevermind" — clearing the underlying like drops the item from the queue on the next reconcile
  // (the list is derived from liked-but-unowned ratings), so this intercepts an item before download.
  // clearRating only reads artist/album, so a minimal feed item from the row is enough.
  //
  // A hand-added row has no rating behind it — clearing one would be a no-op and the row would sit
  // there forever — so it's deleted directly instead.
  //
  // An upgrade row is the third case, and clearing is the wrong verb for it: the user owns this record
  // and likes it — the row exists only because the copy on disk is below what they can have. Deleting
  // that like to get rid of the row would throw away a verdict about the music to answer a question
  // about a file. Thumbing it down as an upgrade is the verdict that actually fits ("keep the copy we
  // have"), and it drops the row on the same reconcile.
  const remove = useMutation({
    mutationFn: async (item: PurchaseItem) => {
      // A manual row is deleted outright whatever state it's in, queued included: nothing re-creates
      // it, and the drainer re-reads the row before fetching, so a stale id left in its queue finds
      // nothing there and is skipped.
      if (item.manual) return removeManualPurchase(item.id)

      const feedItem: FeedItem = {
        kind: item.kind,
        artist: item.artist,
        album: item.album,
        imageUrl: item.imageUrl,
        score: 0,
        sources: [],
        deezerAlbumId: item.deezerAlbumId,
        year: null,
        reconsider: null,
        ownedQuality: null,
      }
      await (item.kind === 'UpgradeAlbum' ? rate(feedItem, 'down') : clearRating(feedItem))

      // Dropping the want isn't enough once a row has been handed to the drainer: the reconcile
      // only prunes Pending/Failed rows, so an unwanted Queued one survives every pass and gets
      // downloaded anyway. Moving it back to Pending is what puts it back in the prune's reach —
      // and it defuses the id already sitting in the drainer's in-memory queue too, since the
      // consumer re-reads the status before fetching and skips a row that is no longer Queued.
      //
      // Deliberately after the rating is cleared rather than before: a Pending row that is *still*
      // wanted is precisely what the auto-enqueue pass picks up, and during a fast-mode burst that
      // pass runs every few seconds. Clearing first means every reconcile that could re-queue the
      // row prunes it instead — the enqueue pass reconciles before it selects candidates.
      if (item.status === 'Queued') await unsendPurchase(item.id)
    },
    onSuccess: () => {
      invalidate()
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      queryClient.invalidateQueries({ queryKey: ['feed'] })
    },
  })
  const recheck = useMutation({ mutationFn: (id: string) => recheckUpgrade(id), onSuccess: invalidate })
  const dismiss = useMutation({ mutationFn: (id: string) => dismissUpgrade(id), onSuccess: invalidate })
  const busy = download.isPending || unsend.isPending || remove.isPending || recheck.isPending
    || dismiss.isPending

  // The remove (✕) action shared by pending/failed rows — cancels the want before it downloads. An
  // upgrade row isn't a want being dropped (the record is already on the shelf), so it says what it
  // actually does: settle for the copy in the library.
  const removeBtn = (item: PurchaseItem) => (
    <button
      className="disc-btn"
      title={item.kind === 'UpgradeAlbum'
        ? 'Keep the copy in the library — stop offering this upgrade'
        : 'Remove from queue'}
      disabled={busy}
      onClick={() => remove.mutate(item)}
    >
      <IconClear />
    </button>
  )

  // "Already in library?" — opens the merge pane to reconcile a near-miss title against an album
  // Plex already has (which is why it's stuck in the queue rather than flipping to in-library).
  const mergeBtn = (item: PurchaseItem) => (
    <button
      className="disc-btn"
      title="Match an album already in the library"
      disabled={busy}
      onClick={() => setMergingId(item.id)}
    >
      <IconWrench />
    </button>
  )
  // What an upgrade row offers depends on how far it got. Once the swap has happened there's no
  // taking it back from here (the old copy is in the trash folder, recoverable by hand), so a finished
  // row only offers Recheck — when its ratings may need rescuing — and Dismiss, once it has landed.
  const upgradeActions = (item: PurchaseItem): ReactNode => {
    switch (item.status) {
      case 'Pending':
        return (
          <>
            <button
              className="disc-btn up"
              title="Download now"
              disabled={busy}
              onClick={() => download.mutate(item.id)}
            >
              <IconDownload />
            </button>
            {removeBtn(item)}
          </>
        )
      case 'Queued':
        return (
          <>
            <button
              className="disc-btn"
              title="Cancel download — back to waiting"
              disabled={busy}
              onClick={() => unsend.mutate(item.id)}
            >
              <IconUndo />
            </button>
            {removeBtn(item)}
          </>
        )
      case 'Downloading':
        return <span className="dl-spinner" title="Downloading">⬇</span>
      case 'Failed':
        return (
          <>
            <button
              className="disc-btn up"
              title="Retry download"
              disabled={busy}
              onClick={() => download.mutate(item.id)}
            >
              Retry
            </button>
            {removeBtn(item)}
          </>
        )
      default:
        return (
          <>
            {item.upgrade?.match === 'NeedsFixMatch' && (
              <button
                className="disc-btn up"
                title="Check the Plex match again, and rematch it to the old copy's release if it has drifted"
                disabled={busy}
                onClick={() => recheck.mutate(item.id)}
              >
                Recheck
              </button>
            )}
            {item.status === 'InLibrary' && item.upgrade && (
              <button
                className="disc-btn"
                title="Dismiss — clear this finished upgrade off the list"
                disabled={busy}
                onClick={() => dismiss.mutate(item.id)}
              >
                <IconClear />
              </button>
            )}
          </>
        )
    }
  }

  const mergingItem = mergingId ? (data ?? []).find((i) => i.id === mergingId) : undefined

  if (!user) {
    return (
      <section>
        <h1>Download</h1>
        <p><em>Log in to see the albums you've queued to download.</em></p>
      </section>
    )
  }

  const all = data ?? []
  // Upgrades get their own section: they replace something already in the library, so what matters
  // about them — what moved where, whether ratings survived — isn't what matters about a gap.
  const upgrades = all
    .filter((i) => i.kind === 'UpgradeAlbum' && i.album)
    .sort((a, b) =>
      upgradeRank(a) - upgradeRank(b)
      || (b.upgrade?.at ?? b.requestedAt).localeCompare(a.upgrade?.at ?? a.requestedAt))
  const upgradesActive = upgrades.filter((i) => i.status !== 'InLibrary').length
  const items = all.filter((i) => i.kind !== 'UpgradeAlbum')
  // Only albums are actionable here — they're what the downloader can grab. Liked artists still seed
  // recommendations, but they're managed on the Artists page, not shown as wishlist rows.
  // Everything in the download pipeline shows in the "Downloading now" section: the one actively
  // fetching plus any requested-and-waiting (Queued) behind it — matching the monitor's tally.
  const downloading = items.filter(
    (i) => (i.status === 'Downloading' || i.status === 'Queued') && i.album,
  )
  const pendingAlbums = items.filter((i) => i.status === 'Pending' && i.album)
  const sent = items.filter((i) => i.status === 'Sent' && i.album)
  const failed = items.filter((i) => i.status === 'Failed' && i.album)
  const shownCount =
    downloading.length + pendingAlbums.length + sent.length + failed.length + upgradesActive

  const row = (item: PurchaseItem, actions: ReactNode) => (
    <PurchaseRow key={item.id} item={item} actions={actions} />
  )

  return (
    <section>
      <h1>Downloading {shownCount > 0 ? `(${shownCount})` : ''}</h1>

      {status && (
        <Monitor
          s={status}
          busy={setAutomatic.isPending || setFast.isPending}
          onToggleAutomatic={(automatic) => setAutomatic.mutate(automatic)}
          onToggleFast={(fast) => setFast.mutate(fast)}
          onFixed={() => {
            queryClient.invalidateQueries({ queryKey: ['download-status'] })
            queryClient.invalidateQueries({ queryKey: ['purchases'] })
          }}
        />
      )}

      <PasteAlbum onAdded={invalidate} />

      {isError && <p className="error">Failed to load wishlist: {(error as Error).message}</p>}
      {isPending && <p><em>Loading…</em></p>}

      {data && shownCount === 0 && upgrades.length === 0 && (
        <p>
          <em>
            Nothing here yet. Thumbs-up albums on the <Link to="/">Discover</Link> page, or add an
            artist's albums from the <Link to="/browse">Browse</Link> page, to queue them.
          </em>
        </p>
      )}

      {downloading.length > 0 && (
        <div className="dl-section">
          <h2 className="feed-section-title">
            Downloading now <span className="feed-count">{downloading.length}</span>
          </h2>
          <div className="disc-list">
            {downloading.map((item) =>
              row(
                item,
                item.status === 'Downloading' ? (
                  // Nothing is offered mid-fetch: the download is already running and can't be
                  // called back, so a button here would only pretend to stop it.
                  <span className="dl-spinner" title="Downloading">⬇</span>
                ) : (
                  // Still only waiting its turn, so it can still be called off — either back onto
                  // the pending list, or off the list entirely.
                  <>
                    <span className="disc-provenance" title="Queued to download">Queued…</span>
                    <button
                      className="disc-btn"
                      title="Cancel download — back to the pending list"
                      disabled={busy}
                      onClick={() => unsend.mutate(item.id)}
                    >
                      <IconUndo />
                    </button>
                    {removeBtn(item)}
                  </>
                ),
              ),
            )}
          </div>
        </div>
      )}

      {failed.length > 0 && (
        <div className="dl-section">
          <h2 className="feed-section-title">
            Failed <span className="feed-count">{failed.length}</span>
          </h2>
          <p className="disc-sub">
            <em>
              {failed.some((i) => i.failure === 'DeezerAuth' || i.failure === 'DeezerCredentialsMissing')
                ? 'Blocked by the Deezer login above — fix that first, then retry.'
                : "The downloader couldn't grab these — retry."}
            </em>
          </p>
          <div className="disc-list">
            {failed.map((item) =>
              row(
                item,
                <>
                  <button
                    className="disc-btn up"
                    title="Retry download"
                    disabled={busy}
                    onClick={() => download.mutate(item.id)}
                  >
                    Retry
                  </button>
                  {removeBtn(item)}
                </>,
              ),
            )}
          </div>
        </div>
      )}

      {pendingAlbums.length > 0 && (
        <div className="dl-section">
          <div className="disc-list">
            {pendingAlbums.map((item) =>
              row(
                item,
                <>
                  <button
                    className="disc-btn up"
                    title="Download now"
                    disabled={busy}
                    onClick={() => download.mutate(item.id)}
                  >
                    <IconDownload />
                  </button>
                  {mergeBtn(item)}
                  {removeBtn(item)}
                </>,
              ),
            )}
          </div>
        </div>
      )}

      {sent.length > 0 && (
        <div className="dl-section">
          <h2 className="feed-section-title">
            Complete <span className="feed-count">{sent.length}</span>
          </h2>
          <div className="disc-list">
            {sent.map((item) =>
              row(
                item,
                <>
                  {mergeBtn(item)}
                  <button
                    className="disc-btn"
                    title="Undo — move back to queued"
                    disabled={busy}
                    onClick={() => unsend.mutate(item.id)}
                  >
                    <IconUndo />
                  </button>
                </>,
              ),
            )}
          </div>
        </div>
      )}

      {upgrades.length > 0 && (
        <div className="dl-section">
          <h2 className="feed-section-title">
            Upgrades <span className="feed-count">{upgrades.length}</span>
          </h2>
          <div className="disc-list">
            {upgrades.map((item) => (
              <UpgradeRow
                key={item.id}
                item={item}
                actions={upgradeActions(item)}
              />
            ))}
          </div>
          {recheck.isError && <p className="error">{(recheck.error as Error).message}</p>}
          {dismiss.isError && <p className="error">{(dismiss.error as Error).message}</p>}
        </div>
      )}

      {mergingItem?.album && (
        <MergeAlbumPane
          artist={mergingItem.artist.artistName}
          album={mergingItem.album}
          onClose={() => setMergingId(null)}
          onMerged={() => {
            setMergingId(null)
            invalidate()
            queryClient.invalidateQueries({ queryKey: ['ratings'] })
            queryClient.invalidateQueries({ queryKey: ['feed'] })
            queryClient.invalidateQueries({ queryKey: ['artist-discography'] })
          }}
        />
      )}
    </section>
  )
}
