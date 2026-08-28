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
      <PlexConnection />
    </section>
  )
}

// ---- Connecting the user's own Plex account ---------------------------------------------------

function PlexConnection() {
  const plex = usePlexLink()
  const [token, setToken] = useState('')
  const [label, setLabel] = useState('')

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
        <h2>Connect your Plex account</h2>
        <p>
          Connect Plex to get a few useful smart playlists set up for you — like one holding only the
          artists you've thumbed up, or your top-rated tracks you haven't heard in a while.
        </p>

        <div className="controls">
          <button
            onClick={() => plex.connect(`${window.location.origin}/playlists`)}
            disabled={plex.starting || plex.waiting}
          >
            {plex.waiting ? 'Waiting for Plex…' : plex.starting ? 'Starting…' : 'Connect Plex'}
          </button>
          {plex.waiting && (
            <button className="secondary" onClick={plex.cancel}>
              Cancel
            </button>
          )}
        </div>

        {plex.waiting && !plex.fallbackUrl && (
          <p className="dev-status">Approve in the tab that opened — this page will update.</p>
        )}
        {plex.fallbackUrl && (
          <p className="dev-status">
            Popup blocked —{' '}
            <a href={plex.fallbackUrl} target="_blank" rel="noreferrer">
              open the approval page
            </a>
            .
          </p>
        )}
        {plex.problem && <p className="error">{plex.problem}</p>}

        {/* The approval flow links whoever is signed in at app.plex.tv in this browser, which can't
            reach a Plex Home / managed user — they have no session of their own. Pasting their token
            names the account directly. Same control as the one in the header menu. */}
        <details className="playlist-token">
          <summary>Or paste a Plex token</summary>
          <p className="dev-status">
            For signing in as a Plex Home or managed user, who has no app.plex.tv session to approve
            with. An account token is checked with plex.tv, and only the server-scoped token it hands
            back is kept. A server access token is checked against the server instead — that proves
            the token works, but the server reports the owner's identity whatever token asks, so it
            can't say whose it is. Name it yourself in that case.
          </p>
          <form
            className="controls"
            onSubmit={async (e) => {
              e.preventDefault()
              if (await plex.linkWithToken(token, label)) {
                setToken('')
                setLabel('')
              }
            }}
          >
            <input
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="X-Plex-Token"
              autoComplete="off"
              spellCheck={false}
              aria-label="Plex token"
            />
            <input
              type="text"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              placeholder="Name (if Plex can't say)"
              autoComplete="off"
              aria-label="Account name"
            />
            <button type="submit" disabled={plex.linkingToken || token.trim() === ''}>
              {plex.linkingToken ? 'Checking…' : 'Link token'}
            </button>
          </form>
        </details>
      </div>
    )
  }

  return (
    <>
      <div className="dev-tool playlist-account">
        <span className="playlist-account-name">
          Plex: <strong>{plex.status.username}</strong>
        </span>
        <button className="secondary" onClick={plex.disconnect} disabled={plex.disconnecting}>
          {plex.disconnecting ? 'Disconnecting…' : 'Disconnect'}
        </button>
      </div>
      {plex.disconnectError && <p className="error">{plex.disconnectError.message}</p>}
      <StockPlaylists />
    </>
  )
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

function PlaylistRow({ playlist, freshMonths }: { playlist: StockPlaylist; freshMonths: number }) {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['stock-playlists'] })

  const create = useMutation({
    mutationFn: () => createStockPlaylist(playlist.id, freshMonths),
    onSuccess: refresh,
  })
  const update = useMutation({
    mutationFn: () => updateStockPlaylist(playlist.id, freshMonths),
    onSuccess: refresh,
  })
  const busy = create.isPending || update.isPending
  const error = (create.error ?? update.error) as Error | undefined

  return (
    <div className="playlist-row">
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
            {create.isPending ? 'Creating…' : 'Create'}
          </button>
        )}
        {playlist.state === 'Differs' && (
          // Destructive: it rewrites the rules of a playlist the user made themselves.
          <button className="playlist-replace" onClick={() => update.mutate()} disabled={busy}>
            {update.isPending ? 'Replacing…' : 'Replace'}
          </button>
        )}
      </div>

      {error && <p className="error">{error.message}</p>}
    </div>
  )
}

