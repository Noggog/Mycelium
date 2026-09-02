import { useEffect, useRef, useState, type CSSProperties } from 'react'
import {
  autoUpdate,
  flip,
  offset,
  safePolygon,
  shift,
  useDismiss,
  useFloating,
  useFocus,
  useHover,
  useInteractions,
  useRole,
} from '@floating-ui/react'
import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  blockAlbum,
  clearRating,
  getArtistAlbums,
  getMixedFeed,
  rate,
  snooze,
  unblockAlbum,
  type SnoozeDuration,
  type AlbumVerdict,
  type Verdict,
} from '../api/discovery'
import { getDeezerPlayInfo, isDeezerBusy } from '../api/deezer'
import { getArtistLibraries } from '../api/library'
import { useArtAccent } from '../art/artColors'
import type { FeedItem, FeedKind, ReconsiderSignal, AudioQuality } from '../types'
import { useAuth } from '../auth/AuthContext'
import { DeezerSample } from '../components/DeezerSample'
import { MergeAlbumPane } from '../components/MergeAlbumPane'
import { PlexRatingStats } from '../components/PlexRatingStats'
import {
  IconApprove, IconBlock, IconCheck, IconDownload, IconIndifferent, IconMoon, IconReject, IconSkip,
  IconWrench, Spinner,
} from '../components/icons'
import { rateFeedback } from '../effects/effectsBus'

const PAGE_SIZE = 20

// How each tier reads on a card. The app's own vocabulary is Lossy/Lossless; these are the names a
// listener would use for the same thing.
const QUALITY_LABEL: Record<AudioQuality, string> = {
  Lossy: 'MP3',
  Lossless: 'FLAC',
}

// The badge shown on each card so it's obvious what action a card is asking for, keyed by kind.
const BADGE: Record<FeedKind, string> = {
  RecommendedArtist: 'Recommended New Artist',
  MissingAlbum: 'Add missing album',
  UpgradeAlbum: 'Upgrade album',
  RecommendedLibraryArtist: 'Recommended Artist',
  SeedLibraryArtist: 'Rate Unfamiliar Artist',
  LibraryArtist: 'Mark existing artist',
  ReconsiderArtist: 'Second Chance',
  SecondThoughtsArtist: 'Second Thoughts',
  IndifferentLikeArtist: 'Maybe You Like This',
  IndifferentDislikeArtist: "Maybe You Don't",
}

// The category filters, shown up top as toggle-able tag chips styled exactly like the per-row
// badges — clicking a chip shows/hides those kinds in the feed. Order mirrors how they read on a card.
//
// A chip can cover more than one kind, because a couple of the categories are two readings of the
// same question and nobody wants to reason about them separately: "Add missing album" covers both a
// record you don't have and a worse copy of one you do (both are "go get this album"), and "Second
// Thoughts" covers both directions of the ratings second-guessing a verdict — a thumbed-down band
// your Plex ratings like, and a thumbed-up one they don't. The cards keep their distinct badges;
// it's only the filter that groups them.
const FILTER_CHIPS: { kinds: FeedKind[]; label: string; tip: string }[] = [
  { kinds: ['RecommendedArtist'], label: 'Recommended New Artist', tip: 'Artists not yet in the library' },
  { kinds: ['RecommendedLibraryArtist'], label: 'Recommended Artist', tip: "Unrated artists already in the library" },
  { kinds: ['SeedLibraryArtist'], label: 'Rate Unfamiliar Artist', tip: "Rate artists not yet recommended to grow the frontier" },
  {
    kinds: ['MissingAlbum', 'UpgradeAlbum'],
    label: 'Add missing album',
    tip: "Albums you're missing, and ones you own in lower quality than you can get",
  },
  {
    // Plum first: the chip borrows that kind's name, so it should borrow its hue too.
    //
    // All four second-guessing kinds ride this one chip. They are the same question asked of the three
    // verdicts — "your own song ratings disagree with what you said" — and nobody wants to reason about
    // six directions separately. The cards keep their distinct badges; only the filter groups them.
    kinds: [
      'SecondThoughtsArtist', 'ReconsiderArtist',
      'IndifferentLikeArtist', 'IndifferentDislikeArtist',
    ],
    label: 'Second Thoughts',
    tip: 'Verdicts your Plex ratings argue with — bands you thumbed down but rate highly, ones you thumbed up but rate poorly, and ones you shrugged at but feel strongly about',
  },
]

const ALL_KINDS: FeedKind[] = [
  'RecommendedArtist', 'MissingAlbum', 'UpgradeAlbum', 'RecommendedLibraryArtist', 'SeedLibraryArtist',
  'ReconsiderArtist', 'SecondThoughtsArtist', 'IndifferentLikeArtist', 'IndifferentDislikeArtist',
]
// Default to everything on: the recommended sections (new + existing owned), the album section
// (gaps and upgrades), the seed section (owned artists nothing recommends yet) — rating those grows
// the frontier — and the second-guessing section (verdicts the user's own song ratings argue with,
// either way).
const DEFAULT_KINDS: FeedKind[] = [...ALL_KINDS]

const newSeed = () => Math.floor(Math.random() * 1_000_000_000)

// Which category chips are checked, persisted across sessions so unchecking e.g. "Rate Unfamiliar
// Artist" sticks the next time you open Discover. Stored as a JSON array of kinds in localStorage;
// any malformed/missing value falls back to the all-on default.
const SHOWN_PREF_KEY = 'mc.discover.shown'

// Bumped whenever a category is added, with the new kinds listed under the bump. A list stored before
// a kind existed can't have meant "off" for it, so on the first read after a bump those kinds are
// switched on and the new version is stamped — after which unchecking one sticks like any other.
// Without this a new section would stay invisible to everyone who has ever touched the chips.
const SHOWN_VERSION_KEY = 'mc.discover.shown.version'
const SHOWN_VERSION = 3
const KINDS_ADDED_IN: Record<number, FeedKind[]> = {
  2: ['SecondThoughtsArtist'],
  3: ['IndifferentLikeArtist', 'IndifferentDislikeArtist'],
}

// The chips toggle whole groups, so a group is all-on or all-off — there's no chip that could turn
// half of one back on. A stored list from before two kinds were grouped can hold exactly that split
// (an upgrades-off preference next to missing-albums-on, say), which would leave a lit chip serving
// only half its feed. Reading one on means the group is on.
function normalizeGroups(shown: Set<FeedKind>): Set<FeedKind> {
  for (const { kinds } of FILTER_CHIPS) {
    if (kinds.some((k) => shown.has(k))) {
      for (const k of kinds) shown.add(k)
    }
  }
  return shown
}

function readShownKinds(): Set<FeedKind> {
  try {
    const stored = localStorage.getItem(SHOWN_PREF_KEY)
    if (stored) {
      const parsed = JSON.parse(stored)
      if (Array.isArray(parsed)) {
        const kinds = parsed.filter((k): k is FeedKind => ALL_KINDS.includes(k as FeedKind))
        const shown = new Set(kinds)
        // Anything unstamped predates the versioning, i.e. version 1.
        const storedVersion = Number(localStorage.getItem(SHOWN_VERSION_KEY)) || 1
        for (let v = storedVersion + 1; v <= SHOWN_VERSION; v++) {
          for (const kind of KINDS_ADDED_IN[v] ?? []) {
            shown.add(kind)
          }
        }
        const before = shown.size
        normalizeGroups(shown)
        if (storedVersion < SHOWN_VERSION || shown.size !== before) {
          writeShownKinds(shown)
        }
        return shown
      }
    }
  } catch {
    // localStorage / JSON can throw (private mode, corrupt value) — fall back to the default.
  }
  return new Set(DEFAULT_KINDS)
}

