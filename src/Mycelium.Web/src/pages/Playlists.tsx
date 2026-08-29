import { useMemo, useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthContext'
import { usePlexLink } from '../auth/usePlexLink'
import { Spinner } from '../components/icons'
import {
  createStockPlaylist,
  FRESH_WINDOWS,
  getStockPlaylists,
  setRatingScale,
  updateStockPlaylist,
  type PlaylistSurvey,
  type StockPlaylist,
} from '../api/playlists'

// Ready-made smart playlists. The point is that someone can get a working set without learning Plex's
// filter editor — and, once they see the shape of it, go build their own in Plex directly.
//
// Everything here acts as the user's *own* linked Plex account rather than the server's, because
// playlists, star ratings and play history are all per-account in Plex. Built with the server token,
// a "4 stars and up" playlist would land in the owner's sidebar and be filtered by the owner's ratings
// — for everyone.
export default function Playlists() {
  const { user, isLoading } = useAuth()

  if (isLoading) {
    return (
      <section>
        <h1>Playlists</h1>
        <p><em>…</em></p>
      </section>
    )
  }

  if (!user) {
    return (
      <section>
        <h1>Playlists</h1>
        <p><em>Log in to set up playlists.</em></p>
      </section>
    )
  }

  return (
    <section>
      <h1>Playlists</h1>
      <PlexGate />
    </section>
  )
}

// ---- The gate: a linked Plex account -----------------------------------------------------------

// Nothing on this page can be built without one, because playlists, star ratings and play history are
// all per-account in Plex — built with the server's own token, a "4 stars and up" playlist would land
// in the owner's sidebar and be filtered by the owner's ratings, for everyone.
//
// Connecting is not offered here: that lives in the account menu in the top right, on every page.
// This only says when it's missing, so the page isn't a mystery.
function PlexGate() {
  const plex = usePlexLink()

  if (plex.isLoading) {
    return (
      <div className="dev-tool playlist-account">
        <span className="disc-sub-busy"><Spinner /> Checking your Plex connection…</span>
      </div>
    )
  }

  if (plex.error) {
    return (
      <div className="dev-tool">
        <p className="error">{plex.error.message}</p>
      </div>
    )
  }

  if (!plex.status?.linked) {
    return (
      <div className="dev-tool">
        <h2>Connect Plex first</h2>
        <p>
          These playlists are built in your own Plex account, so it has to be connected before there
          is anywhere to put them. Use <strong>Log into Plex</strong> in the account menu, top right.
        </p>
      </div>
    )
  }

  return <StockPlaylists />
}

// ---- The stock playlists ----------------------------------------------------------------------

// Both "matched" states name a playlist that exists in Plex, so the badge opens it. Without a link
// — an unreachable server can't be asked for its id — the same text renders as plain text rather
// than as an anchor that goes nowhere.
function BadgeLink({
  tone,
  href,
  children,
}: {
  tone: string
  href: string | null
  children: ReactNode
}) {
  const className = `playlist-badge ${tone}`
  if (!href) return <span className={className}>{children}</span>
  return (
    <a className={`${className} is-link`} href={href} target="_blank" rel="noreferrer">
      {children}
    </a>
  )
}

function StatusBadge({ playlist }: { playlist: StockPlaylist }) {
  switch (playlist.state) {
    case 'Exists': {
      // The name is only worth showing when it isn't the one we'd have used — that's the case where
      // "you already have this" would otherwise look wrong.
      const renamed =
        playlist.matchedTitle && playlist.matchedTitle !== playlist.title
          ? ` as “${playlist.matchedTitle}”`
          : ''
      return (
        <BadgeLink tone="is-exists" href={playlist.plexUrl}>
          ✓ In Plex{renamed}
        </BadgeLink>
      )
    }
    case 'Differs':
      return (
        <BadgeLink tone="is-differs" href={playlist.plexUrl}>
          Name taken
        </BadgeLink>
      )
    case 'Unavailable':
      return <span className="playlist-badge is-unavailable">Not yet</span>
    default:
      return <span className="playlist-badge is-missing">Not created</span>
  }
}

// The starter block, in the order it reads on the page: the three tag-driven playlists, then the
// ready-made "put something on now" trio. Their ids carry the one-month window they are pinned to,
// because the picker below can be set to the same window and two definitions can't share an id.
const STARTERS = [
  'my-library',
  'frontier',
  'frontier-deep',
  'stars-3-fresh-1mo',
  'stars-4-fresh-1mo',
  'stars-5-fresh-1mo',
]

// Every survey in the cache — there is one per fresh window the user has looked at, and a write
// affects all of them.
const SURVEYS = { queryKey: ['stock-playlists'] } as const

// A survey read costs one Plex request per playlist the user owns, so it is the slow thing on this
// page and must never sit between an action and its result. Both writers below patch the cache with
// what the server already told them and let the refetch land whenever it lands.
function usePatchSurvey() {
  const queryClient = useQueryClient()

  return (patch: (survey: PlaylistSurvey) => PlaylistSurvey) => {
    queryClient.setQueriesData<PlaylistSurvey>(SURVEYS, (survey) =>
      survey ? patch(survey) : survey,
    )
    // Reconciles anything the patch couldn't know — in the background, deliberately not awaited:
    // returning this promise from onSuccess would keep the mutation "pending" for the whole refetch,
    // which is what made the controls feel stuck.
    void queryClient.invalidateQueries(SURVEYS)
  }
}

function PlaylistRow({ playlist, freshMonths }: { playlist: StockPlaylist; freshMonths: number }) {
  const patchSurvey = usePatchSurvey()

  // The server answers with the row's new state, so it goes straight into the cache: the badge flips
  // the moment Plex confirms, without waiting to re-read every playlist on the server.
  const applyResult = (updated: StockPlaylist) =>
    patchSurvey((survey) => ({
      ...survey,
      playlists: survey.playlists.map((p) => (p.id === updated.id ? updated : p)),
    }))

  const create = useMutation({
    mutationFn: () => createStockPlaylist(playlist.id, freshMonths),
    onSuccess: applyResult,
  })
  const update = useMutation({
    mutationFn: () => updateStockPlaylist(playlist.id, freshMonths),
    onSuccess: applyResult,
  })
  const busy = create.isPending || update.isPending
  const error = (create.error ?? update.error) as Error | undefined

  return (
    <div className="playlist-row">
      {/* Only the starter rows have one. Decorative — the title beside it already names the
          playlist, so an alt would just say the same thing twice to a screen reader. */}
      {playlist.artUrl && (
        <img className="playlist-art" src={playlist.artUrl} alt="" loading="lazy" />
      )}

      <div className="playlist-row-body">
        <div className="playlist-row-main">
          <div className="playlist-text">
            <div className="playlist-title">{playlist.title}</div>
            <div className="playlist-desc">{playlist.description}</div>
            {playlist.note && <div className="playlist-note">{playlist.note}</div>}
          </div>
          <StatusBadge playlist={playlist} />
        </div>

        <div className="playlist-actions">
          {playlist.state === 'NotCreated' && (
            <button onClick={() => create.mutate()} disabled={busy}>
              {create.isPending ? <><Spinner /> Creating…</> : 'Create'}
            </button>
          )}
          {playlist.state === 'Differs' && (
            // Destructive: it rewrites the rules of a playlist the user made themselves.
            <button className="playlist-replace" onClick={() => update.mutate()} disabled={busy}>
              {update.isPending ? <><Spinner /> Replacing…</> : 'Replace'}
            </button>
          )}
        </div>

        {error && <p className="error">{error.message}</p>}
      </div>
    </div>
  )
}

// Every rung the Plex scale has, always shown: which of them a user can actually set is the whole
// point of the checkbox beside this, and greying out the halves says that far better than making
// half the list disappear.
const RUNGS = ['0.5', '1.0', '1.5', '2.0', '2.5', '3.0', '3.5', '4.0', '4.5', '5.0']

// What each rating means — the ladder the playlists are built around, and how someone decides which
// scale they want. Keyed by rung so the whole-star ladder lines up with the same rows; the rungs it
// has no entry for are the ones that user can't set.
const HALF_STAR_KEY: Record<string, string> = {
  '0.5': 'Hate. Never play again',
  '1.0': 'Deciding whether to give another chance before blocking',
  '1.5': 'Boring. Maybe stop playing just out of drabness',
  '2.0': 'Slightly interesting but questionable',
  '2.5': 'Stuff to have on. Not too opinionated',
  '3.0': "Like it. Wouldn't really play for others",
  '3.5': 'Like it, Would play for others',
  '4.0': 'Love it, Would play for others',
  '4.5': 'Extremely memorable favorite songs',
  '5.0': 'Undeniable masterpieces',
}

const WHOLE_STAR_KEY: Record<string, string> = {
  '1.0': 'Hate. Never play again',
  '2.0': 'Meh',
  '3.0': "Like it. Wouldn't really play for others",
  '4.0': 'Like it, Would play for others',
  '5.0': 'Love it',
}

// How this user rates in Plex. There is no way to ask Plex: half-star support is a per-client
// capability — Plexamp offers it, Plex Web can only set whole stars — and no server or account
// setting exposes which one someone actually uses. It matters because the lowest score a user can
// give is the one that means "never play again", and Frontier has to leave that music alone.
function RatingScale({ halfStars, busy }: { halfStars: boolean; busy: boolean }) {
  const patchSurvey = usePatchSurvey()

  // The answer is the user's own — the server stores it, it doesn't decide it — so the checkbox and
  // the key beside it flip as soon as the write lands. What the answer *implies* (which tiers exist,
  // which Frontier copies still match) takes a full survey, and that arrives in its own time.
  const save = useMutation({
    mutationFn: (next: boolean) => setRatingScale(next),
    onSuccess: (_result, next) => patchSurvey((survey) => ({ ...survey, halfStars: next })),
  })

  return (
    <div className="dev-tool playlist-scale">
      <div className="playlist-scale-choice">
        <h2>Rating Scale</h2>
        <p>How do you plan on rating your songs?</p>

        <label className="playlist-check">
          <input
            type="checkbox"
            checked={halfStars}
            disabled={save.isPending}
            onChange={(e) => save.mutate(e.target.checked)}
          />
          Rate in half stars
        </label>

        {(save.isPending || busy) && (
          <p className="dev-status disc-sub-busy">
            <Spinner /> {save.isPending ? 'Saving…' : 'Rechecking your playlists…'}
          </p>
        )}
        {save.isError && <p className="error">{(save.error as Error).message}</p>}
      </div>

      <dl className="playlist-scale-key">
        {RUNGS.map((rung) => {
          const meaning = (halfStars ? HALF_STAR_KEY : WHOLE_STAR_KEY)[rung]
          return (
            <div key={rung} className={meaning ? undefined : 'is-unused'}>
              <dt>{rung}★</dt>
              <dd>{meaning ?? ''}</dd>
            </div>
          )
        })}
      </dl>
    </div>
  )
}

function StockPlaylists() {
  const [freshMonths, setFreshMonths] = useState(3)
  const [tierIndex, setTierIndex] = useState<number | null>(null)
  // On by default at three months: an unqualified "5★+" is the same twenty songs every time, which
  // is the thing people actually complain about once they've built one.
  const [fresh, setFresh] = useState(true)

  const survey = useQuery({
    queryKey: ['stock-playlists', freshMonths],
    queryFn: () => getStockPlaylists(freshMonths),
  })

  const byId = useMemo(() => {
    const map = new Map<string, StockPlaylist>()
    for (const p of survey.data?.playlists ?? []) map.set(p.id, p)
    return map
  }, [survey.data])

  // The tiers on offer, ascending, as the server generated them — every half step for a half-star
  // user, every whole star otherwise. Taken from the survey rather than rebuilt here, so the id
  // format ("stars-4", "stars-3_5") stays the server's business alone.
  const tiers = useMemo(
    () =>
      (survey.data?.playlists ?? []).filter(
        (p) => p.id.startsWith('stars-') && !p.id.includes('-fresh'),
      ),
    [survey.data],
  )

  if (survey.isLoading) {
    return (
      <div className="dev-tool playlist-account">
        <span className="disc-sub-busy"><Spinner /> Looking at your Plex playlists…</span>
      </div>
    )
  }

  if (survey.isError) {
    return (
      <div className="dev-tool">
        <p className="error">{(survey.error as Error).message}</p>
      </div>
    )
  }

  // Default to 4★+, the tier most people mean by "the good stuff", but hold the *index* so switching
  // rating scale doesn't leave the slider pointing at a tier that no longer exists.
  const defaultIndex = Math.max(0, tiers.findIndex((t) => t.title.startsWith('4★')))
  const index = Math.min(tierIndex ?? defaultIndex, tiers.length - 1)
  const tier = tiers[index]
  const picked = tier ? byId.get(fresh ? `${tier.id}-fresh` : tier.id) : undefined

  return (
    <>
      <RatingScale halfStars={survey.data?.halfStars ?? false} busy={survey.isFetching} />

      <div className="dev-tool">
        <h2>Starter Playlists</h2>
        <p>Some quick smart playlists to get you rolling</p>
        <div className={survey.isFetching ? 'playlist-rows is-stale' : 'playlist-rows'}>
          {STARTERS
            .map((id) => byId.get(id))
            .filter((p): p is StockPlaylist => p !== undefined)
            .map((playlist) => (
              <PlaylistRow key={playlist.id} playlist={playlist} freshMonths={freshMonths} />
            ))}
        </div>
      </div>

      <div className="dev-tool">
        <h2>By Star Rating</h2>
        <p>Create custom star-based playlists to your liking</p>

        <div className="controls playlist-picker playlist-slider">
          <span className="playlist-picker-label">Rating</span>
          <input
            type="range"
            min={0}
            max={Math.max(0, tiers.length - 1)}
            step={1}
            value={index}
            onChange={(e) => setTierIndex(Number(e.target.value))}
            aria-label="Minimum star rating"
          />
          <span className="playlist-slider-value">{tier?.title ?? '—'}</span>
        </div>

        <div className="controls playlist-picker">
          <label className="playlist-check">
            <input type="checkbox" checked={fresh} onChange={() => setFresh(!fresh)} />
            Skip anything played in the last
          </label>
          <select
            value={freshMonths}
            onChange={(e) => setFreshMonths(Number(e.target.value))}
            disabled={!fresh}
            aria-label="Fresh window"
          >
            {FRESH_WINDOWS.map((months) => (
              <option key={months} value={months}>
                {months} month{months === 1 ? '' : 's'}
              </option>
            ))}
          </select>
        </div>

        <div className={survey.isFetching ? 'playlist-rows is-stale' : 'playlist-rows'}>
          {picked ? (
            <PlaylistRow playlist={picked} freshMonths={freshMonths} />
          ) : (
            <p className="dev-status">Nothing to offer at that rating.</p>
          )}
        </div>
      </div>
    </>
  )
}
