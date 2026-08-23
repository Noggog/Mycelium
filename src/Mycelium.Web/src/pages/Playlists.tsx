import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthContext'
import {
  completePlexLink,
  createStockPlaylist,
  FRESH_WINDOWS,
  getPlexLink,
  getStockPlaylists,
  startPlexLink,
  unlinkPlex,
  updateStockPlaylist,
  type PlexLinkStatus,
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
        <p><em>Log in to build playlists in your Plex account.</em></p>
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
  const queryClient = useQueryClient()
  const [waiting, setWaiting] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  // Set only when the browser blocked the approval tab, so the user can open it by hand.
  const [fallbackUrl, setFallbackUrl] = useState<string | null>(null)
  // The approval happens in another tab; this holds it so we can focus it back if it's still open.
  const authTab = useRef<Window | null>(null)

  const link = useQuery<PlexLinkStatus>({ queryKey: ['plex-link'], queryFn: getPlexLink })

  // While a link is in flight, ask the server whether plex.tv has handed over a token yet. The user
  // is approving in another tab, so there's nothing to react to here except the clock.
  useEffect(() => {
    if (!waiting) return
    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const { outcome, status } = await completePlexLink()
        if (cancelled || outcome === 'pending') return

        setWaiting(false)
        if (outcome === 'linked') {
          queryClient.setQueryData(['plex-link'], status)
          queryClient.invalidateQueries({ queryKey: ['stock-playlists'] })
          authTab.current?.close()
        } else if (outcome === 'noserveraccess') {
          setProblem(
            "That Plex account can't see this server's music library, so nothing made for it would " +
              'show up. Connect the account the library is shared with.',
          )
        } else {
          setProblem('That took too long and the code expired. Try connecting again.')
        }
      } catch (e) {
        if (!cancelled) {
          setWaiting(false)
          setProblem((e as Error).message)
        }
      }
    }, 2000)

    // Plex codes last about half an hour, but a tab left open that long is abandoned, not pending.
    const giveUp = setTimeout(() => {
      if (!cancelled) {
        setWaiting(false)
        setProblem('Gave up waiting for Plex. Try connecting again.')
      }
    }, 5 * 60 * 1000)

    return () => {
      cancelled = true
      clearInterval(timer)
      clearTimeout(giveUp)
    }
  }, [waiting, queryClient])

  // The approval tab is opened *synchronously* on the click, before the round trip that fetches the
  // URL. A tab opened in the promise continuation is a popup as far as the browser is concerned, and
  // gets blocked — so it's opened blank up front and navigated once the URL arrives. If it was blocked
  // anyway, we fall back to a link the user clicks themselves.
  const connect = async () => {
    setProblem(null)
    setFallbackUrl(null)
    const tab = window.open('about:blank', '_blank')
    setStarting(true)
    try {
      const authUrl = await startPlexLink(`${window.location.origin}/playlists`)
      if (tab && !tab.closed) {
        // Severs the opener reference before handing the tab to plex.tv.
        tab.opener = null
        tab.location.href = authUrl
        authTab.current = tab
      } else {
        setFallbackUrl(authUrl)
      }
      setWaiting(true)
    } catch (e) {
      tab?.close()
      setProblem((e as Error).message)
    } finally {
      setStarting(false)
    }
  }

  const disconnect = useMutation({
    mutationFn: unlinkPlex,
    onSuccess: () => {
      queryClient.setQueryData(['plex-link'], { linked: false, username: null, email: null, linkedAt: null })
      queryClient.invalidateQueries({ queryKey: ['stock-playlists'] })
    },
  })

  if (link.isLoading) {
    return <div className="dev-tool"><p><em>Checking your Plex connection…</em></p></div>
  }

  if (link.isError) {
    return (
      <div className="dev-tool">
        <p className="error">Couldn't check your Plex connection: {(link.error as Error).message}</p>
      </div>
    )
  }

  if (!link.data?.linked) {
    return (
      <div className="dev-tool">
        <h2>Connect your Plex account</h2>
        <p>
          Playlists are made <strong>in your own Plex account</strong>, so they show up in your sidebar
          like any playlist you'd make yourself, and the star-rating and "haven't heard it lately" rules
          use <em>your</em> ratings and <em>your</em> play history — not the server owner's.
        </p>
        <p>
          <em>
            Approval happens on plex.tv; this app never sees your password. It keeps a token limited to
            this one server, which you can revoke here at any time.
          </em>
        </p>

        <div className="controls">
          <button onClick={connect} disabled={starting || waiting}>
            {waiting ? 'Waiting for Plex…' : starting ? 'Starting…' : 'Connect Plex'}
          </button>
          {waiting && (
            <button className="secondary" onClick={() => setWaiting(false)}>
              Cancel
            </button>
          )}
        </div>

        {waiting && !fallbackUrl && (
          <p className="dev-status">
            Approve the request in the tab that just opened. This page will pick it up on its own.
          </p>
        )}
        {fallbackUrl && (
          <p className="dev-status">
            Your browser blocked the popup —{' '}
            <a href={fallbackUrl} target="_blank" rel="noreferrer">
              open the Plex approval page
            </a>{' '}
            instead. This page will pick it up once you're done.
          </p>
        )}
        {problem && <p className="error">{problem}</p>}
      </div>
    )
  }

  return (
    <>
      <div className="dev-tool">
        <h2>Plex account</h2>
        <p>
          Connected as <strong>{link.data.username}</strong>
          {link.data.email ? ` (${link.data.email})` : ''}. Playlists below are created here.
        </p>
        <div className="controls">
          <button
            className="secondary"
            onClick={() => disconnect.mutate()}
            disabled={disconnect.isPending}
          >
            {disconnect.isPending ? 'Disconnecting…' : 'Disconnect'}
          </button>
        </div>
        <p className="dev-status">
          Disconnecting only forgets the token — playlists already made stay in your Plex account.
        </p>
        {disconnect.isError && (
          <p className="error">{(disconnect.error as Error).message}</p>
        )}
      </div>
      <StockPlaylists />
    </>
  )
}