function writeShownKinds(shown: Set<FeedKind>) {
  try {
    localStorage.setItem(SHOWN_PREF_KEY, JSON.stringify([...shown]))
    localStorage.setItem(SHOWN_VERSION_KEY, String(SHOWN_VERSION))
  } catch {
    // Ignore — the in-memory state still reflects the choice for this session.
  }
}

// How a row was marked this view (approve / meh / snooze, merged into an album already owned, or
// blocked for everyone) so it stays in place until the next natural refresh.
type RowMark = Verdict | 'snoozed' | 'merged' | 'blocked'
const MARK_LABEL: Record<RowMark, string> = {
  up: 'Added',
  down: 'Dismissed',
  indifferent: 'Noted',
  snoozed: 'Snoozed',
  merged: 'In library',
  blocked: 'Blocked for all',
}
// What each button promises, named by its *effect* rather than by the gesture. "Not interested" said
// nothing about which of the two negative verdicts you were picking — and that is the whole difference
// between them: a dislike blocks the band from your playlists, a shrug leaves it playing. A tooltip on
// an icon-only button is the entire affordance, so it has to answer "what will this do".
const ARTIST_ACTION_TITLE = {
  up: 'Like - Recommend',
  indifferent: "Indifferent - Keep playing, don't recommend",
  down: 'Dislike - Block',
} as const

// Albums aren't rated, they're fetched or passed over — so they get their own pair. An upgrade says
// what happens to the copy already on disk, which is the only thing that makes it different from a
// gap: one replaces a record, the other adds one.
const albumActionTitle = (kind: FeedKind) =>
  kind === 'UpgradeAlbum'
    ? { up: 'Download - replace the copy you have', down: 'Skip - keep the copy you have' }
    : { up: 'Download - queue this album', down: 'Skip - hide this album from my feed' }

// An album's up/down aren't verdicts, so they don't read as ones once taken: you queued a download or
// you passed on it. Only these two differ — a snoozed or blocked album means the same thing it does
// anywhere else — so this overlays the label map rather than replacing it.
const ALBUM_MARK_LABEL: Partial<Record<RowMark, string>> = {
  up: 'Queued',
  down: 'Skipped',
}
// Every mark spelled out, with snooze last as the fallback. The fallback is why this needs saying:
// `mark` is a union and this is a ternary chain, so a new mark without a branch here silently renders
// the *moon* — the reader would see a snooze icon labelled "Noted" and nothing would error.
const MarkIcon = ({ mark, isAlbum }: { mark: RowMark; isAlbum?: boolean }) =>
  mark === 'up' ? (isAlbum ? <IconDownload size={15} /> : <IconApprove size={15} />)
    : mark === 'down' ? (isAlbum ? <IconSkip size={15} /> : <IconReject size={15} />)
      : mark === 'indifferent' ? <IconIndifferent size={15} />
        : mark === 'merged' ? <IconCheck size={15} />
          : mark === 'blocked' ? <IconBlock size={15} />
            : <IconMoon size={15} />

// An in-place decision marker with an undo: "✓ Added · undo". Every decision (like / dislike /
// download / skip / snooze) is reversible from the feed so a misclick is one click to fix. A merge is
// the exception — it's an assertion about the library, not a verdict, and there's nothing to walk back
// in the feed once the album is known to be owned.
//
// Up and down mean different things either side of the artist/album line, so the icon and the label
// both follow: on an artist they're taste ("Added" / "Dismissed"), on an album they're an errand
// ("Queued" / "Skipped").
function DecisionMark({
  mark,
  onUndo,
  disabled,
  isAlbum = false,
}: {
  mark: RowMark
  onUndo: () => void
  disabled: boolean
  isAlbum?: boolean
}) {
  return (
    <span className={`disc-rated mark-${mark}${isAlbum ? ' is-album' : ''}`}>
      <span className="disc-rated-icon"><MarkIcon mark={mark} isAlbum={isAlbum} /></span>
      {(isAlbum && ALBUM_MARK_LABEL[mark]) || MARK_LABEL[mark]}
      {mark !== 'merged' && (
        <button className="disc-undo" title="Undo this decision" disabled={disabled} onClick={onUndo}>
          undo
        </button>
      )}
    </span>
  )
}

// Block an album for everyone. The down-chevron beside it is a personal "meh" — it hides the album
// from you alone and leaves it offerable to everyone else, which is what you want for a band you like
// but don't want the whole discography of. This is the other thing: a release nobody should be
// offered (a junk Deezer entry, a reissue of something owned). It gets a stop sign rather than a
// louder chevron so it reads as a different action, not a bigger thumbs-down, and sits muted at rest
// so the per-user pass stays the obvious default.
function BlockButton({ onBlock, disabled }: { onBlock: () => void; disabled: boolean }) {
  return (
    <button
      className="disc-btn block"
      title="Block for everyone — no one gets offered this album"
      disabled={disabled}
      onClick={onBlock}
    >
      <IconBlock size={15} />
    </button>
  )
}

// "Already in library?" on a missing-album row — the copy we own is filed under a near-miss title (or
// a different act), so the diff keeps offering it. Opens the merge pane before it downloads.
function MergeButton({ onMerge, disabled }: { onMerge: () => void; disabled: boolean }) {
  return (
    <button
      className="disc-btn"
      title="Already in library — match an album you own"
      disabled={disabled}
      onClick={onMerge}
    >
      <IconWrench size={15} />
    </button>
  )
}

const SNOOZE_OPTIONS: { label: string; duration: SnoozeDuration }[] = [
  { label: 'Week', duration: 'week' },
  { label: 'Month', duration: 'month' },
  { label: 'Year', duration: 'year' },
]
const SNOOZE_LABEL: Record<SnoozeDuration, string> = {
  week: 'a week',
  month: 'a month',
  year: 'a year',
}

// "Sticky snooze": remember the last duration the user picked so a quick click on the moon re-applies
// it without reopening the flyout. Persisted in localStorage and mirrored across every SnoozeControl
// on the page via a custom event (the native `storage` event only fires in *other* tabs).
const SNOOZE_PREF_KEY = 'mc.snooze.last'
const SNOOZE_PREF_EVENT = 'mc-snooze-changed'
const DEFAULT_SNOOZE: SnoozeDuration = 'week'

function readStickySnooze(): SnoozeDuration {
  try {
    const stored = localStorage.getItem(SNOOZE_PREF_KEY)
    if (stored && stored in SNOOZE_LABEL) return stored as SnoozeDuration
  } catch {
    // localStorage can throw (private mode / disabled storage) — fall back to the default.
  }
  return DEFAULT_SNOOZE
}

