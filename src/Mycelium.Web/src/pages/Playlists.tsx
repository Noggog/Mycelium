import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthContext'
import { usePlexLink } from '../auth/usePlexLink'
import { Spinner } from '../components/icons'
import {
  createStockPlaylist,
  FRESH_WINDOWS,
  getStockPlaylists,
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

const STAR_TIERS = [3, 4, 5] as const
type Variant = 'raw' | 'fresh'

function StatusBadge({ playlist }: { playlist: StockPlaylist }) {
  switch (playlist.state) {
    case 'Exists': {
      // The name is only worth showing when it isn't the one we'd have used — that's the case where
      // "you already have this" would otherwise look wrong.
      const renamed =
        playlist.matchedTitle && playlist.matchedTitle !== playlist.title
          ? ` as “${playlist.matchedTitle}”`
          : ''
      const tracks = playlist.trackCount ? ` · ${playlist.trackCount.toLocaleString()}` : ''
      return <span className="playlist-badge is-exists">✓ In Plex{renamed}{tracks}</span>
    }
    case 'Differs':
      return <span className="playlist-badge is-differs">Name taken</span>
    case 'Unavailable':
      return <span className="playlist-badge is-unavailable">Not yet</span>
    default:
      return <span className="playlist-badge is-missing">Not created</span>
  }
}

function PlaylistRow({
  playlist,
  freshMonths,
  selectable,
  selected,
  onToggle,
}: {
  playlist: StockPlaylist
  freshMonths: number
  selectable?: boolean
  selected?: boolean
  onToggle?: () => void
}) {
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
        {selectable && (
          <label className="playlist-pick">
            <input type="checkbox" checked={selected} onChange={onToggle} />
          </label>
        )}
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
          <button onClick={() => update.mutate()} disabled={busy}>
            {update.isPending ? 'Updating…' : 'Update'}
          </button>
        )}
      </div>

      {error && <p className="error">{error.message}</p>}
    </div>
  )
}

function StockPlaylists() {
  const queryClient = useQueryClient()
  const [freshMonths, setFreshMonths] = useState(3)
  const [tiers, setTiers] = useState<number[]>([3, 4, 5])
  const [variants, setVariants] = useState<Variant[]>(['raw'])

  const survey = useQuery({
    queryKey: ['stock-playlists', freshMonths],
    queryFn: () => getStockPlaylists(freshMonths),
  })

  const byId = useMemo(() => {
    const map = new Map<string, StockPlaylist>()
    for (const p of survey.data?.playlists ?? []) map.set(p.id, p)
    return map
  }, [survey.data])

  // The tier rows the picker currently selects, in a stable order.
  const picked = useMemo(() => {
    const ids: string[] = []
    for (const stars of STAR_TIERS) {
      if (!tiers.includes(stars)) continue
      if (variants.includes('raw')) ids.push(`stars-${stars}`)
      if (variants.includes('fresh')) ids.push(`stars-${stars}-fresh`)
    }
    return ids.map((id) => byId.get(id)).filter((p): p is StockPlaylist => p !== undefined)
  }, [tiers, variants, byId])

  const missing = picked.filter((p) => p.state === 'NotCreated')

  // Created one at a time rather than in parallel: this is someone's home server, and a half-finished
  // batch is easier to reason about than four simultaneous failures.
  const createAll = useMutation({
    mutationFn: async () => {
      for (const playlist of missing) {
        await createStockPlaylist(playlist.id, freshMonths)
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['stock-playlists'] }),
  })

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

  const toggle = <T,>(list: T[], value: T, set: (next: T[]) => void) =>
    set(list.includes(value) ? list.filter((v) => v !== value) : [...list, value])

  return (
    <>
      <div className="dev-tool">
        <h2>Starter playlists</h2>
        {['my-library', 'frontier']
          .map((id) => byId.get(id))
          .filter((p): p is StockPlaylist => p !== undefined)
          .map((playlist) => (
            <PlaylistRow key={playlist.id} playlist={playlist} freshMonths={freshMonths} />
          ))}
      </div>

      <div className="dev-tool">
        <h2>By star rating</h2>
        <p>Only play things not heard recently.</p>

        <div className="controls playlist-picker">
          <span className="playlist-picker-label">Tiers</span>
          {STAR_TIERS.map((stars) => (
            <label key={stars} className="playlist-check">
              <input
                type="checkbox"
                checked={tiers.includes(stars)}
                onChange={() => toggle(tiers, stars, setTiers)}
              />
              {stars}★+
            </label>
          ))}
        </div>

        <div className="controls playlist-picker">
          <span className="playlist-picker-label">Variants</span>
          <label className="playlist-check">
            <input
              type="checkbox"
              checked={variants.includes('raw')}
              onChange={() => toggle(variants, 'raw' as Variant, setVariants)}
            />
            Raw
          </label>
          <label className="playlist-check">
            <input
              type="checkbox"
              checked={variants.includes('fresh')}
              onChange={() => toggle(variants, 'fresh' as Variant, setVariants)}
            />
            Fresh
          </label>
          <select
            value={freshMonths}
            onChange={(e) => setFreshMonths(Number(e.target.value))}
            disabled={!variants.includes('fresh')}
            aria-label="Fresh window"
          >
            {FRESH_WINDOWS.map((months) => (
              <option key={months} value={months}>
                not played in {months} month{months === 1 ? '' : 's'}
              </option>
            ))}
          </select>
        </div>

        {picked.length === 0 ? (
          <p className="dev-status">Pick a tier and a variant.</p>
        ) : (
          <>
            {picked.map((playlist) => (
              <PlaylistRow key={playlist.id} playlist={playlist} freshMonths={freshMonths} />
            ))}

            <div className="controls">
              <button
                onClick={() => createAll.mutate()}
                disabled={missing.length === 0 || createAll.isPending}
              >
                {createAll.isPending
                  ? 'Creating…'
                  : missing.length === 0
                    ? 'All set'
                    : `Create ${missing.length}`}
              </button>
            </div>
            {createAll.isError && <p className="error">{(createAll.error as Error).message}</p>}
          </>
        )}
      </div>
    </>
  )
}
