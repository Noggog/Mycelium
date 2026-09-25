import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthContext'
import {
  acceptArtist,
  getIdentityReport,
  getReconcileList,
  MUSICBRAINZ_ADD_ARTIST_URL,
  musicBrainzArtistUrl,
  musicBrainzSearchUrl,
  parseArtistMbid,
  RECONCILE_COUNT_KEY,
  recheckArtist,
  startIdentityPass,
  type ArtistResolution,
  type IdentityReport,
  type ResolutionCandidate,
} from '../api/identity'

// Which MusicBrainz artist each library artist is. Mycelium is moving to keying artists by MBID
// (MUSICBRAINZ-IDENTITY.md), and an artist it can't match won't survive that — so this is the list of
// the ones it couldn't settle on its own, with what it found, and the ways to settle them: accept a
// candidate, paste the right one, or go and add the artist to MusicBrainz and re-check.

const REPORT_KEY = ['dev', 'identity', 'report']
const LIST_KEY = ['dev', 'identity', 'reconcile']

export default function Reconcile() {
  const { user, isLoading } = useAuth()

  if (isLoading) {
    return (
      <section>
        <h1>Reconcile</h1>
        <p><em>…</em></p>
      </section>
    )
  }

  if (!user?.isDev) {
    return (
      <section>
        <h1>Reconcile</h1>
        <p><em>This page is for maintainers.</em></p>
      </section>
    )
  }

  return (
    <section>
      <h1>Reconcile</h1>
      <Summary />
      <AttentionList />
    </section>
  )
}

// ---- Where the library stands, and the pass that keeps it current ----

const TILES: { key: keyof IdentityReport; label: string; hint: string }[] = [
  { key: 'high', label: 'Settled', hint: 'the library’s own albums are on it' },
  { key: 'medium', label: 'Likely', hint: 'some evidence beyond the name, but thin' },
  { key: 'pinned', label: 'Pinned', hint: 'chosen by hand' },
  { key: 'low', label: 'Name only', hint: 'nothing but a matching name — needs a look' },
  { key: 'ambiguous', label: 'Several candidates', hint: 'more than one act fits equally well' },
  { key: 'mixed', label: 'Mixes acts', hint: 'one Plex artist holds albums by different acts — split it in Plex' },
  { key: 'missing', label: 'Not on MusicBrainz', hint: 'nobody by this name — add it there' },
  { key: 'unlinked', label: 'Detached', hint: 'unlinked from MusicBrainz by hand' },
  {
    key: 'disagreesWithCurrent',
    label: 'Differs from today',
    hint: 'resolved to a different act than the one linked now',
  },
  {
    key: 'sharedNames',
    label: 'Shared names',
    hint: 'names that cover several Plex artists — each is checked on its own albums',
  },
]

function Summary() {
  const queryClient = useQueryClient()
  const { data: report, error } = useQuery({
    queryKey: REPORT_KEY,
    queryFn: getIdentityReport,
    refetchInterval: (query) => (query.state.data?.pass.running ? 2000 : false),
  })

  const start = useMutation({
    mutationFn: startIdentityPass,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: REPORT_KEY }),
  })

  const pass = report?.pass
  const running = pass?.running ?? false

  // A pass that just finished changed the list and the badge too.
  const wasRunning = useRef(false)
  useEffect(() => {
    if (wasRunning.current && !running) {
      queryClient.invalidateQueries({ queryKey: LIST_KEY })
      queryClient.invalidateQueries({ queryKey: RECONCILE_COUNT_KEY })
    }
    wasRunning.current = running
  }, [running, queryClient])

  return (
    <div className="dev-tool">
      <h2>MusicBrainz identity</h2>
      <p>
        Every library artist is checked against MusicBrainz: its name, the MusicBrainz artist its
        Deezer page links to, and — what settles it — which candidate’s discography holds the albums
        you own. Nothing here changes an artist’s current link except Accept. Checks run daily at
        about one request a second; each artist is re-checked monthly.
      </p>

      {error && <p className="error">{(error as Error).message}</p>}

      {report && (
        <>
          <p className="dev-muted">
            {report.checked} of {report.libraryArtists} library artists checked ·{' '}
            {report.needsAttention} need a look
          </p>
          <dl className="takeout-counts">
            {TILES.map((t) => (
              <div className="takeout-count" key={t.key} title={t.hint}>
                <dt>{t.label}</dt>
                <dd>{report[t.key] as number}</dd>
              </div>
            ))}
          </dl>
        </>
      )}

      <div className="controls">
        <button onClick={() => start.mutate(false)} disabled={running || start.isPending}>
          {running ? 'Checking…' : 'Check new and due artists'}
        </button>
        <button
          onClick={() => {
            if (window.confirm('Re-check every library artist? That is hours at one request a second.')) {
              start.mutate(true)
            }
          }}
          disabled={running || start.isPending}
        >
          Re-check everything
        </button>
      </div>

      {start.isError && <p className="error">{(start.error as Error).message}</p>}

      {pass && (pass.running || pass.finishedAt) && (
        <p className="dev-status">
          {pass.running ? (
            <>
              Checked {pass.processed} / {pass.total}
              {pass.currentArtist ? ` — ${pass.currentArtist}` : ''}.
            </>
          ) : (
            <>✓ Last pass checked {pass.processed} artist(s).</>
          )}
          {pass.unreachable > 0 && ` ${pass.unreachable} unanswered (retried next pass).`}
          {pass.errors > 0 && ` ${pass.errors} error(s).`}
        </p>
      )}
    </div>
  )
}