function useStickySnooze(): [SnoozeDuration, (duration: SnoozeDuration) => void] {
  const [duration, setDuration] = useState<SnoozeDuration>(readStickySnooze)
  useEffect(() => {
    const sync = () => setDuration(readStickySnooze())
    window.addEventListener(SNOOZE_PREF_EVENT, sync)
    window.addEventListener('storage', sync)
    return () => {
      window.removeEventListener(SNOOZE_PREF_EVENT, sync)
      window.removeEventListener('storage', sync)
    }
  }, [])
  const remember = (next: SnoozeDuration) => {
    try {
      localStorage.setItem(SNOOZE_PREF_KEY, next)
    } catch {
      // Ignore — we still update in-memory state so this session behaves correctly.
    }
    setDuration(next)
    window.dispatchEvent(new Event(SNOOZE_PREF_EVENT))
  }
  return [duration, remember]
}

// The 💤 snooze action. Collapsed to just the icon; hovering (or focusing) grows it rightward into a
// matching dark-glass square holding the three durations (Week / Month / Year). The panel floats over
// the row without resizing it or nudging the thumbs up/down beside it; the spans run a "frozen" scale —
// purple Week → icy Year (see data-duration styling in index.css).
//
// Clicking the moon itself re-applies the last-used duration ("sticky snooze") — the panel is only
// needed when you want a *different* span. Picking from the panel updates that remembered default.
//
// Floating UI drives the open/close: `safePolygon` keeps the panel open while the cursor travels the
// diagonal from the moon toward an option (the old rectangular keepalive collapsed on any corner-cut),
// and `flip`/`shift` re-anchor it so it never overflows the viewport (e.g. inside the mobile drawer).
function SnoozeControl({
  onPick,
  disabled,
}: {
  onPick: (duration: SnoozeDuration) => void
  disabled: boolean
}) {
  const [lastDuration, rememberDuration] = useStickySnooze()
  const [open, setOpen] = useState(false)

  const { refs, floatingStyles, context } = useFloating({
    open,
    onOpenChange: setOpen,
    // `right-start` aligns the panel's TOP edge to the moon's top (not centered) so the two halves of the
    // pill share top and bottom edges exactly, rather than relying on equal heights + rounding agreeing.
    placement: 'right-start',
    // Default (transform-based) positioning is kept ON PURPOSE: Floating UI rounds the translate to whole
    // device pixels, so the panel's 1px borders stay crisp. (Switching to top/left via `transform: false`
    // let the panel land on a fractional pixel, which anti-aliased the bottom border into looking absent.)
    // The grow-in uses `clip-path`, not `transform`, so there's no conflict with the positioning transform.
    // offset -1 laps the panel 1px ONTO the moon's right edge. The panel is opaque and stacks above the
    // moon, so that column is painted by the panel's own left-edge colour (identical to the moon's fill) —
    // covering the seam on ANY background. (offset 0 left a 1px see-through gap that showed through on the
    // lighter detail-pane panel, reading as two separate boxes.)
    middleware: [offset(-1), flip({ fallbackAxisSideDirection: 'start' }), shift({ padding: 8 })],
    whileElementsMounted: autoUpdate,
  })

  const hover = useHover(context, {
    enabled: !disabled,
    handleClose: safePolygon({ buffer: 1 }),
    delay: { open: 0, close: 90 },
  })
  const focus = useFocus(context, { enabled: !disabled })
  const dismiss = useDismiss(context)
  const role = useRole(context, { role: 'menu' })
  const { getReferenceProps, getFloatingProps } = useInteractions([hover, focus, dismiss, role])

  const pick = (duration: SnoozeDuration) => {
    rememberDuration(duration)
    onPick(duration)
    setOpen(false)
  }

  return (
    <span className="disc-snooze">
      <button
        ref={refs.setReference}
        type="button"
        className={`disc-btn snooze snooze-trigger${open ? ' is-open' : ''}`}
        data-placement={context.placement}
        disabled={disabled}
        aria-label={`Snooze for ${SNOOZE_LABEL[lastDuration]}`}
        onClick={() => pick(lastDuration)}
        {...getReferenceProps()}
      >
        <IconMoon size={18} />
      </button>
      <span
        ref={refs.setFloating}
        className={`disc-snooze-flyout${open ? ' is-open' : ''}`}
        style={floatingStyles}
        data-placement={context.placement}
        {...getFloatingProps()}
      >
        {SNOOZE_OPTIONS.map((o) => (
          <button
            key={o.duration}
            type="button"
            className={`disc-btn snooze${o.duration === lastDuration ? ' is-last' : ''}`}
            data-duration={o.duration}
            role="menuitemradio"
            aria-checked={o.duration === lastDuration}
            disabled={disabled || !open}
            tabIndex={open ? 0 : -1}
            onClick={() => pick(o.duration)}
          >
            {o.label}
          </button>
        ))}
      </span>
    </span>
  )
}

// Stable identity for a feed row, shared by render and the rate mutation so a rated row can be
// marked in place. Albums key on artist+album; artists on kind+name.
const rowKeyFor = (item: FeedItem) =>
  item.album ? `${item.artist.artistName}::${item.album}` : `${item.kind}:${item.artist.artistName}`

// The artwork URL a source supplied (artist photo or album art), resolving the Deezer fallback for
// library artists that carry no photo of their own. Cached and shared with the sample player; null
// when nothing is available (the caller draws a coloured initial instead). Accepts null so callers
// that must run hooks before an early return (e.g. the detail panel) can call it unconditionally.
function useArtUrl(item: FeedItem | null): string | null {
  const name = item?.artist.artistName ?? ''
  const isArtist = !!item && !item.album
  const { data: deezer } = useQuery({
    queryKey: ['deezer-play', name],
    queryFn: () => getDeezerPlayInfo(name),
    enabled: isArtist && !item!.imageUrl,
    staleTime: 60 * 60 * 1000,
  })
  if (!item) return null
  return item.imageUrl ?? (isArtist ? deezer?.imageUrl ?? null : null)
}

// The image a source supplied (artist photo or album art), or a coloured initial when missing.
// `hero` renders the large readout image: drop the inline size so CSS (.detail-hero) drives it.
function FeedAvatar({ item, size, hero }: { item: FeedItem; size: number; hero?: boolean }) {
  const label = item.album ?? item.artist.artistName
  const src = useArtUrl(item)
  if (src) {
    return <img className="disc-avatar" src={src} alt={label} width={hero ? undefined : size} height={hero ? undefined : size} />
  }
  return (
    <div
      className="disc-avatar disc-avatar-fallback"
      style={hero ? undefined : { width: size, height: size, fontSize: size / 2.5 }}
    >
      {label.charAt(0).toUpperCase()}
    </div>
  )
}

// "via boygenius, Snail Mail (+2)" — the frontier artists that recommended this candidate.
function Provenance({ sources }: { sources: string[] }) {
  if (sources.length === 0) return null
  const shown = sources.slice(0, 3).join(', ')
  const extra = sources.length > 3 ? ` (+${sources.length - 3})` : ''
  return (
    <span className="disc-provenance">
      via {shown}
      {extra}
    </span>
  )
}

// Why a second-guessing card is in the feed: nothing recommended it — the user's own Plex song ratings
// did, by contradicting the verdict they gave the band (too high for a thumbs-down, too low for a
// thumbs-up, either for a shrug). Stands in for the provenance line, and is shared by all four kinds
// because the readout is just the evidence: which way it cuts is the card's badge and blurb to say.
// The numbers come off the feed item (snapshotted by the sweep that flagged it), so no per-row fetch.
function ReconsiderWhy({ signal }: { signal: ReconsiderSignal | null }) {
  if (!signal) return null
  return (
    <span className="disc-provenance disc-why">
      you rated {signal.ratedCount} of {signal.trackCount} songs · {signal.average.toFixed(1)}★ avg
    </span>
  )
}