// ---- The stock playlists ----------------------------------------------------------------------

const STAR_TIERS = [3, 4, 5] as const
type Variant = 'raw' | 'fresh'

function StatusBadge({ playlist }: { playlist: StockPlaylist }) {
  switch (playlist.state) {
    case 'Exists':
      return (
        <span className="playlist-badge is-exists">
          ✓ In Plex{playlist.matchedTitle ? ` as “${playlist.matchedTitle}”` : ''}
          {playlist.trackCount ? ` · ${playlist.trackCount.toLocaleString()} tracks` : ''}
        </span>
      )
    case 'Differs':
      return <span className="playlist-badge is-differs">Name taken, different rules</span>
    case 'Unavailable':
      return <span className="playlist-badge is-unavailable">Not available yet</span>
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
            {update.isPending ? 'Updating…' : 'Update its rules'}
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
    return <div className="dev-tool"><p><em>Looking at your Plex playlists…</em></p></div>
  }

  if (survey.isError) {
    return (
      <div className="dev-tool">
        <p className="error">Couldn't read your playlists: {(survey.error as Error).message}</p>
      </div>
    )
  }

  const toggle = <T,>(list: T[], value: T, set: (next: T[]) => void) =>
    set(list.includes(value) ? list.filter((v) => v !== value) : [...list, value])

  return (
    <>
      <div className="dev-tool">
        <h2>Starter playlists</h2>
        <p>
          Two playlists worth having whatever your library looks like. Already got one? It'll say so —
          that's worked out by comparing <em>rules</em>, not names, so a playlist you built yourself
          years ago and called something else still counts.
        </p>
        {['my-library', 'frontier']
          .map((id) => byId.get(id))
          .filter((p): p is StockPlaylist => p !== undefined)
          .map((playlist) => (
            <PlaylistRow key={playlist.id} playlist={playlist} freshMonths={freshMonths} />
          ))}
      </div>

      <div className="dev-tool">
        <h2>By star rating</h2>
        <p>
          Pick the tiers you want. <strong>Fresh</strong> variants leave out anything you've played
          recently, which is what stops a favourites playlist turning into the same twenty songs.
        </p>

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
          <p className="dev-status">Pick at least one tier and one variant.</p>
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
                    ? 'All of these already exist'
                    : `Create ${missing.length} missing playlist${missing.length === 1 ? '' : 's'}`}
              </button>
            </div>
            {createAll.isError && <p className="error">{(createAll.error as Error).message}</p>}
          </>
        )}
      </div>

      <div className="dev-tool">
        <p className="dev-status">
          These are ordinary Plex smart playlists — nothing here keeps managing them. Open one in Plex,
          hit <em>Edit filters</em>, and change whatever you like; the app will simply notice the rules
          no longer match and offer to put them back.
        </p>
      </div>
    </>
  )
}