// ---- The artists that need a person ----

const GROUPS: { title: string; blurb: string; test: (r: ArtistResolution) => boolean }[] = [
  {
    title: 'Library mixes several acts',
    blurb:
      'Albums by different acts are filed under one Plex artist. Split them in Plex (so each act is its own Plex artist), then re-check — don’t accept either.',
    test: (r) => r.status === 'Mixed',
  },
  {
    title: 'Several candidates',
    blurb: 'More than one MusicBrainz artist fits. Pick the right one.',
    test: (r) => r.status === 'Ambiguous',
  },
  {
    title: 'Name only',
    blurb: 'Matched on the name and nothing else, or on evidence that disagrees. Confirm or correct it.',
    test: (r) => r.status === 'Resolved',
  },
  {
    title: 'Not on MusicBrainz',
    blurb: 'Nobody by this name. Add the artist to MusicBrainz, then re-check.',
    test: (r) => r.status === 'Missing',
  },
  {
    title: 'Detached',
    blurb: 'Unlinked from MusicBrainz by hand. Link it once MusicBrainz has it.',
    test: (r) => r.status === 'Unlinked',
  },
]

// Anything the groups above don't claim. Should stay empty; it exists so a status the page doesn't
// know about shows up rather than vanishing from a list whose count says it's there.
const OTHER_GROUP = {
  title: 'Other',
  blurb: 'Not in any group above.',
  test: (r: ArtistResolution) => !GROUPS.some((g) => g.test(r)),
}

function AttentionList() {
  const [filter, setFilter] = useState('')
  const { data, error, isLoading } = useQuery({ queryKey: LIST_KEY, queryFn: getReconcileList })

  const visible = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    return (data ?? []).filter((r) => !needle || r.artist.toLowerCase().includes(needle))
  }, [data, filter])

  return (
    <div className="dev-tool">
      <h2>Needs a look {data ? `(${data.length})` : ''}</h2>

      <div className="controls">
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter artists" />
      </div>

      {isLoading && <p className="dev-status">Loading…</p>}
      {error && <p className="error">{(error as Error).message}</p>}
      {data && data.length === 0 && <p className="dev-muted">Nothing to reconcile.</p>}

      {[...GROUPS, OTHER_GROUP].map((g) => {
        const rows = visible.filter(g.test)
        if (rows.length === 0) return null
        return (
          <details className="reconcile-group" key={g.title} open>
            <summary>
              {g.title} <span className="dev-muted">({rows.length})</span>
            </summary>
            <p className="dev-muted">{g.blurb}</p>
            {rows.map((r) => (
              <ArtistRow key={r.id} resolution={r} />
            ))}
          </details>
        )
      })}
    </div>
  )
}