// One album under the inline panel below — themed from its cover via `--art-accent`, matching the
// feed rows / Download queue. Owns its accent so the per-row hook stays out of the parent's `.map`.
function SubAlbumRow({
  album,
  verdict,
  isOpen,
  canPlay,
  disabled,
  onToggle,
  onRate,
  onUndo,
  onMerge,
  onBlock,
}: {
  album: FeedItem
  verdict: RowMark | undefined
  isOpen: boolean
  canPlay: boolean
  disabled: boolean
  onToggle: () => void
  // AlbumVerdict, not Verdict: these rows are albums, and "indifferent" is an artist verdict the API
  // answers 400 for. Narrowing the callback is what makes wiring the third button in here a compile
  // error rather than a click that fails at runtime.
  onRate: (item: FeedItem, verdict: AlbumVerdict) => void
  onUndo: (item: FeedItem) => void
  onMerge: (item: FeedItem) => void
  onBlock: (item: FeedItem) => void
}) {
  const accent = useArtAccent(useArtUrl(album))
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  return (
    <div className="disc-sub-album-wrap">
      {/* The whole row toggles the preview; the action cluster stops the click so a thumb
          doesn't also open/close the player. */}
      <div
        className={`disc-sub-album${isOpen ? ' selected' : ''}${canPlay ? '' : ' no-play'}`}
        style={accentStyle}
        onClick={canPlay ? onToggle : undefined}
      >
        <FeedAvatar item={album} size={36} />
        <div className="disc-sub-album-name">
          {album.album}
          {album.year && <span className="album-year">{album.year}</span>}
        </div>
        <div className="disc-actions" onClick={(e) => e.stopPropagation()}>
          {verdict ? (
            <DecisionMark mark={verdict} disabled={disabled} isAlbum onUndo={() => onUndo(album)} />
          ) : (
            <>
              <button className="disc-btn download" title="Download — queue this album" disabled={disabled} onClick={() => onRate(album, 'up')}>
                <IconDownload />
              </button>
              <button className="disc-btn skip" title="Skip — hide this album from my feed" disabled={disabled} onClick={() => onRate(album, 'down')}>
                <IconSkip />
              </button>
              <MergeButton disabled={disabled} onMerge={() => onMerge(album)} />
              <BlockButton disabled={disabled} onBlock={() => onBlock(album)} />
            </>
          )}
        </div>
      </div>
      {isOpen && album.deezerAlbumId && <DeezerSample albumId={album.deezerAlbumId} />}
    </div>
  )
}

// Inline under a just-liked brand-new artist: their Deezer albums, each thumbable so a fresh
// discovery can actually be acquired (a liked album flows to the downloader). Reuses the parent's
// `rated`/play/rate plumbing so album rows mark in place exactly like top-level cards.
function ArtistAlbumsPanel({
  artist,
  rated,
  onRate,
  onUndo,
  onMerge,
  onBlock,
  disabled,
}: {
  artist: string
  rated: Map<string, RowMark>
  // AlbumVerdict, not Verdict: these rows are albums, and "indifferent" is an artist verdict the API
  // answers 400 for. Narrowing the callback is what makes wiring the third button in here a compile
  // error rather than a click that fails at runtime.
  onRate: (item: FeedItem, verdict: AlbumVerdict) => void
  onUndo: (item: FeedItem) => void
  onMerge: (item: FeedItem) => void
  onBlock: (item: FeedItem) => void
  disabled: boolean
}) {
  // Which album's Deezer preview is expanded — one at a time, like selecting a row in the left-hand
  // list. Clicking anywhere on a row toggles its player (and collapses whichever was open before).
  const [openAlbum, setOpenAlbum] = useState<string | null>(null)

  const { data, isPending, isError, error, isFetching, refetch } = useQuery({
    queryKey: ['artist-albums', artist],
    queryFn: () => getArtistAlbums(artist),
    staleTime: 5 * 60 * 1000,
  })

  // A live Deezer call (the artist's listing plus a lookup per release), so it can run for seconds on
  // an artist nothing has touched yet — show it working rather than a line that reads as an answer.
  if (isPending) {
    return (
      <div className="disc-sub-albums">
        <span className="disc-sub-busy"><Spinner /> Finding albums on Deezer…</span>
      </div>
    )
  }
  // "Couldn't ask" and "asked, nothing there" used to render as the same sentence, so a Deezer
  // rate-limit read as an artist with no albums — and stuck, because the client cached it.
  if (isError && !data) {
    return (
      <div className="disc-sub-albums">
        <span className="disc-sub-busy">
          {isFetching ? <Spinner /> : null}
          <em>
            {isDeezerBusy(error)
              ? 'Deezer is rate-limiting requests — the albums are still there.'
              : 'Couldn’t load albums.'}
          </em>
          {!isFetching && (
            <button className="disc-btn" onClick={() => refetch()}>Retry</button>
          )}
        </span>
      </div>
    )
  }
  if (data.length === 0) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">No albums found on Deezer.</em></div>
  }

  return (
    <div className="disc-sub-albums">
      {/* Keep the last loaded list up when a refresh fails, instead of swapping it for an error. */}
      {isError && (
        <span className="disc-sub-stale">
          {isFetching ? <Spinner size={12} /> : null}
          {isDeezerBusy(error) ? 'Deezer is busy — showing the last loaded list.' : 'Couldn’t refresh — showing the last loaded list.'}
        </span>
      )}
      {data.map((album) => {
        const rowKey = `${album.artist.artistName}::${album.album}`
        return (
          <SubAlbumRow
            key={rowKey}
            album={album}
            verdict={rated.get(rowKey)}
            isOpen={openAlbum === rowKey}
            canPlay={!!album.deezerAlbumId}
            disabled={disabled}
            onToggle={() => setOpenAlbum((cur) => (cur === rowKey ? null : rowKey))}
            onRate={onRate}
            onUndo={onUndo}
            onMerge={onMerge}
            onBlock={onBlock}
          />
        )
      })}
    </div>
  )
}

// The two owned-artist categories that ask "do you like this band?" without showing you what you own
// by them. Their readouts get a link into Browse focused on the artist, so the answer is one click
// away. The second-guessing kinds are left out: those cards carry their own evidence (the Plex song
// ratings that contradict the verdict), which is the thing to judge on there.
const BROWSE_LINK_KINDS = new Set<FeedKind>(['RecommendedLibraryArtist', 'SeedLibraryArtist'])

// Discover kinds whose artist is already in the library (owned), so a "open where it lives" link makes
// sense. RecommendedArtist (not yet owned) and MissingAlbum (an album, not the artist) are excluded.
const IN_LIBRARY_KINDS = new Set<FeedKind>([
  'RecommendedLibraryArtist', 'SeedLibraryArtist', 'LibraryArtist', 'ReconsiderArtist',
  // The two indifferent kinds belong here for a second reason beyond the library link: this set also
  // gates the Plex star readout, which is the very evidence these cards argue from. Leaving them out
  // would show "your ratings disagree with you" with the ratings hidden.
  'SecondThoughtsArtist', 'IndifferentLikeArtist', 'IndifferentDislikeArtist',
])

