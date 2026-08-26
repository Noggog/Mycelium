import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthContext'
import { getRelated } from '../api/related'
import { refreshCatalog } from '../api/artists'
import { refreshQueue } from '../api/discovery'
import {
  getCombinedArtists,
  resolveCombinedArtists,
  type CleanupResult,
  type CombinedNameEntry,
} from '../api/maintenance'
import {
  clearPlexServerToken,
  clearPlexTags,
  completePlexServerTokenLink,
  getPlexServerToken,
  getSimilarityWarmStatus,
  getUserQualities,
  reapplyPlexTags,
  rebuildPlexTags,
  runQualitySweep,
  setPlexServerToken,
  setUserQuality,
  startPlexServerTokenLink,
  startSimilarityWarm,
  verifyPlexServerToken,
  type PlexServerTokenStatus,
  type RebuildResult,
} from '../api/dev'
import type { AudioQuality } from '../types'

// The in-app dev panel: tooling that's only shown to (and only usable by) DEV_USERNAMES users.
// Absorbs the old Related (dev) similarity debugger and adds the Plex tag maintenance controls.
export default function Dev() {
  const { user, isLoading } = useAuth()

  if (isLoading) {
    return (
      <section>
        <h1>Dev tools</h1>
        <p><em>…</em></p>
      </section>
    )
  }

  // The route is rendered for everyone, but the panel is dev-only. The server enforces the same gate
  // on every endpoint, so this is just to avoid showing controls that would 403.
  if (!user?.isDev) {
    return (
      <section>
        <h1>Dev tools</h1>
        <p><em>Not available for this account.</em></p>
      </section>
    )
  }

  return (
    <section>
      <h1>Dev tools</h1>
      <PlexServerToken />
      <CatalogRefresh />
      <QualitySweep />
      <UserQuality />
      <CleanupTool />
      <PlexTagTools />
      <SimilarityWarm />
      <QueueRebuild />
      <SimilarityDebug />
    </section>
  )
}

// ---- The server's own Plex credential ----

// Everything else on this page that touches Plex depends on this token, so it renders first. It is
// minted here and stored, rather than configured — an expired one is re-linked in the browser instead
// of costing a redeploy.

function tokenVerdict(status: PlexServerTokenStatus): { text: string; className: string } {
  if (!status.configured) return { text: 'Not linked', className: 'error' }
  if (status.valid === false) return { text: 'Rejected by Plex', className: 'error' }
  // "Present" is not the same claim as "works" — an unchecked token says so rather than reading green.
  if (status.valid === null) return { text: 'Not checked yet', className: 'dev-status' }
  return { text: 'Working', className: 'dev-status' }
}