function ArtistRow({ resolution: r }: { resolution: ArtistResolution }) {
  const queryClient = useQueryClient()
  const [pasted, setPasted] = useState('')

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: LIST_KEY })
    queryClient.invalidateQueries({ queryKey: REPORT_KEY })
    queryClient.invalidateQueries({ queryKey: RECONCILE_COUNT_KEY })
  }

  const accept = useMutation({
    mutationFn: (mbid: string) => acceptArtist(r.artist, r.plexArtistKey, mbid),
    onSuccess: refresh,
  })
  const recheck = useMutation({
    mutationFn: () => recheckArtist(r.artist, r.plexArtistKey),
    onSuccess: refresh,
  })

  const pastedMbid = parseArtistMbid(pasted)
  const busy = accept.isPending || recheck.isPending
  const candidates = [...r.candidates].sort((a, b) => (b.albumOverlap ?? -1) - (a.albumOverlap ?? -1))
  const matchedAnywhere = new Set(r.candidates.flatMap((c) => c.matchedAlbums ?? []))
  const unmatched = (r.albums ?? []).filter((a) => !matchedAnywhere.has(a))
  const checked = r.candidates.some((c) => c.matchedAlbums !== null)

  return (
    <div className="reconcile-row">
      <div className="reconcile-head">
        <strong>{r.artist}</strong>
        {r.plexArtistKey !== null && (
          <span
            className="reconcile-chip"
            title="Several Plex artists share this name; this one is checked on its own albums"
          >
            Plex artist {r.plexArtistKey}
          </span>
        )}
        <span className="dev-muted">
          {r.ownedAlbums} album{r.ownedAlbums === 1 ? '' : 's'} in the library
        </span>
      </div>
      <p className="reconcile-reason">{r.reason}</p>
      {checked && unmatched.length > 0 && (
        <p className="reconcile-unmatched">
          <span className="dev-muted">Not on any candidate: </span>
          {unmatched.join(', ')}
        </p>
      )}

      {candidates.length > 0 && (
        <ul className="reconcile-candidates">
          {candidates.map((c) => (
            <CandidateRow
              key={c.mbid}
              candidate={c}
              owned={r.ownedAlbums}
              chosen={c.mbid === r.mbid}
              busy={busy}
              onAccept={() => accept.mutate(c.mbid)}
            />
          ))}
        </ul>
      )}

      <div className="controls reconcile-actions">
        <input
          value={pasted}
          onChange={(e) => setPasted(e.target.value)}
          placeholder="MusicBrainz artist URL or id"
        />
        <button onClick={() => pastedMbid && accept.mutate(pastedMbid)} disabled={!pastedMbid || busy}>
          Use this one
        </button>
        <button onClick={() => recheck.mutate()} disabled={busy} title="Ask MusicBrainz again now, skipping the cache">
          {recheck.isPending ? 'Checking…' : 'Re-check'}
        </button>
        <a href={musicBrainzSearchUrl(r.artist)} target="_blank" rel="noreferrer">
          Search MusicBrainz
        </a>
        {r.status === 'Missing' && (
          <a href={MUSICBRAINZ_ADD_ARTIST_URL} target="_blank" rel="noreferrer">
            Add to MusicBrainz
          </a>
        )}
      </div>

      {accept.isError && <p className="error">{(accept.error as Error).message}</p>}
      {recheck.isError && <p className="error">{(recheck.error as Error).message}</p>}
    </div>
  )
}

const EVIDENCE_LABELS: Record<string, string> = {
  name: 'name',
  alias: 'alias',
  deezer: 'Deezer link',
  current: 'linked today',
}

function CandidateRow({
  candidate: c,
  owned,
  chosen,
  busy,
  onAccept,
}: {
  candidate: ResolutionCandidate
  owned: number
  chosen: boolean
  busy: boolean
  onAccept: () => void
}) {
  return (
    <li className={chosen ? 'reconcile-candidate is-chosen' : 'reconcile-candidate'}>
      <a href={musicBrainzArtistUrl(c.mbid)} target="_blank" rel="noreferrer">
        {c.name ?? c.mbid}
      </a>
      {c.disambiguation && <span className="dev-muted"> ({c.disambiguation})</span>}
      <span className="reconcile-overlap">
        {c.albumOverlap === null ? 'not checked' : `${c.albumOverlap} of your ${owned}`}
        {c.releaseGroups !== null && ` · ${c.releaseGroups} release${c.releaseGroups === 1 ? '' : 's'} on MusicBrainz`}
      </span>
      <span className="reconcile-evidence">
        {c.evidence.map((e) => (
          <span className="reconcile-chip" key={e}>
            {EVIDENCE_LABELS[e] ?? e}
          </span>
        ))}
      </span>
      <button onClick={onAccept} disabled={busy}>
        Accept
      </button>
      {c.matchedAlbums && c.matchedAlbums.length > 0 && (
        <div className="reconcile-matched">
          <span className="dev-muted">On it: </span>
          {c.matchedAlbums.join(', ')}
        </div>
      )}
    </li>
  )
}