// Deep links to open an owned artist where it lives (Plex now, Navidrome later) — the same per-library
// links as the Artists-page "Library" tab, shown compactly in the readout for in-library Discover cards.
function LibraryLinks({ artist }: { artist: string }) {
  const { data } = useQuery({
    queryKey: ['artist-libraries', artist],
    queryFn: () => getArtistLibraries(artist),
    staleTime: 5 * 60 * 1000,
  })

  const links = (data?.sources ?? []).filter((s) => s.present).flatMap((s) => s.links)
  if (links.length === 0) return null

  return (
    <>
      <div className="detail-section-label">In your library</div>
      <div className="detail-library-links">
        {links.map((l) => (
          <a className="deezer-link" key={l.url} href={l.url} target="_blank" rel="noopener noreferrer">
            {l.label} ↗
          </a>
        ))}
      </div>
    </>
  )
}

// The right-hand readout (desktop) / bottom drawer (mobile) for the row currently selected in the
// list. A big hero image, recommendation chips, a Deezer preview player, and — for a brand-new
// recommended artist — their grabbable albums. All the rate/snooze/undo plumbing is threaded in from
// the parent so a decision made here marks the matching list row in place too.
function DetailPanel({
  item,
  rated,
  busy,
  onRate,
  onSnooze,
  onUndo,
  onMerge,
  onBlock,
  onClose,
}: {
  item: FeedItem | null
  rated: Map<string, RowMark>
  busy: boolean
  onRate: (item: FeedItem, verdict: Verdict) => void
  onSnooze: (item: FeedItem, duration: SnoozeDuration) => void
  onUndo: (item: FeedItem) => void
  onMerge: (item: FeedItem) => void
  onBlock: (item: FeedItem) => void
  onClose: () => void
}) {
  // Resolve artwork + accent unconditionally (hooks must run before the empty-state early return) so
  // the focused card's hero halo glows in its own colour. `useArtUrl` tolerates a null item.
  const accent = useArtAccent(useArtUrl(item))
  if (!item) {
    return (
      <aside className="disc-detail is-empty">
        <div className="disc-detail-empty">
          <span className="detail-empty-icon">🎧</span>
        </div>
      </aside>
    )
  }

  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  const name = item.artist.artistName
  const isAlbum = !!item.album
  const rowKey = rowKeyFor(item)
  const verdict = rated.get(rowKey)
  const canPlay = isAlbum ? !!item.deezerAlbumId : true

  return (
    <aside className="disc-detail" style={accentStyle}>
      <button className="detail-close" title="Close" onClick={onClose}>✕</button>

      {/* Header: art aligned left with the badge / title / chips / actions stacked to its right, so the
          square no longer floats alone in a sea of empty space. Tracks + albums stay full-width below. */}
      <div className="detail-header">
        <div className="detail-hero">
          <FeedAvatar item={item} size={0} hero />
        </div>

        <div className="detail-headinfo">
          <span className={`feed-badge feed-badge-${item.kind}`}>{BADGE[item.kind]}</span>
          <h2 className="detail-name">{item.album ?? name}</h2>

          {isAlbum ? (
            <>
              <div className="detail-chips">
                <span className="detail-chip">Album</span>
                <span className="detail-chip via">{name}</span>
                {item.year && <span className="detail-chip">{item.year}</span>}
                {/* An upgrade card is offering to replace a record the user already has, so say what
                    they have — otherwise it reads like a gap and the thumbs are ambiguous. */}
                {item.kind === 'UpgradeAlbum' && item.ownedQuality && (
                  <span className="detail-chip upgrade-chip">
                    You have {QUALITY_LABEL[item.ownedQuality]}
                  </span>
                )}
              </div>
              {/* Jump to this artist in the library (the Artists tab), filtered + opened to them —
                  handy on a missing-album card to see what else of theirs you already own. */}
              <Link className="deezer-link detail-goartist" to={`/browse?artist=${encodeURIComponent(name)}`}>
                Go to artist ↗
              </Link>
            </>
          ) : item.kind === 'ReconsiderArtist' ? (
            // Nothing recommended this one — the user's own song ratings contradict the thumbs-down
            // they gave it. Spell out the second thumbs-down, since that's what makes it permanent.
            <>
              <div className="detail-section-label">Why this is back</div>
              <p className="detail-why">
                You thumbed this band down, but your song ratings say otherwise. Thumb it down again to
                settle it for good — it won't come back a third time.
              </p>
            </>
          ) : item.kind === 'SecondThoughtsArtist' ? (
            // The mirror: a thumbs-up the song ratings argue against. Worth saying that the like is
            // still growing the queue, since that's the cost of leaving it in place.
            <>
              <div className="detail-section-label">Why this is back</div>
              <p className="detail-why">
                You thumbed this band up, but your song ratings are poor — and the like is still seeding
                recommendations. Thumb it down to drop it, or up again to settle it for good.
              </p>
            </>
          ) : item.kind === 'IndifferentLikeArtist' ? (
            // A shrug the ratings argue up. The copy leans on "you've heard it since", because that is
            // the actual difference between now and when they shrugged — indifference deliberately
            // leaves a band in the Deep Frontier rotation, which is how they came to rate it.
            <>
              <div className="detail-section-label">Why this is back</div>
              <p className="detail-why">
                You had no strong feelings about this band, but you've been rating its songs highly
                since. Thumb it up to start growing recommendations from it, or shrug again to settle it
                for good.
              </p>
            </>
          ) : item.kind === 'IndifferentDislikeArtist' ? (
            // The other side of the same verdict. Lower stakes — an indifferent band feeds nothing — so
            // the copy says what a rejection would actually buy: it leaves the rotation.
            <>
              <div className="detail-section-label">Why this is back</div>
              <p className="detail-why">
                You had no strong feelings about this band, but you've been rating its songs poorly.
                Thumb it down to drop it out of your playlists, or shrug again to settle it for good.
              </p>
            </>
          ) : item.sources.length > 0 ? (
            <>
              <div className="detail-section-label">Recommended via</div>
              <div className="detail-chips">
                {/* Each source is the library artist this recommendation stems from — clicking one
                    opens their Browse page in a fresh tab, so the feed (and the current preview)
                    stays exactly where it is. A plain anchor rather than a router Link: target
                    "_blank" needs a real navigation. */}
                {item.sources.slice(0, 8).map((s) => (
                  <a
                    className="detail-chip via"
                    key={s}
                    href={`/browse?artist=${encodeURIComponent(s)}`}
                    target="_blank"
                    rel="noopener noreferrer"
                    title={`Open ${s} in a new tab`}
                  >
                    {s} ↗
                  </a>
                ))}
              </div>
            </>
          ) : null}

          {/* Owned artists you're being asked to rate: jump to them in Browse to see what of theirs
              the library actually holds before thumbing. A new tab (plain anchor — target "_blank"
              needs a real navigation) so the feed and the running preview stay put, same as the
              "Recommended via" chips above. */}
          {BROWSE_LINK_KINDS.has(item.kind) && (
            <a
              className="deezer-link detail-goartist"
              href={`/browse?artist=${encodeURIComponent(name)}`}
              target="_blank"
              rel="noopener noreferrer"
              title={`Open ${name} in Browse in a new tab`}
            >
              Go to artist ↗
            </a>
          )}

          <div className="detail-actions">
            {verdict ? (
              <DecisionMark mark={verdict} disabled={busy} isAlbum={isAlbum} onUndo={() => onUndo(item)} />
            ) : (
              <>
                <button
                  className={isAlbum ? 'disc-btn download' : 'disc-btn up'}
                  title={isAlbum ? albumActionTitle(item.kind).up : ARTIST_ACTION_TITLE.up}
                  onClick={() => onRate(item, 'up')}
                >
                  {isAlbum ? <IconDownload /> : <IconApprove />}
                </button>
                {/* Artists only: an album already has this verdict twice over — its skip *is*
                    "meh, hide this from my feed", and an upgrade's is "keep the copy I have". The API
                    refuses "indifferent" on an album, and AlbumVerdict keeps that from compiling. */}
                {!isAlbum && (
                  <button
                    className="disc-btn indifferent"
                    title={ARTIST_ACTION_TITLE.indifferent}
                    onClick={() => onRate(item, 'indifferent')}
                  >
                    <IconIndifferent />
                  </button>
                )}
                <button
                  className={isAlbum ? 'disc-btn skip' : 'disc-btn down'}
                  title={isAlbum ? albumActionTitle(item.kind).down : ARTIST_ACTION_TITLE.down}
                  onClick={() => onRate(item, 'down')}
                >
                  {isAlbum ? <IconSkip /> : <IconReject />}
                </button>
                <SnoozeControl onPick={(duration) => onSnooze(item, duration)} disabled={false} />
                {isAlbum && <MergeButton disabled={busy} onMerge={() => onMerge(item)} />}
                {isAlbum && <BlockButton disabled={busy} onBlock={() => onBlock(item)} />}
              </>
            )}
          </div>

          {/* Open the owned artist where it lives (Plex / Navidrome), just under the rate buttons.
              On a missing-album card the artist is owned too, so offer the same link there. */}
          {(isAlbum || IN_LIBRARY_KINDS.has(item.kind)) && <LibraryLinks artist={name} />}
        </div>

        {/* The user's song ratings for this artist, pinned to the right of the art. Owned artists
            only — Plex has no songs (so no ratings) for a not-yet-owned recommendation or an album. */}
        {!isAlbum && IN_LIBRARY_KINDS.has(item.kind) && <PlexRatingStats artist={name} />}
      </div>

      {canPlay && (
        <>
          {/* DeezerSample renders its own "Album tracks" / "Top tracks" header (with the Deezer link),
              so no separate detail-section-label here — that produced a duplicate heading. */}
          {/* Key by row so switching selection remounts the player (stops the previous preview). */}
          {isAlbum ? (
            <DeezerSample key={rowKey} albumId={item.deezerAlbumId!} />
          ) : (
            <DeezerSample key={rowKey} artist={name} />
          )}
        </>
      )}

      {/* A brand-new recommended artist: show their acquirable albums so a find can be grabbed. */}
      {item.kind === 'RecommendedArtist' && (
        <>
          <div className="detail-section-label">Albums</div>
          <ArtistAlbumsPanel
            artist={name}
            rated={rated}
            onRate={onRate}
            onUndo={onUndo}
            onMerge={onMerge}
            onBlock={onBlock}
            disabled={false}
          />
        </>
      )}
    </aside>
  )
}