function PlexServerToken() {
  const queryClient = useQueryClient()
  const [waiting, setWaiting] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [fallbackUrl, setFallbackUrl] = useState<string | null>(null)
  const [paste, setPaste] = useState('')
  const authTab = useRef<Window | null>(null)

  const status = useQuery<PlexServerTokenStatus>({
    queryKey: ['plex-server-token'],
    queryFn: getPlexServerToken,
  })

  const settle = (next: PlexServerTokenStatus) => {
    queryClient.setQueryData(['plex-server-token'], next)
    // A token that just started working makes the stale catalog worth re-reading.
    if (next.valid) queryClient.invalidateQueries({ queryKey: ['artists'] })
  }

  // While the operator approves in the other tab, ask whether plex.tv has handed over a token yet.
  // Same shape as the per-user flow in usePlexLink — there's nothing to react to here but the clock.
  useEffect(() => {
    if (!waiting) return
    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const { outcome, status: next } = await completePlexServerTokenLink()
        if (cancelled || outcome === 'pending') return

        setWaiting(false)
        if (outcome === 'linked') {
          queryClient.setQueryData(['plex-server-token'], next)
          queryClient.invalidateQueries({ queryKey: ['artists'] })
          authTab.current?.close()
        } else if (outcome === 'noserveraccess') {
          setProblem("That Plex account can't see this server's library, so its token would be useless here.")
        } else {
          setProblem('Timed out — try again.')
        }
      } catch (e) {
        if (!cancelled) {
          setWaiting(false)
          setProblem((e as Error).message)
        }
      }
    }, 2000)

    const giveUp = setTimeout(() => {
      if (!cancelled) {
        setWaiting(false)
        setProblem('Timed out — try again.')
      }
    }, 5 * 60 * 1000)

    return () => {
      cancelled = true
      clearInterval(timer)
      clearTimeout(giveUp)
    }
  }, [waiting, queryClient])

  // The tab is opened synchronously on the click, before the round trip that fetches the URL — one
  // opened in the promise continuation is a popup as far as the browser is concerned, and gets blocked.
  const connect = async () => {
    setProblem(null)
    setFallbackUrl(null)
    const tab = window.open('about:blank', '_blank')
    try {
      const authUrl = await startPlexServerTokenLink(window.location.href)
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
    }
  }

  const check = useMutation({ mutationFn: verifyPlexServerToken, onSuccess: settle })

  const pasteToken = useMutation({
    mutationFn: setPlexServerToken,
    onSuccess: (completion) => {
      if (completion.outcome !== 'linked') {
        setProblem("Plex won't accept that token — check it copied whole, and that it hasn't been reset.")
        return
      }
      setProblem(null)
      setPaste('')
      settle(completion.status)
    },
    onError: (e: Error) => setProblem(e.message),
  })

  const clear = useMutation({ mutationFn: clearPlexServerToken, onSuccess: settle })

  const current = status.data
  const verdict = current ? tokenVerdict(current) : null

  return (
    <div className="dev-tool">
      <h2>Plex connection</h2>
      <p>
        The credential every library read is made with — the catalog sync, the quality sweep, the tag
        writes. Linking stores a <strong>server-scoped</strong> token (it reaches this one library and
        nothing else in the account) and takes effect on the next call, with no restart. Link as the{' '}
        <strong>server owner</strong>: the same token writes the library&rsquo;s mood tags.
      </p>
      <p>
        Plex revokes tokens when the account password changes with &ldquo;sign out connected
        devices&rdquo; set. The daily catalog sync re-checks this one and pings plex.tv to keep it
        fresh, so a lapse shows up here rather than as a button that fails.
      </p>

      {status.isLoading && <p><em>…</em></p>}
      {status.isError && <p className="error">{(status.error as Error).message}</p>}

      {current && verdict && (
        <p className={verdict.className}>
          <strong>{verdict.text}</strong>
          {current.username && <> — linked as {current.username}</>}
          {current.checkedAt && <> · checked {new Date(current.checkedAt).toLocaleString()}</>}
        </p>
      )}
      {current?.problem && <p className="error">{current.problem}</p>}

      <div className="controls">
        <button type="button" onClick={connect} disabled={waiting}>
          {waiting ? 'Waiting for approval…' : 'Link with Plex'}
        </button>
        <button type="button" onClick={() => check.mutate()} disabled={check.isPending}>
          {check.isPending ? 'Checking…' : 'Check now'}
        </button>
        {current?.configured && (
          <button type="button" onClick={() => clear.mutate()} disabled={clear.isPending}>
            {clear.isPending ? 'Disconnecting…' : 'Disconnect Plex'}
          </button>
        )}
      </div>

      {waiting && (
        <p className="dev-status">
          Approve the request in the Plex tab and this will pick it up.{' '}
          <button type="button" onClick={() => setWaiting(false)}>Cancel</button>
        </p>
      )}
      {fallbackUrl && (
        <p className="dev-status">
          The approval tab was blocked —{' '}
          <a href={fallbackUrl} target="_blank" rel="noreferrer noopener">open it here</a>.
        </p>
      )}

      <form
        className="controls"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (paste.trim()) pasteToken.mutate(paste.trim())
        }}
      >
        <input
          type="password"
          value={paste}
          onChange={(e) => setPaste(e.target.value)}
          placeholder="…or paste a token from Plex Web"
          autoComplete="off"
        />
        <button type="submit" disabled={!paste.trim() || pasteToken.isPending}>
          {pasteToken.isPending ? 'Checking…' : 'Use pasted token'}
        </button>
      </form>

      {problem && <p className="error">{problem}</p>}
    </div>
  )
}