// What each rating means, so the scales aren't just a number of stars — this is the ladder the
// playlists are built around, and seeing it is how someone decides which scale they want. The half
// scale earns its extra rungs at the top ("I'd play it for others" is a different verdict from "I
// like it"); the whole scale says the same thing in five.
const HALF_STAR_KEY: [string, string][] = [
  ['0.5', 'Hate. Never play again'],
  ['1.0', 'Deciding whether to give another chance before blocking'],
  ['1.5', "Boring. Maybe stop playing just out of drabness"],
  ['2.0', 'Slightly interesting but questionable'],
  ['2.5', 'Stuff to have on. Not too opinionated'],
  ['3.0', 'Like it. Wouldn\'t really play for others'],
  ['3.5', 'Like it, Would play for others'],
  ['4.0', 'Love it, Would play for others'],
  ['4.5', 'Extremely memorable favorite songs'],
  ['5.0', 'Undeniable masterpieces'],
]

const WHOLE_STAR_KEY: [string, string][] = [
  ['1', 'Hate. Never play again'],
  ['2', 'Meh'],
  ['3', 'Like it. Wouldn\'t really play for others'],
  ['4', 'Like it, Would play for others'],
  ['5', 'Love it'],
]

// How this user rates in Plex. There is no way to ask Plex: half-star support is a per-client
// capability — Plexamp offers it, Plex Web can only set whole stars — and no server or account
// setting exposes which one someone actually uses. It matters because the lowest score a user can
// give is the one that means "never play again", and Frontier has to leave that music alone.
function RatingScale({ halfStars }: { halfStars: boolean }) {
  const queryClient = useQueryClient()

  // Invalidates the survey rather than just the checkbox: the Frontier rules change with the answer,
  // so every row's "do you already have this?" verdict has to be recomputed.
  const save = useMutation({
    mutationFn: (next: boolean) => setRatingScale(next),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['stock-playlists'] }),
  })

  return (
    <div className="dev-tool playlist-scale">
      <div className="playlist-scale-choice">
        <h2>Rating scale</h2>
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

        {save.isError && <p className="error">{(save.error as Error).message}</p>}
      </div>

      <dl className="playlist-scale-key">
        {(halfStars ? HALF_STAR_KEY : WHOLE_STAR_KEY).map(([stars, meaning]) => (
          <div key={stars}>
            <dt>{stars}★</dt>
            <dd>{meaning}</dd>
          </div>
        ))}
      </dl>
    </div>
  )
}

function StockPlaylists() {
  const [freshMonths, setFreshMonths] = useState(3)
  const [tierIndex, setTierIndex] = useState<number | null>(null)
  const [fresh, setFresh] = useState(false)

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
        (p) => p.id.startsWith('stars-') && !p.id.endsWith('-fresh'),
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
      <RatingScale halfStars={survey.data?.halfStars ?? true} />

      <div className="dev-tool">
        <h2>Starter playlists</h2>
        {['my-library', 'frontier', 'frontier-deep']
          .map((id) => byId.get(id))
          .filter((p): p is StockPlaylist => p !== undefined)
          .map((playlist) => (
            <PlaylistRow key={playlist.id} playlist={playlist} freshMonths={freshMonths} />
          ))}
      </div>

      <div className="dev-tool">
        <h2>By star rating</h2>
        <p>One playlist of everything at or above the rating you pick.</p>

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
            Skip anything played recently
          </label>
          <select
            value={freshMonths}
            onChange={(e) => setFreshMonths(Number(e.target.value))}
            disabled={!fresh}
            aria-label="Fresh window"
          >
            {FRESH_WINDOWS.map((months) => (
              <option key={months} value={months}>
                not played in {months} month{months === 1 ? '' : 's'}
              </option>
            ))}
          </select>
        </div>

        {picked ? (
          <PlaylistRow playlist={picked} freshMonths={freshMonths} />
        ) : (
          <p className="dev-status">Nothing to offer at that rating.</p>
        )}
      </div>
    </>
  )
}