// A single feed row. Extracted into its own component so it can resolve the card's artwork and derive
// an art-driven accent colour per row (calling those hooks inside the parent's `.map` would break the
// rules of hooks). The accent is published as the `--art-accent` CSS variable so the row + its avatar
// theme themselves from the art — a soft border/glow tint over the dark base (see index.css).
function DiscRow({
  item,
  selected,
  verdict,
  busy,
  onSelect,
  onRate,
  onSnooze,
  onUndo,
  onMerge,
  onBlock,
}: {
  item: FeedItem
  selected: boolean
  verdict: RowMark | undefined
  busy: boolean
  onSelect: (item: FeedItem) => void
  onRate: (item: FeedItem, verdict: Verdict) => void
  onSnooze: (item: FeedItem, duration: SnoozeDuration) => void
  onUndo: (item: FeedItem) => void
  onMerge: (item: FeedItem) => void
  onBlock: (item: FeedItem) => void
}) {
  const name = item.artist.artistName
  const isAlbum = !!item.album
  const accent = useArtAccent(useArtUrl(item))
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  return (
    <div className={verdict ? 'disc-row-wrap rated' : 'disc-row-wrap'}>
      {/* The whole row opens the readout; the action cluster stops the click so a
          thumb/snooze doesn't also yank the panel open. */}
      <div
        className={selected ? 'disc-row selected' : 'disc-row'}
        style={accentStyle}
        onClick={() => onSelect(item)}
      >
        <FeedAvatar item={item} size={56} />
        <div className="disc-row-main">
          <span className={`feed-badge feed-badge-${item.kind}`}>{BADGE[item.kind]}</span>
          <div className="disc-name">{item.album ?? name}</div>
          {isAlbum ? (
            // "Björk · 1997" — the artist the album belongs to, dated so a recommendation reads as a
            // new record or a back-catalogue gap at a glance.
            <span className="disc-provenance">
              {name}
              {item.year && <> · {item.year}</>}
            </span>
          ) : item.kind === 'ReconsiderArtist' || item.kind === 'SecondThoughtsArtist'
            || item.kind === 'IndifferentLikeArtist' || item.kind === 'IndifferentDislikeArtist' ? (
            // The readout is direction-neutral — it just shows the stars behind the flag — so all four
            // second-guessing kinds share it.
            <ReconsiderWhy signal={item.reconsider} />
          ) : (
            <Provenance sources={item.sources} />
          )}
        </div>
        <div className="disc-actions" onClick={(e) => e.stopPropagation()}>
          {verdict ? (
            <DecisionMark mark={verdict} disabled={busy} isAlbum={isAlbum} onUndo={() => onUndo(item)} />
          ) : (
            <>
              <button
                className={isAlbum ? 'disc-btn download' : 'disc-btn up'}
                title={isAlbum ? albumActionTitle(item.kind).up : ARTIST_ACTION_TITLE.up}
                onClick={() => onRate(item, 'up')}
              >
                {isAlbum ? <IconDownload /> : <IconApprove />}
              </button>
              {!isAlbum && (
                <button
                  className="disc-btn indifferent"
                  title={ARTIST_ACTION_TITLE.indifferent}
                  onClick={() => onRate(item, 'indifferent')}
                >
                  <IconIndifferent />
                </button>
              )}
              <button
                className={isAlbum ? 'disc-btn skip' : 'disc-btn down'}
                title={isAlbum ? albumActionTitle(item.kind).down : ARTIST_ACTION_TITLE.down}
                onClick={() => onRate(item, 'down')}
              >
                {isAlbum ? <IconSkip /> : <IconReject />}
              </button>
              {/* Snooze hides a "not now" pick for a while — works for artists and missing albums. */}
              <SnoozeControl
                onPick={(duration) => onSnooze(item, duration)}
                disabled={false}
              />
              {isAlbum && <MergeButton disabled={busy} onMerge={() => onMerge(item)} />}
              {isAlbum && <BlockButton disabled={busy} onBlock={() => onBlock(item)} />}
            </>
          )}
        </div>
      </div>
    </div>
  )
}