// ---- Emergency rebuild of every user's recommendation queue ----

function QueueRebuild() {
  const queryClient = useQueryClient()
  const rebuild = useMutation({
    mutationFn: refreshQueue,
    onSuccess: () => {
      // The queue feeds Discover and the to-buy list — refresh both once it's rebuilt.
      queryClient.invalidateQueries({ queryKey: ['feed'] })
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })

  return (
    <div className="dev-tool">
      <h2>Rebuild recommendations</h2>
      <p>
        Discards the pending recommendation queue for <strong>every user</strong> and recomputes each
        from scratch by re-expanding one hop out from that user's currently-liked artists. This is a
        site-wide sweep — it touches all accounts — and it <strong>keeps ratings</strong> (likes/dislikes/
        snoozes are untouched); it just rebuilds the <em>undecided</em> candidates the swipe feed draws
        from. It reads the already-persisted similarity graph (lazily fetching a source on a cache miss),
        so a cold graph makes it slower — warm it first with <em>Rebuild entire graph</em> for speed.
      </p>
      <p>
        <em>
          You normally shouldn't need this. Liking an artist already expands its recommendations
          immediately, and disliking / un-liking now prunes the candidates that artist had seeded — so
          each queue tracks taste on its own. This is the emergency "nuke and recompute" button for
          when they drift anyway.
        </em>
      </p>

      <div className="controls">
        <button onClick={() => rebuild.mutate()} disabled={rebuild.isPending}>
          {rebuild.isPending ? 'Rebuilding…' : 'Rebuild all recommendations'}
        </button>
      </div>

      {rebuild.isError && <p className="error">Rebuild failed: {(rebuild.error as Error).message}</p>}
      {rebuild.isSuccess && (
        <p className="dev-status">✓ Rebuilt {rebuild.data?.rebuilt ?? 'all'} recommendation queues.</p>
      )}
    </div>
  )
}

// ---- One-off audio-quality catch-up ----

function QualitySweep() {
  const queryClient = useQueryClient()
  const sweep = useMutation({
    mutationFn: runQualitySweep,
    onSuccess: () => {
      // Ownership now carries quality, which the feed and the to-buy list both read.
      queryClient.invalidateQueries({ queryKey: ['feed'] })
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })

  return (
    <div className="dev-tool">
      <h2>Audio quality sweep</h2>
      <p>
        Reads every track in the Plex library to work out what format each album is actually in.
        Needed <strong>once</strong>, to fill in albums that predate quality tracking — after that the
        ordinary syncs resolve new arrivals a few at a time, whether they came from a download or were
        dropped into the library by hand.
      </p>
      <p>
        Takes roughly <strong>20&ndash;30 seconds</strong> on a large library and only reads from Plex
        — nothing is moved, changed or downloaded.
      </p>

      <div className="controls">
        <button type="button" onClick={() => sweep.mutate()} disabled={sweep.isPending}>
          {sweep.isPending ? 'Sweeping…' : 'Run quality sweep'}
        </button>
      </div>

      {sweep.isError && <p className="error">Sweep failed: {(sweep.error as Error).message}</p>}
      {sweep.isSuccess && (
        <p className="dev-status">✓ Swept the library ({sweep.data?.artists ?? 0} artists).</p>
      )}
    </div>
  )
}

// ---- Per-user download quality ----

// Lossless runs ~3x the size of 320kbps for the same album, so this is really a disk-budget control:
// it decides what each person's likes cost on the shared library volume, not what they can listen to
// (Plex transcodes on playback regardless).
const QUALITY_LABEL: Record<AudioQuality, string> = {
  Lossy: 'MP3 320',
  Lossless: 'FLAC',
}

function UserQuality() {
  const queryClient = useQueryClient()

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['dev', 'user-quality'],
    queryFn: getUserQualities,
  })

  const update = useMutation({
    mutationFn: ({ subject, quality }: { subject: string; quality: AudioQuality | null }) =>
      setUserQuality(subject, quality),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dev', 'user-quality'] })
      // A raised ceiling changes what pending albums download at, and the to-buy list shows it.
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })

  const users = data?.users ?? []

  return (
    <div className="dev-tool">
      <h2>Download quality</h2>
      <p>
        What each account&apos;s requests are downloaded at. FLAC is roughly <strong>3x</strong> the
        size of MP3 320 for the same album, so this caps what one person&apos;s likes cost on the
        shared library. It doesn&apos;t affect listening — Plex transcodes on playback either way.
      </p>
      <p>
        An album several people want is fetched <em>once</em>, at the best of their settings. Accounts
        appear here after they have signed in at least once.
      </p>

      {isPending && <p><em>Loading users…</em></p>}
      {isError && <p className="error">Failed to load users: {(error as Error).message}</p>}

      {!isPending && !isError && users.length === 0 && (
        <p><em>No users have signed in yet.</em></p>
      )}

      {users.length > 0 && (
        <table className="dev-table">
          <thead>
            <tr>
              <th>User</th>
              <th>Last login</th>
              <th>Quality</th>
            </tr>
          </thead>
          <tbody>
            {users.map(u => (
              <tr key={u.subject}>
                <td>
                  {u.displayName ?? u.username ?? u.subject}
                  {u.username && u.displayName && <span className="dev-muted"> ({u.username})</span>}
                </td>
                <td>{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleDateString() : '—'}</td>
                <td>
                  <select
                    value={u.maxQuality ?? ''}
                    disabled={update.isPending}
                    onChange={e =>
                      update.mutate({
                        subject: u.subject,
                        quality: e.target.value === '' ? null : (e.target.value as AudioQuality),
                      })
                    }
                  >
                    {/* Empty = no explicit tier, so this account follows the deployment default. */}
                    <option value="">Default ({QUALITY_LABEL[data!.defaultQuality]})</option>
                    <option value="Lossy">{QUALITY_LABEL.Lossy}</option>
                    <option value="Lossless">{QUALITY_LABEL.Lossless}</option>
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {update.isError && (
        <p className="error">Couldn&apos;t save: {(update.error as Error).message}</p>
      )}
    </div>
  )
}

// ---- Combined-name cleanup (split Plex's semicolon-joined collaborators) ----

const CLEANUP_SCOPE_LABEL: Record<CombinedNameEntry['scope'], string> = {
  catalog: 'Library artist',
  artistRating: 'Artist rating',
  albumRating: 'Album rating',
}

function CleanupResultSummary({ result }: { result: CleanupResult }) {
  const parts = [
    result.catalogSplit > 0 && `${result.catalogSplit} library artist(s) split`,
    result.artistRatingsSplit > 0 && `${result.artistRatingsSplit} artist rating(s) re-attributed`,
    result.albumRatingsSplit > 0 && `${result.albumRatingsSplit} album rating(s) re-attributed`,
    result.pendingRemoved > 0 && `${result.pendingRemoved} stale recommendation(s) dropped`,
  ].filter(Boolean) as string[]

  return (
    <p className="dev-status">
      ✓ Done. {parts.length > 0 ? parts.join(', ') + '.' : 'Nothing needed changing.'}
    </p>
  )
}

function CleanupTool() {
  const queryClient = useQueryClient()

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['maintenance', 'combined-artists'],
    queryFn: getCombinedArtists,
  })

  const resolve = useMutation({
    mutationFn: resolveCombinedArtists,
    onSuccess: () => {
      // The sweep touches the catalog feed, ratings and the to-buy list — refresh them all.
      queryClient.invalidateQueries({ queryKey: ['maintenance'] })
      queryClient.invalidateQueries({ queryKey: ['feed'] })
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })

  const entries = data ?? []

  return (
    <div className="dev-tool">
      <h2>Cleanup {entries.length > 0 ? `(${entries.length})` : ''}</h2>
      <p>
        Plex sometimes joins collaborators into one name with a semicolon (e.g.{' '}
        <code>Nina Simone;Hot Chip</code>). These are really two artists. Resolving splits them apart
        in the library and re-attributes any ratings to each real artist.
      </p>

      {isError && <p className="error">Failed to scan: {(error as Error).message}</p>}
      {resolve.isError && <p className="error">Cleanup failed: {(resolve.error as Error).message}</p>}
      {isPending && <p><em>Scanning…</em></p>}

      {resolve.isSuccess && !resolve.isPending && <CleanupResultSummary result={resolve.data} />}

      {data && entries.length === 0 && !resolve.isSuccess && (
        <p><em>Nothing to clean up — no combined names found. 🎉</em></p>
      )}

      {entries.length > 0 && (
        <>
          <div className="controls">
            <button onClick={() => resolve.mutate()} disabled={resolve.isPending}>
              {resolve.isPending ? 'Cleaning…' : `Clean up all ${entries.length}`}
            </button>
          </div>

          <div className="disc-list cleanup-list">
            {entries.map((e) => (
              <div className="disc-row" key={`${e.scope}:${e.name}:${e.album ?? ''}`}>
                <div className="disc-row-main">
                  <span className="feed-badge">{CLEANUP_SCOPE_LABEL[e.scope]}</span>
                  <div className="disc-name">
                    {e.name}
                    {e.album ? ` — ${e.album}` : ''}
                  </div>
                  <span className="disc-provenance">
                    → {e.splitInto.join(' + ')}
                    {e.affected > 1 ? ` (${e.affected} entries)` : ''}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}

// ---- Library catalog refresh (the one Plex-touching sync) ----

function CatalogRefresh() {
  const queryClient = useQueryClient()
  const refresh = useMutation({
    mutationFn: refreshCatalog,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['artists'] }),
  })

  return (
    <div className="dev-tool">
      <h2>Refresh from Plex</h2>
      <p>
        Re-syncs the <strong>library catalog</strong> from your Plex server — the one and only call
        that touches Plex directly. It pulls the full artist list from your Plex music library and
        upserts it into the local catalog store: artists new to Plex are added, artists still present
        have their metadata refreshed, and artists no longer in Plex are <strong>marked absent</strong>{' '}
        (soft-removed) so they drop out of the Artists list. It does <strong>not</strong> resolve
        Deezer identities, warm the similarity graph, or change any ratings/tags — just the artist
        roster. The catalog already auto-syncs on startup and once daily, so this is only needed when
        you've just added/removed artists in Plex and want the change reflected immediately. Safe to
        run repeatedly; it's idempotent.
      </p>

      <div className="controls">
        <button onClick={() => refresh.mutate()} disabled={refresh.isPending}>
          {refresh.isPending ? 'Refreshing…' : 'Refresh from Plex'}
        </button>
      </div>

      {refresh.isError && <p className="error">Refresh failed: {(refresh.error as Error).message}</p>}

      {refresh.isSuccess && (
        <p className="dev-status">
          ✓ Synced: {refresh.data.upserted} from Plex, {refresh.data.markedAbsent} removed,{' '}
          {refresh.data.totalPresent} in catalog.
        </p>
      )}
    </div>
  )
}

// ---- Whole-library similarity warm ----

function SimilarityWarm() {
  const queryClient = useQueryClient()
  const [force, setForce] = useState(false)

  const { data: status } = useQuery({
    queryKey: ['dev', 'similarity-warm'],
    queryFn: getSimilarityWarmStatus,
    // Poll while a warm is in flight; idle otherwise.
    refetchInterval: (query) => (query.state.data?.running ? 1500 : false),
  })

  const start = useMutation({
    mutationFn: () => startSimilarityWarm(force),
    onSuccess: (s) => queryClient.setQueryData(['dev', 'similarity-warm'], s),
  })

  const running = status?.running ?? false
  const pct = status && status.total > 0 ? Math.round((status.processed / status.total) * 100) : 0

  return (
    <div className="dev-tool">
      <h2>Rebuild entire graph</h2>
      <p>
        Warms the similarity graph for <strong>every artist in the library</strong> across all
        sources (Deezer + ListenBrainz), instead of waiting for the lazy path to fill it as you
        browse/swipe. Runs in the background — bounded by MusicBrainz's ~1 request/second, so a large
        library takes a while. <em>Force refresh</em> re-fetches even edges that are still fresh;
        otherwise only missing/stale edges are filled.
      </p>

      <div className="controls">
        <button
          onClick={() => {
            if (
              window.confirm(
                force
                  ? 'Re-fetch similarity edges for EVERY library artist from all sources?'
                  : 'Fill in missing/stale similarity edges for every library artist?',
              )
            ) {
              start.mutate()
            }
          }}
          disabled={running || start.isPending}
        >
          {running ? 'Warming…' : 'Rebuild entire graph'}
        </button>
        <label style={{ display: 'flex', alignItems: 'center', gap: '0.35rem' }}>
          <input
            type="checkbox"
            checked={force}
            onChange={(e) => setForce(e.target.checked)}
            disabled={running}
          />
          Force refresh
        </label>
      </div>

      {start.isError && <p className="error">{(start.error as Error).message}</p>}

      {status && (status.running || status.finishedAt) && (
        <p className="dev-status">
          {status.running ? (
            <>
              Processed {status.processed} / {status.total} ({pct}%)
              {status.errors > 0 ? `, ${status.errors} error(s)` : ''}
              {status.currentArtist ? ` — ${status.currentArtist}` : ''}
            </>
          ) : (
            <>
              ✓ Done. Processed {status.processed} / {status.total}
              {status.errors > 0 ? `, ${status.errors} error(s)` : ''}.
            </>
          )}
        </p>
      )}
    </div>
  )
}

// ---- Plex tag maintenance ----

function PlexTagTools() {
  const [status, setStatus] = useState<string | null>(null)

  const clear = useMutation({
    mutationFn: clearPlexTags,
    onSuccess: (r) => setStatus(`Cleared managed tags from ${r.cleared} artist(s).`),
    onError: (e) => setStatus((e as Error).message),
  })
  const reapply = useMutation({
    mutationFn: reapplyPlexTags,
    onSuccess: (r) => setStatus(`Reapplied ${r.applied} tag(s) from stored ratings.`),
    onError: (e) => setStatus((e as Error).message),
  })
  const rebuild = useMutation({
    mutationFn: rebuildPlexTags,
    onSuccess: (r: RebuildResult) =>
      setStatus(`Rebuilt: cleared ${r.cleared} artist(s), reapplied ${r.applied} tag(s).`),
    onError: (e) => setStatus((e as Error).message),
  })

  const busy = clear.isPending || reapply.isPending || rebuild.isPending

  return (
    <div className="dev-tool">
      <h2>Plex tags</h2>
      <p>
        Per-user <code>&lt;username&gt;_liked</code> / <code>_disliked</code> mood tags mirrored onto
        artists in Plex. Clear nukes every verdict tag — moods, plus the same-named collections an
        earlier version wrote; reapply re-derives the moods from stored ratings; rebuild does both (the
        true reset). The permanent <code>&lt;username&gt;_added</code> credits on albums are untouched by
        all three — nothing could rebuild them.
      </p>

      <div className="controls">
        <button
          onClick={() => {
            if (window.confirm('Remove every "_liked"/"_disliked" tag from all Plex artists? ("_added" credits are kept.)')) {
              setStatus(null)
              clear.mutate()
            }
          }}
          disabled={busy}
        >
          {clear.isPending ? 'Clearing…' : 'Clear'}
        </button>
        <button
          onClick={() => {
            setStatus(null)
            reapply.mutate()
          }}
          disabled={busy}
        >
          {reapply.isPending ? 'Reapplying…' : 'Reapply from ratings'}
        </button>
        <button
          onClick={() => {
            if (window.confirm('Wipe all managed tags, then reapply from current ratings?')) {
              setStatus(null)
              rebuild.mutate()
            }
          }}
          disabled={busy}
        >
          {rebuild.isPending ? 'Rebuilding…' : 'Rebuild'}
        </button>
      </div>

      {status && <p className="dev-status">{status}</p>}
    </div>
  )
}

// ---- Similarity graph debugger (formerly the Related (dev) page) ----

// A submission of the form: the artist to query, whether to force a re-fetch, and a nonce so
// every "Fetch" (even for the same artist) re-runs the query — handy when debugging staleness.
interface Query {
  name: string
  refresh: boolean
  nonce: number
}

function SimilarityDebug() {
  const [input, setInput] = useState('Radiohead')
  const [refresh, setRefresh] = useState(false)
  const [query, setQuery] = useState<Query | null>(null)

  const { data, isFetching, isError, error } = useQuery({
    queryKey: ['related', query?.name, query?.refresh, query?.nonce],
    queryFn: () => getRelated(query!.name, query!.refresh),
    enabled: query !== null,
  })

  function run(name: string, force: boolean) {
    const trimmed = name.trim()
    if (!trimmed) return
    setInput(trimmed)
    setQuery({ name: trimmed, refresh: force, nonce: Date.now() })
  }

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    run(input, refresh)
  }

  return (
    <div className="dev-tool">
      <h2>Similarity graph</h2>
      <p>
        Hits <code>GET /related/{'{artist}'}</code> — ingests from every source (Deezer +
        ListenBrainz) on a cache miss / stale entry, persists the graph, then unifies across sources.
        Click a card to explore from it.
      </p>

      <form onSubmit={onSubmit} className="controls">
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Artist name"
          aria-label="Artist name"
        />
        <label style={{ display: 'flex', alignItems: 'center', gap: '0.35rem' }}>
          <input
            type="checkbox"
            checked={refresh}
            onChange={(e) => setRefresh(e.target.checked)}
          />
          Force refresh
        </label>
        <button type="submit" disabled={isFetching}>
          {isFetching ? 'Fetching…' : 'Fetch related'}
        </button>
      </form>

      {isError && <p className="error">{(error as Error).message}</p>}

      {data && (
        <>
          <p>
            <em>
              {data.related.length} related artist{data.related.length === 1 ? '' : 's'} for{' '}
              <strong>{data.artist.artistName}</strong>
            </em>
          </p>

          {data.related.length === 0 ? (
            <p>
              <em>No related artists found (Deezer had no match, or returned none).</em>
            </p>
          ) : (
            <div className="related-grid">
              {data.related.map((r) => (
                <div
                  className="related-card"
                  key={r.artistKey.artistName}
                  onClick={() => run(r.artistKey.artistName, false)}
                  title={`Explore ${r.artistKey.artistName}`}
                >
                  {r.imageUrl ? (
                    <img src={r.imageUrl} alt={r.artistKey.artistName} loading="lazy" />
                  ) : (
                    <div className="related-card-noimg">no image</div>
                  )}
                  <div className="related-card-name">{r.artistKey.artistName}</div>
                  <div className="related-card-sources">
                    {r.sources.map((s) => (
                      <span className="source-badge" key={s}>
                        {s}
                      </span>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}