// View state kept at module scope so navigating away from /discover and back restores the same feed
// instead of remounting fresh — which regenerated `seed` (reshuffling the whole list) and dropped the
// rated marks and a just-approved artist's inline albums. The QueryClient already caches the feed
// data across navigation; this keeps the local view in sync with it. Lives for the browser session
// (resets on full reload), which is the right scope for a randomized, react-to-it-now feed.
type DiscoverState = {
  shown: Set<FeedKind>
  page: number
  seed: number
  rated: Map<string, RowMark>
  // The feed row open in the readout panel (desktop) / bottom drawer (mobile).
  selected: FeedItem | null
}
const persisted: DiscoverState = {
  shown: readShownKinds(),
  page: 0,
  seed: newSeed(),
  rated: new Map<string, RowMark>(),
  selected: null,
}

export default function Discover() {
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [shown, setShown] = useState<Set<FeedKind>>(() => persisted.shown)
  const [page, setPage] = useState(() => persisted.page)
  const [seed, setSeed] = useState(() => persisted.seed)
  // Rows rated this view, by row key -> verdict. They stay in place (marked, not removed) until the
  // next natural refresh, so a 👍/👎 doesn't reflow the whole list out from under you.
  const [rated, setRated] = useState<Map<string, RowMark>>(() => persisted.rated)
  // The row whose readout is open on the right (desktop) / in the drawer (mobile). Liking a brand-new
  // recommended artist auto-selects it so its grabbable albums surface in the panel.
  const [selected, setSelected] = useState<FeedItem | null>(() => persisted.selected)
  // The album row whose "Already in library?" pane is open, if any. Deliberately not persisted — a
  // modal shouldn't reopen itself when you navigate back to the feed.
  const [merging, setMerging] = useState<FeedItem | null>(null)

  // Mirror the live view state back into the module store every render so a later remount restores it.
  useEffect(() => {
    persisted.shown = shown
    persisted.page = page
    persisted.seed = seed
    persisted.rated = rated
    persisted.selected = selected
  })

  // Keep a stable, sorted kinds list so the query key (and the server's interleave) are deterministic.
  const kinds = ALL_KINDS.filter((k) => shown.has(k))

  // A natural refresh (page, shuffle, or category change refetches the feed) clears the in-place
  // marks so the freshly-fetched list — which already excludes the rated items — starts clean.
  // The guard is value-based, not run-count-based: it clears only when the page/seed/kinds actually
  // change from what's currently shown. On a remount we restore the persisted state for this same
  // page/seed, so the key matches and nothing clears — and it stays correct under StrictMode, which
  // double-invokes the mount effect (a run-count "skip first mount" guard would clear on the 2nd run).
  const kindsKey = kinds.join(',')
  const lastRefreshKey = useRef(`${page}|${seed}|${kindsKey}`)
  useEffect(() => {
    const key = `${page}|${seed}|${kindsKey}`
    if (lastRefreshKey.current === key) return
    lastRefreshKey.current = key
    setRated(new Map())
    // The selected item has almost certainly left the freshly-fetched feed — close the readout.
    setSelected(null)
  }, [page, seed, kindsKey])

  // On mobile the readout takes over the screen; lock the background list so it can't scroll
  // (or peek through the translucent top bar) behind it. CSS scopes the lock to the mobile breakpoint.
  useEffect(() => {
    document.body.classList.toggle('detail-open', selected != null)
    return () => document.body.classList.remove('detail-open')
  }, [selected])

  const toggleCategory = (kinds: FeedKind[]) => {
    setShown((prev) => {
      const next = new Set(prev)
      const on = kinds.some((k) => next.has(k))
      for (const k of kinds) on ? next.delete(k) : next.add(k)
      writeShownKinds(next)
      return next
    })
    setPage(0)
  }

  const { data, isPending, isError, error, isFetching } = useQuery({
    queryKey: ['feed', 'mixed', kinds.join(','), page, seed],
    queryFn: () => getMixedFeed(kinds, page, PAGE_SIZE, seed),
    enabled: !!user && kinds.length > 0,
    placeholderData: keepPreviousData,
    // Freeze the feed for the session. Without this it defaults to stale-immediately and refetches on
    // every remount (i.e. navigating away and back) — and because the server drops just-rated artists
    // from the feed, that refetch made an approved artist's card (and its inline albums) disappear.
    // A new seed/page is a different query key, so Shuffle and paging still fetch fresh; a full page
    // reload starts a new session with a new seed. (The queue rebuild now lives in the dev panel.)
    staleTime: Infinity,
    gcTime: 60 * 60 * 1000,
  })

  const rateMutation = useMutation({
    mutationFn: ({ item, verdict }: { item: FeedItem; verdict: Verdict }) => rate(item, verdict),
    // Mark the row in place immediately and leave the feed query alone — invalidating it here is
    // what made the list re-interleave and jump. The mark drops away on the next natural refresh.
    onMutate: ({ item, verdict }) => {
      setRated((prev) => new Map(prev).set(rowKeyFor(item), verdict))
      // Spore + mycelium feedback at the cursor (shared with the artist page).
      rateFeedback(verdict)
    },
    onError: (_err, { item }) => {
      setRated((prev) => {
        const next = new Map(prev)
        next.delete(rowKeyFor(item))
        return next
      })
    },
    onSuccess: (_data, { item, verdict }) => {
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      // Liking a brand-new artist opens its readout so the albums-to-grab panel surfaces.
      if (verdict === 'up' && item.kind === 'RecommendedArtist') {
        setSelected(item)
      }
    },
  })
  const snoozeMutation = useMutation({
    mutationFn: ({ item, duration }: { item: FeedItem; duration: SnoozeDuration }) => snooze(item, duration),
    // Mark in place like a rating; snooze writes a decided row so the artist drops out of the feed on
    // the next natural refresh. Doesn't touch the buy list (a snooze isn't a "yes").
    onMutate: ({ item }) => {
      setRated((prev) => new Map(prev).set(rowKeyFor(item), 'snoozed'))
    },
    onError: (_err, { item }) => {
      setRated((prev) => {
        const next = new Map(prev)
        next.delete(rowKeyFor(item))
        return next
      })
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
    },
  })
  // Blocking an album takes it off *everyone's* feed. Marked in place like any other decision, and
  // undo lifts the block again — so it writes straight through, no confirmation.
  const blockMutation = useMutation({
    mutationFn: (item: FeedItem) => blockAlbum(item.artist.artistName, item.album!),
    onMutate: (item) => {
      setRated((prev) => new Map(prev).set(rowKeyFor(item), 'blocked'))
    },
    onError: (_err, item) => {
      setRated((prev) => {
        const next = new Map(prev)
        next.delete(rowKeyFor(item))
        return next
      })
    },
    onSuccess: () => {
      // The album is gone from every album surface now; drop the cached discographies so a later
      // visit to Browse reflects the block.
      queryClient.invalidateQueries({ queryKey: ['artist-discography'] })
      queryClient.invalidateQueries({ queryKey: ['artist-albums'] })
    },
  })
  // The inline album decisions made under a just-liked brand-new artist (from its expanded panel),
  // read from the react-query cache. Used so undoing the artist also walks back those album picks.
  const decidedAlbumsFor = (artistName: string): FeedItem[] => {
    const albums = queryClient.getQueryData<FeedItem[]>(['artist-albums', artistName]) ?? []
    return albums.filter((a) => rated.has(rowKeyFor(a)))
  }

  // Undo any decision (like / meh / snooze / block, artist or album), clearing it back to actionable.
  // Optimistically drops the in-place mark so the card's 👍/👎/💤 reappear instantly; rolls back on
  // failure. Undoing a recommended artist also clears the album decisions made in its readout panel —
  // you went back on the artist, so its album picks shouldn't linger.
  const undo = useMutation({
    mutationFn: async (item: FeedItem) => {
      // A block isn't a verdict — there's no rating to clear, so undo means lifting it globally.
      if (rated.get(rowKeyFor(item)) === 'blocked') {
        await unblockAlbum(item.artist.artistName, item.album!)
        return
      }
      await clearRating(item)
      if (item.kind === 'RecommendedArtist') {
        await Promise.all(decidedAlbumsFor(item.artist.artistName).map((a) => clearRating(a)))
      }
    },
    onMutate: (item) => {
      const prev = new Map(rated)
      const next = new Map(rated)
      next.delete(rowKeyFor(item))
      if (item.kind === 'RecommendedArtist') {
        decidedAlbumsFor(item.artist.artistName).forEach((a) => next.delete(rowKeyFor(a)))
      }
      setRated(next)
      return { prev }
    },
    onError: (_err, _item, ctx) => {
      if (ctx?.prev) setRated(ctx.prev)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })
  const shuffle = () => {
    setSeed(newSeed())
    setPage(0)
  }

  const busy = rateMutation.isPending || snoozeMutation.isPending || undo.isPending || blockMutation.isPending
  const items = data?.items ?? []
  const total = data?.total ?? 0

  // Open the first row by default once the feed is populated, so the readout shows a recommendation
  // instead of the empty headphones placeholder — that placeholder is only meaningful when there's
  // literally nothing to show. Desktop only: on mobile the readout is a drawer that slides over the
  // list, so we leave it closed until the user actually taps a row. Re-runs after a natural refresh
  // (which clears the selection) to land on the new first item, but never overrides a live selection.
  useEffect(() => {
    if (items.length === 0) return
    if (selected && items.some((it) => rowKeyFor(it) === rowKeyFor(selected))) return
    if (typeof window !== 'undefined' && !window.matchMedia('(min-width: 961px)').matches) return
    setSelected(items[0])
  }, [items, selected])
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))
  // Identity of the row open in the readout, so the matching list row renders as selected.
  const selectedKey = selected ? rowKeyFor(selected) : null

  if (!user) {
    return (
      <section>
        <h1>Discover</h1>
        <p><em>Log in to build your personal recommendation feed.</em></p>
      </section>
    )
  }

  return (
    <section>
      <div className="disc-header">
        <h1>Discover</h1>
        <button className="disc-rebuild" onClick={shuffle} disabled={busy || isFetching}>
          ⤮ Shuffle
        </button>
      </div>

      {/* The category tags double as the filter: click a chip to show/hide that kind in the feed. */}
      <div className="feed-filters">
        {FILTER_CHIPS.map(({ kinds: chipKinds, label, tip }) => {
          const on = chipKinds.some((k) => shown.has(k))
          // A grouped chip wears the hue of its first kind — the one whose badge name it borrows.
          const hue = chipKinds[0]
          return (
            <button
              key={hue}
              type="button"
              title={tip}
              aria-pressed={on}
              className={`feed-chip feed-badge feed-badge-${hue}${on ? '' : ' off'}`}
              onClick={() => toggleCategory(chipKinds)}
            >
              {label}
            </button>
          )
        })}
      </div>

      {rateMutation.isError && <p className="error">Rating failed: {(rateMutation.error as Error).message}</p>}
      {snoozeMutation.isError && <p className="error">Snooze failed: {(snoozeMutation.error as Error).message}</p>}
      {blockMutation.isError && <p className="error">Block failed: {(blockMutation.error as Error).message}</p>}
      {isError && <p className="error">Failed to load feed: {(error as Error).message}</p>}

      {kinds.length === 0 && (
        <p><em>Pick at least one category above to see things to react to.</em></p>
      )}

      {kinds.length > 0 && isPending && <p><em>Loading…</em></p>}

      {kinds.length > 0 && !isPending && total === 0 && (
        <p>
          <em>
            Nothing in the feed. Thumb some bands on the <Link to="/browse">Browse</Link> page to
            seed recommendations, or check more categories above.
          </em>
        </p>
      )}

      {items.length > 0 && (
        <div className="disc-layout">
          <div className="disc-main">
            <div className={isFetching ? 'disc-list fetching' : 'disc-list'}>
              {items.map((item) => {
                const name = item.artist.artistName
                const isAlbum = !!item.album
                const rowKey = isAlbum ? `${name}::${item.album}` : `${item.kind}:${name}`
                return (
                  <DiscRow
                    key={rowKey}
                    item={item}
                    selected={selectedKey === rowKey}
                    verdict={rated.get(rowKey)}
                    busy={busy}
                    onSelect={setSelected}
                    onRate={(it, verdict) => rateMutation.mutate({ item: it, verdict })}
                    onSnooze={(it, duration) => snoozeMutation.mutate({ item: it, duration })}
                    onUndo={(it) => undo.mutate(it)}
                    onMerge={setMerging}
                    onBlock={(item) => blockMutation.mutate(item)}
                  />
                )
              })}
            </div>

            {pageCount > 1 && (
              <div className="disc-pager">
                <button disabled={page === 0 || isFetching} onClick={() => setPage((p) => p - 1)}>
                  ‹ prev
                </button>
                <span>
                  page {page + 1} / {pageCount}
                </span>
                <button disabled={page >= pageCount - 1 || isFetching} onClick={() => setPage((p) => p + 1)}>
                  next ›
                </button>
              </div>
            )}
          </div>

          <DetailPanel
            item={selected}
            rated={rated}
            busy={busy}
            onRate={(item, verdict) => rateMutation.mutate({ item, verdict })}
            onSnooze={(item, duration) => snoozeMutation.mutate({ item, duration })}
            onUndo={(item) => undo.mutate(item)}
            onMerge={setMerging}
            onBlock={(item) => blockMutation.mutate(item)}
            onClose={() => setSelected(null)}
          />
        </div>
      )}

      {/* Merging marks the row in place ("✓ In library") like a rating does, rather than invalidating
          the feed — the list is frozen for the session on purpose, and a reshuffle here would move
          every other card out from under you. The album is gone from the server's missing set, so the
          next natural refresh drops it. */}
      {merging?.album && (
        <MergeAlbumPane
          artist={merging.artist.artistName}
          album={merging.album}
          onClose={() => setMerging(null)}
          onMerged={() => {
            setRated((prev) => new Map(prev).set(rowKeyFor(merging), 'merged'))
            setMerging(null)
            queryClient.invalidateQueries({ queryKey: ['purchases'] })
            queryClient.invalidateQueries({ queryKey: ['artist-discography'] })
          }}
        />
      )}
    </section>
  )
}
