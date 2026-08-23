import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getArtists } from '../api/artists'
import { clearSource, getArtistSources, pinSource, searchSource, unlinkSource } from '../api/sources'
import { getArtistLibraries } from '../api/library'
import { editArtistTag, getArtistTags } from '../api/tags'
import {
  blockAlbum,
  clearRating,
  getArtistDiscography,
  getRatings,
  rate,
  seedArtist,
  unblockAlbum,
  type Verdict,
} from '../api/discovery'
import { getRelated } from '../api/related'
import { useArtAccent } from '../art/artColors'
import { useDebounced } from '../hooks/useDebounced'
import { rateFeedback } from '../effects/effectsBus'
import type {
  ArtistAlbumItem,
  ArtistListItem,
  ArtistTags,
  DiscoveryStatus,
  FeedItem,
  SourceCandidate,
  SourceIdentity,
  TagField,
} from '../types'
import { useAuth } from '../auth/AuthContext'
import { DeezerSample } from '../components/DeezerSample'
import { MergeAlbumPane } from '../components/MergeAlbumPane'
import { PlexRatingStats } from '../components/PlexRatingStats'
import { IconApprove, IconBlock, IconCheck, IconClear, IconReject, IconWrench } from '../components/icons'

// The detail pane is driven by a lightweight selection: just enough to render the readout and to key
// the Albums / Related tab queries. A library row supplies the full ArtistListItem (looked up by name
// for the Deezer link, genres, fans, correction); a related-artist card the user drills into may not
// be in the library, so all we can carry is its name + photo — the tabs still work off the name.
type SelectedArtist = { name: string; imageUrl: string | null }
type DetailTab = 'albums' | 'related' | 'meta' | 'library' | 'tags'

// Human labels for the source keys the backend emits.
const SOURCE_LABELS: Record<string, string> = {
  deezer: 'Deezer',
  musicbrainz: 'MusicBrainz',
  listenbrainz: 'ListenBrainz',
}
const sourceLabel = (s: string) => SOURCE_LABELS[s] ?? s

const verdictStatus = (v: Verdict): DiscoveryStatus => (v === 'up' ? 'Liked' : 'Disliked')

// The full library loads in one fetch, but rendering every row at once is the costly part — each
// row extracts an accent colour from its photo — so we page the rendered rows. Search still spans
// the whole library (it filters before paging).
const PAGE_SIZE = 25

const normalize = (s: string) => s.trim().toLowerCase()

// A library row is "suspect" when it resolved to a Deezer artist whose name doesn't match — the
// tell-tale of a misassociation (e.g. library "ALEX" → Deezer "Alex Warren").
const isSuspect = (a: ArtistListItem) =>
  a.deezerId != null && a.deezerName != null && normalize(a.deezerName) !== normalize(a.artistKey.artistName)

function formatFans(n: number | null): string {
  if (n == null) return ''
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(n >= 10_000 ? 0 : 1)}k`
  return String(n)
}

// The "Meta" tab: every external metadata source's resolved identity for the selected library
// artist — its id, a link out to the source's page, the override flag — plus a per-source wrench
// (Correct) button for correctable sources (Deezer, MusicBrainz). ListenBrainz appears as a
// read-only link (its identity is just the MusicBrainz MBID).
function MetaTab({ artist }: { artist: string }) {
  const queryClient = useQueryClient()
  const [correcting, setCorrecting] = useState<SourceIdentity | null>(null)

  const { data, isPending, isError } = useQuery({
    queryKey: ['artist-sources', artist],
    queryFn: () => getArtistSources(artist),
  })

  // A pin/clear changes the resolved Deezer id, which in turn changes the discography (album list +
  // cover art) and re-derives similarity edges — refresh this tab, the discography, the artist list
  // (Deezer columns / suspect badge) and the downstream feeds. Without the discography invalidation a
  // relink left the old albums (and missing/stale covers) on screen until a hard refresh.
  const afterChange = () => {
    queryClient.invalidateQueries({ queryKey: ['artist-sources', artist] })
    queryClient.invalidateQueries({ queryKey: ['artist-discography', artist] })
    queryClient.invalidateQueries({ queryKey: ['artists'] })
    queryClient.invalidateQueries({ queryKey: ['feed'] })
    queryClient.invalidateQueries({ queryKey: ['related'] })
    setCorrecting(null)
  }

  if (isPending) return <div className="disc-sub-albums"><em className="disc-sub-note">Loading sources…</em></div>
  if (isError) return <div className="disc-sub-albums"><em className="disc-sub-note">Failed to load sources.</em></div>

  return (
    <div className="source-list">
      {data.sources.map((s) => (
        <div className="source-row" key={s.source}>
          <span className="source-badge">{sourceLabel(s.source)}</span>
          <div className="source-meta">
            {s.id ? (
              <>
                <span className="source-name">{s.name ?? '(unknown)'}</span>
                <span className="source-sub">
                  {s.detail ? `${s.detail} · ` : ''}
                  {s.id}
                  {s.isOverride ? ' · pinned' : ''}
                </span>
              </>
            ) : s.unlinked ? (
              <span className="source-sub"><em>Detached — won’t auto-resolve</em></span>
            ) : (
              <span className="source-sub"><em>Not resolved yet</em></span>
            )}
          </div>
          {s.link && (
            <a className="deezer-link" href={s.link} target="_blank" rel="noopener noreferrer">
              {sourceLabel(s.source)} ↗
            </a>
          )}
          {s.correctable && (
            <button
              className="disc-btn"
              title={`Correct ${sourceLabel(s.source)} association`}
              onClick={() => setCorrecting(s)}
            >
              <IconWrench size={16} />
            </button>
          )}
        </div>
      ))}

      {correcting && (
        <SourcePicker
          artist={artist}
          source={correcting}
          onClose={() => setCorrecting(null)}
          onApplied={afterChange}
        />
      )}
    </div>
  )
}

// Inline picker to pin the correct artist on one source. Searches that source (prefilled with the
// library name) and lets the user pick the right candidate. We surface each candidate's link rather
// than an audio preview, because verifying by name is exactly what's unreliable here.
function SourcePicker({
  artist,
  source,
  onClose,
  onApplied,
}: {
  artist: string
  source: SourceIdentity
  onClose: () => void
  onApplied: () => void
}) {
  const key = source.source
  const label = sourceLabel(key)
  const [query, setQuery] = useState(artist)
  // Search on a pause, not per keystroke — MusicBrainz allows one call a second, and typing a name
  // through would spend the whole budget answering prefixes nobody asked about.
  const searched = useDebounced(query.trim())

  const search = useQuery({
    queryKey: ['source-search', key, searched],
    queryFn: () => searchSource(key, searched),
    enabled: searched.length > 0,
  })

  const apply = useMutation({
    mutationFn: (id: string) => pinSource(key, artist, id),
    onSuccess: onApplied,
  })

  const reset = useMutation({
    mutationFn: () => clearSource(key, artist),
    onSuccess: onApplied,
  })

  const unlink = useMutation({
    mutationFn: () => unlinkSource(key, artist),
    onSuccess: onApplied,
  })

  const busy = apply.isPending || reset.isPending || unlink.isPending

  return (
    <div className="picker-backdrop" onClick={onClose}>
      <div className="picker-panel" onClick={(e) => e.stopPropagation()}>
        <div className="picker-head">
          <h2>Correct {label} for “{artist}”</h2>
          <button className="auth-btn" onClick={onClose}>Close</button>
        </div>
        <p>
          <em>Pick the right {label} artist — use the ↗ link to confirm before applying.</em>
        </p>

        <input
          className="picker-search"
          type="text"
          value={query}
          autoFocus
          placeholder={`Search ${label}…`}
          onChange={(e) => setQuery(e.target.value)}
        />

        {source.unlinked ? (
          <p className="picker-pinned">
            Detached — {label} has no match for this artist.{' '}
            <button className="link-btn" onClick={() => reset.mutate()} disabled={busy}>
              Re-enable automatic resolution
            </button>
          </p>
        ) : (
          <p className="picker-pinned">
            {source.isOverride
              ? `Pinned to ${label} ${source.id}. `
              : source.id
                ? `Auto-linked to ${label} ${source.id}. `
                : `Not linked to ${label} yet. `}
            {source.isOverride && (
              <>
                <button className="link-btn" onClick={() => reset.mutate()} disabled={busy}>
                  Reset to automatic
                </button>{' '}
              </>
            )}
            <button className="link-btn" onClick={() => unlink.mutate()} disabled={busy}>
              Unlink — no match on {label}
            </button>
          </p>
        )}

        {search.isPending && query.trim() && <p><em>Searching…</em></p>}
        {search.isError && <p className="error">Search failed.</p>}

        <ul className="picker-results">
          {(search.data ?? []).map((c: SourceCandidate) => {
            const current = c.id === source.id
            return (
              <li key={c.id} className={current ? 'picker-result current' : 'picker-result'}>
                {c.imageUrl ? (
                  <img className="picker-thumb" src={c.imageUrl} alt="" />
                ) : (
                  <div className="picker-thumb placeholder" />
                )}
                <div className="picker-meta">
                  <span className="picker-name">{c.name ?? '(unknown)'}</span>
                  <span className="picker-sub">
                    {c.detail ? `${c.detail} · ` : ''}
                    {c.id}
                    {current && ' · current'}
                  </span>
                </div>
                {c.link && (
                  <a className="deezer-link" href={c.link} target="_blank" rel="noopener noreferrer">
                    {label} ↗
                  </a>
                )}
                <button
                  className="auth-btn"
                  disabled={busy || current}
                  onClick={() => apply.mutate(c.id)}
                >
                  {current ? 'In use' : 'Use this'}
                </button>
              </li>
            )
          })}
          {search.data && search.data.length === 0 && query.trim() && (
            <li><em>No {label} matches.</em></li>
          )}
        </ul>

        {apply.isError && <p className="error">Failed to apply: {(apply.error as Error).message}</p>}
        {unlink.isError && <p className="error">Failed to unlink: {(unlink.error as Error).message}</p>}
        {reset.isError && <p className="error">Failed to reset: {(reset.error as Error).message}</p>}
      </div>
    </div>
  )
}

// Compact deep links to open the selected artist where it lives (Plex now, Navidrome later), shown
// inline in the readout header — the same per-library links as the Library tab, surfaced without a
// tab switch (mirrors the Discover readout's "In your library" links).
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

// The "Library" tab: where the selected artist lives in the user's media servers (Plex now,
// Navidrome eventually), with a deep link to open the artist there. Reuses the source-row styling.
function LibraryTab({ artist }: { artist: string }) {
  const { data, isPending, isError } = useQuery({
    queryKey: ['artist-libraries', artist],
    queryFn: () => getArtistLibraries(artist),
  })

  if (isPending) return <div className="disc-sub-albums"><em className="disc-sub-note">Loading libraries…</em></div>
  if (isError) return <div className="disc-sub-albums"><em className="disc-sub-note">Failed to load libraries.</em></div>

  return (
    <div className="source-list">
      {data.sources.map((s) => (
        <div className="source-row" key={s.source}>
          <span className="source-badge">{s.label}</span>
          <div className="source-meta">
            {s.present ? (
              <span className="source-sub">In this library</span>
            ) : (
              <span className="source-sub"><em>Not in this library</em></span>
            )}
          </div>
          {s.links.map((l) => (
            <a className="deezer-link" key={l.url} href={l.url} target="_blank" rel="noopener noreferrer">
              {l.label} ↗
            </a>
          ))}
        </div>
      ))}
    </div>
  )
}

// One editable tag field (Genres / Styles / Moods) in the Tags tab: the artist's current tags as
// removable chips plus an inline add box. Each edit is its own delta write to Plex, so a slow or failed
// one never takes the rest of the field with it.
function TagGroup({
  label,
  field,
  tags,
  placeholder,
  onEdit,
  busy,
}: {
  label: string
  field: TagField
  tags: string[]
  placeholder: string
  onEdit: (field: TagField, edit: { add?: string; remove?: string }) => void
  busy: boolean
}) {
  const [draft, setDraft] = useState('')

  const submit = () => {
    const value = draft.trim()
    if (!value) return
    // Adding a tag the artist already has is a no-op server-side; drop it here so the box still clears.
    if (!tags.some((t) => normalize(t) === normalize(value))) {
      onEdit(field, { add: value })
    }
    setDraft('')
  }

  return (
    <div className="tag-group">
      <div className="detail-section-label">{label}</div>
      <div className="tag-chips">
        {tags.map((t) => (
          <span className="tag-chip" key={t}>
            {t}
            <button
              className="tag-chip-x"
              title={`Remove ${t}`}
              disabled={busy}
              onClick={() => onEdit(field, { remove: t })}
            >
              ✕
            </button>
          </span>
        ))}
        {tags.length === 0 && <em className="disc-sub-note">None</em>}
      </div>
      <div className="tag-add">
        <input
          className="tag-input"
          type="text"
          value={draft}
          placeholder={placeholder}
          disabled={busy}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              submit()
            }
          }}
        />
        <button className="auth-btn" disabled={busy || !draft.trim()} onClick={submit}>
          Add
        </button>
      </div>
    </div>
  )
}

// The "Tags" tab: edit the descriptor tags the artist carries in Plex — genres, styles and moods —
// the same fields Plex smart collections filter on. The app's own "<user>_liked"/"_disliked" verdict
// moods are stripped by the backend and can't be added or removed here: those are rating state, owned
// by the thumbs in the header, and showing them would offer a second, desyncable way to change a rating.
function TagsTab({ artist }: { artist: string }) {
  const queryClient = useQueryClient()

  const { data, isPending, isError } = useQuery({
    queryKey: ['artist-tags', artist],
    queryFn: () => getArtistTags(artist),
  })

  const edit = useMutation({
    mutationFn: ({ field, ...rest }: { field: TagField; add?: string; remove?: string }) =>
      editArtistTag(artist, field, rest),
    onSuccess: (updated: ArtistTags, vars) => {
      // The endpoint returns the artist's tags as they now stand — take them straight, no refetch.
      queryClient.setQueryData(['artist-tags', artist], updated)
      // The artist list (and the readout header chips) render genres off the catalog, which the
      // backend mirrors on a genre edit — pull the row again so the change shows without a reload.
      if (vars.field === 'genre') {
        queryClient.invalidateQueries({ queryKey: ['artists'] })
      }
    },
  })

  if (isPending) return <div className="disc-sub-albums"><em className="disc-sub-note">Loading tags…</em></div>
  if (isError) return <div className="disc-sub-albums"><em className="disc-sub-note">Failed to load tags.</em></div>
  if (!data.present) {
    return (
      <div className="disc-sub-albums">
        <em className="disc-sub-note">This artist isn’t in your Plex library, so there’s nothing to tag.</em>
      </div>
    )
  }

  const onEdit = (field: TagField, e: { add?: string; remove?: string }) => edit.mutate({ field, ...e })

  return (
    <div className="tag-editor">
      <TagGroup
        label="Genres" field="genre" tags={data.genres} placeholder="Add a genre…"
        onEdit={onEdit} busy={edit.isPending}
      />
      <TagGroup
        label="Styles" field="style" tags={data.styles} placeholder="Add a style…"
        onEdit={onEdit} busy={edit.isPending}
      />
      <TagGroup
        label="Moods" field="mood" tags={data.moods} placeholder="Add a mood…"
        onEdit={onEdit} busy={edit.isPending}
      />
      {edit.isError && <p className="error">Tag edit failed: {(edit.error as Error).message}</p>}
    </div>
  )
}

// Album art (or a coloured initial) for an album in the discography drill-down.
function AlbumThumb({ item }: { item: ArtistAlbumItem }) {
  if (item.imageUrl) {
    return <img className="disc-avatar" src={item.imageUrl} alt="" width={36} height={36} loading="lazy" />
  }
  return (
    <div className="disc-avatar disc-avatar-fallback" style={{ width: 36, height: 36, fontSize: 15 }}>
      {item.album.charAt(0).toUpperCase()}
    </div>
  )
}

// A decided missing album (queued / meh / snoozed, or blocked for everyone) with a one-click clear
// back to actionable.
function AlbumState({
  label,
  onClear,
  busy,
  clearTitle = 'Clear — return to choices',
}: {
  label: string
  onClear: () => void
  busy: boolean
  clearTitle?: string
}) {
  return (
    <span className="album-state">
      {label}
      <button className="disc-btn" title={clearTitle} disabled={busy} onClick={onClear}>
        <IconClear size={15} />
      </button>
    </span>
  )
}

// Verdict → the label shown on a decided missing album. "Meh" is a purely personal pass: it hides
// the album from your feed for good and leaves it offerable to every other user (a globally blocked
// album shows as "Blocked" instead — see the `blocked` flag).
const ALBUM_VERDICT_LABEL: Partial<Record<DiscoveryStatus, string>> = {
  Liked: 'Queued',
  Disliked: 'Meh',
  Snoozed: 'Snoozed',
}

// Deezer's record_type → the discography section a release is filed under, in display order. The
// drill-down lists every type but the Discover feed only carries albums and EPs, so the section is
// what marks a row as browse-only — and why a 3-track release like Ben Howard's "Another Friday
// Night / Hot Heavy Summer / Sister" sits under Singles instead of being mistaken for an LP.
// Anything else (including the null type an owned-only album Deezer doesn't list carries) falls to
// the trailing "Other" section.
const ALBUM_SECTIONS: { key: string; title: string }[] = [
  { key: 'album', title: 'Albums' },
  { key: 'ep', title: 'EPs' },
  { key: 'single', title: 'Singles' },
  { key: 'compilation', title: 'Compilations' },
  { key: 'other', title: 'Other' },
]

// Which section a release belongs to — its record type when we recognise it, "other" otherwise.
function albumSectionKey(a: ArtistAlbumItem): string {
  const type = a.recordType?.toLowerCase()
  return type && ALBUM_SECTIONS.some((s) => s.key === type) ? type : 'other'
}

// Newest first within a section. Deezer leaves the year off owned-only albums (and the odd release
// with no date), and an undated entry can't claim a spot in the timeline — sink those to the bottom
// and break the tie on title so the order is stable across refetches.
function byYearDesc(x: ArtistAlbumItem, y: ArtistAlbumItem): number {
  if (x.year !== y.year) {
    if (x.year == null) return 1
    if (y.year == null) return -1
    return y.year - x.year
  }
  return x.album.localeCompare(y.album)
}

// A single album in the discography drill-down, themed from its cover art via `--art-accent` (the
// shared `.disc-sub-album` styling turns that into the tinted card + the cover's glow). When the album
// has a Deezer id, the whole row toggles a 30-second track-preview player below it (like Discover); the
// action cluster stops the click so a thumb doesn't also open/close the preview.
function AlbumSubRow({
  a,
  busy,
  isOpen,
  onToggle,
  onRate,
  onClear,
  onMerge,
  onBlock,
  onUnblock,
}: {
  a: ArtistAlbumItem
  busy: boolean
  isOpen: boolean
  onToggle: () => void
  onRate: (a: ArtistAlbumItem, verdict: Verdict) => void
  onClear: (a: ArtistAlbumItem) => void
  onMerge: (a: ArtistAlbumItem) => void
  onBlock: (a: ArtistAlbumItem) => void
  onUnblock: (a: ArtistAlbumItem) => void
}) {
  const accent = useArtAccent(a.imageUrl)
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  const label = a.verdict ? ALBUM_VERDICT_LABEL[a.verdict] : null
  const canPlay = a.deezerAlbumId != null
  return (
    <div className="disc-sub-album-wrap">
      <div
        className={`disc-sub-album${isOpen ? ' selected' : ''}${canPlay ? '' : ' no-play'}${a.owned ? ' owned' : ''}`}
        style={accentStyle}
        onClick={canPlay ? onToggle : undefined}
      >
        <AlbumThumb item={a} />
        <div className="disc-sub-album-name">
          {a.album}
          {a.year && <span className="album-year">{a.year}</span>}
        </div>
        <div className="disc-actions" onClick={(e) => e.stopPropagation()}>
          {a.owned ? (
            <span className="album-owned" title="Already in your library">
              <IconCheck size={15} /> In library
            </span>
          ) : a.blocked ? (
            // Blocked for everyone — it's filtered out of all the feeds, and shown here (the one place
            // a block is reviewable) purely so it can be lifted again.
            <AlbumState
              label="Blocked"
              busy={busy}
              clearTitle="Unblock — return this album to everyone's feeds"
              onClear={() => onUnblock(a)}
            />
          ) : (
            <>
              {label ? (
                <AlbumState label={label} busy={busy} onClear={() => onClear(a)} />
              ) : (
                <>
                  <button
                    className="disc-btn up"
                    title="Queue album to buy"
                    disabled={busy}
                    onClick={() => onRate(a, 'up')}
                  >
                    <IconApprove />
                  </button>
                  <button
                    className="disc-btn down"
                    title="Meh — hide this from my feed only"
                    disabled={busy}
                    onClick={() => onRate(a, 'down')}
                  >
                    <IconReject />
                  </button>
                </>
              )}
              {/* The copy we already have is filed under a near-miss title (or a different act), so
                  the diff can't see it. Merge the two before the downloader grabs a duplicate. */}
              <button
                className="disc-btn"
                title="Already in library — match an album you own"
                disabled={busy}
                onClick={() => onMerge(a)}
              >
                <IconWrench size={15} />
              </button>
              {/* Escalation from the personal "meh": takes the album off every user's feed. */}
              <button
                className="disc-btn block"
                title="Block for everyone — no one gets offered this album"
                disabled={busy}
                onClick={() => onBlock(a)}
              >
                <IconBlock size={15} />
              </button>
            </>
          )}
        </div>
      </div>
      {isOpen && a.deezerAlbumId != null && <DeezerSample albumId={a.deezerAlbumId} />}
    </div>
  )
}

// The readout's Albums tab: the selected artist's full Deezer discography, owned albums flagged and
// missing ones thumbable so they can be queued to buy (or dismissed) right here — no trip through
// Discover. Fetched on demand (one Deezer call) only when the Albums tab is shown for an artist.
function ArtistAlbums({ artist }: { artist: string }) {
  const queryClient = useQueryClient()
  // Which album's Deezer preview is expanded — one at a time, like selecting a row in Discover.
  const [openAlbum, setOpenAlbum] = useState<string | null>(null)
  // The album whose "Already in library?" pane is open, if any.
  const [merging, setMerging] = useState<ArtistAlbumItem | null>(null)
  const { data, isPending, isError } = useQuery({
    queryKey: ['artist-discography', artist],
    queryFn: () => getArtistDiscography(artist),
    staleTime: 5 * 60 * 1000,
  })

  // Albums / EPs / Singles / Compilations as their own headed sections, newest release first inside
  // each — the type is the section it sits under, so the rows themselves carry no type badge. Empty
  // sections are dropped (most artists have no compilations, and "Other" is usually empty too).
  const sections = useMemo(() => {
    if (!data) return []
    return ALBUM_SECTIONS.map((s) => ({
      ...s,
      items: data.filter((a) => albumSectionKey(a) === s.key).sort(byYearDesc),
    })).filter((s) => s.items.length > 0)
  }, [data])

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['artist-discography', artist] })
    queryClient.invalidateQueries({ queryKey: ['purchases'] })
    queryClient.invalidateQueries({ queryKey: ['ratings'] })
  }

  // rate/clearRating only read artist/album/imageUrl/deezerAlbumId — build the minimal feed item.
  const toFeedItem = (a: ArtistAlbumItem): FeedItem => ({
    kind: 'MissingAlbum',
    artist: a.artist,
    album: a.album,
    imageUrl: a.imageUrl,
    score: 0,
    sources: [],
    deezerAlbumId: a.deezerAlbumId,
    year: a.year,
    reconsider: null,
  })

  const rateAlbum = useMutation({
    mutationFn: ({ a, verdict }: { a: ArtistAlbumItem; verdict: Verdict }) => rate(toFeedItem(a), verdict),
    onMutate: ({ verdict }) => rateFeedback(verdict),
    onSuccess: invalidate,
  })
  const clearAlbum = useMutation({
    mutationFn: (a: ArtistAlbumItem) => clearRating(toFeedItem(a)),
    onSuccess: invalidate,
  })
  // Blocking/unblocking changes what *every* user is offered, so it also busts the feed cache — not
  // just this artist's discography.
  const setBlocked = useMutation({
    mutationFn: ({ a, blocked }: { a: ArtistAlbumItem; blocked: boolean }) =>
      blocked
        ? blockAlbum(a.artist.artistName, a.album)
        : unblockAlbum(a.artist.artistName, a.album),
    onSuccess: () => {
      invalidate()
      queryClient.invalidateQueries({ queryKey: ['feed'] })
    },
  })
  const busy = rateAlbum.isPending || clearAlbum.isPending || setBlocked.isPending

  if (isPending) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">Loading albums…</em></div>
  }
  if (isError || !data) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">Couldn’t load albums.</em></div>
  }
  if (data.length === 0) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">No albums found on Deezer.</em></div>
  }

  return (
    <div className="disc-sub-albums">
      {sections.map((s) => (
        <section className="album-section" key={s.key}>
          <h4 className="album-section-title">
            {s.title}
            <span className="album-section-count">{s.items.length}</span>
          </h4>
          {s.items.map((a) => (
            <AlbumSubRow
              key={a.album}
              a={a}
              busy={busy}
              isOpen={openAlbum === a.album}
              onToggle={() => setOpenAlbum((cur) => (cur === a.album ? null : a.album))}
              onRate={(album, verdict) => rateAlbum.mutate({ a: album, verdict })}
              onClear={(album) => clearAlbum.mutate(album)}
              onMerge={setMerging}
              onBlock={(album) => setBlocked.mutate({ a: album, blocked: true })}
              onUnblock={(album) => setBlocked.mutate({ a: album, blocked: false })}
            />
          ))}
        </section>
      ))}

      {merging && (
        <MergeAlbumPane
          artist={merging.artist.artistName}
          album={merging.album}
          onClose={() => setMerging(null)}
          onMerged={() => {
            setMerging(null)
            // The album now reads as owned here, is gone from the feed, and off the download queue.
            invalidate()
            queryClient.invalidateQueries({ queryKey: ['feed'] })
          }}
        />
      )}
    </div>
  )
}

// Artist photo (or a coloured initial), shared by the list rows and the detail hero. `hero` drops the
// inline size so CSS (.detail-hero) drives the large readout image.
function ArtistAvatar({ name, image, size, hero }: { name: string; image: string | null; size?: number; hero?: boolean }) {
  if (image) {
    return <img className="disc-avatar" src={image} alt={name} width={hero ? undefined : size} height={hero ? undefined : size} loading="lazy" />
  }
  return (
    <div className="disc-avatar disc-avatar-fallback" style={hero ? undefined : { width: size, height: size, fontSize: (size ?? 40) / 2.5 }}>
      {name.charAt(0).toUpperCase()}
    </div>
  )
}

// One artist in the left-hand list, themed from its photo via `--art-accent` (matching the Discover
// feed). Clicking the row opens it in the readout; the rate cluster (signed-in only) stops the click
// so a thumb doesn't also re-select the row.
function ArtistListRow({
  artist,
  verdict,
  selected,
  user,
  ratePending,
  onSelect,
  onRate,
}: {
  artist: ArtistListItem
  verdict: DiscoveryStatus | undefined
  selected: boolean
  user: boolean
  ratePending: boolean
  onSelect: (artist: ArtistListItem) => void
  onRate: (name: string, verdict: Verdict, current?: DiscoveryStatus) => void
}) {
  const name = artist.artistKey.artistName
  const suspect = isSuspect(artist)
  const accent = useArtAccent(artist.artistImageUrl)
  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  return (
    <div className={selected ? 'disc-row selected' : 'disc-row'} style={accentStyle} onClick={() => onSelect(artist)}>
      <ArtistAvatar name={name} image={artist.artistImageUrl} size={52} />
      <div className="disc-row-main">
        <div className="disc-name">
          {name}
          {suspect && (
            <span className="warn-badge" title="Deezer name doesn't match — likely the wrong artist"> ⚠</span>
          )}
        </div>
        {artist.genres.length > 0 && (
          <div className="genre-tags">
            {artist.genres.slice(0, 3).map((g) => (
              <span className="genre-tag" key={g}>{g}</span>
            ))}
          </div>
        )}
      </div>
      {user && (
        <div className="disc-actions" onClick={(e) => e.stopPropagation()}>
          <button
            className={verdict === 'Liked' ? 'disc-btn up active' : 'disc-btn up'}
            title={verdict === 'Liked' ? 'Clear rating' : 'Approve'}
            disabled={ratePending}
            onClick={() => onRate(name, 'up', verdict)}
          >
            <IconApprove />
          </button>
          <button
            className={verdict === 'Disliked' ? 'disc-btn down active' : 'disc-btn down'}
            title={verdict === 'Disliked' ? 'Clear rating' : 'Reject'}
            disabled={ratePending}
            onClick={() => onRate(name, 'down', verdict)}
          >
            <IconReject />
          </button>
        </div>
      )}
    </div>
  )
}

// The "Related" tab: the artists that stem from the selected one, unified across similarity sources
// (the same /related graph the Discover feed is built from). Each card drills the readout into that
// artist so you can walk the graph; a library artist lands on its full readout, a stranger on a
// lighter one. Fetched on demand only when the tab is open.
function RelatedTab({ artist, onExplore }: { artist: string; onExplore: (sel: SelectedArtist) => void }) {
  const { data, isPending, isError } = useQuery({
    queryKey: ['related', artist],
    queryFn: () => getRelated(artist),
    staleTime: 5 * 60 * 1000,
  })

  if (isPending) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">Finding related artists…</em></div>
  }
  if (isError || !data) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">Couldn’t load related artists.</em></div>
  }
  if (data.related.length === 0) {
    return <div className="disc-sub-albums"><em className="disc-sub-note">No related artists found on Deezer.</em></div>
  }

  return (
    <div className="related-grid artist-related-grid">
      {data.related.map((r) => {
        const rname = r.artistKey.artistName
        return (
          <div
            className="related-card"
            key={rname}
            onClick={() => onExplore({ name: rname, imageUrl: r.imageUrl })}
            title={`Explore ${rname}`}
          >
            {r.imageUrl ? (
              <img src={r.imageUrl} alt={rname} loading="lazy" />
            ) : (
              <div className="related-card-noimg">no image</div>
            )}
            <div className="related-card-name">{rname}</div>
            {r.sources.length > 0 && (
              <div className="related-card-sources">
                {r.sources.map((s) => (
                  <span className="source-badge" key={s}>{s}</span>
                ))}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}

// The right-hand readout for the artist selected in the list (desktop) / a bottom drawer (mobile): a
// big hero, the Deezer link-out / fans / genres, the rate + correct actions, and a tab strip whose
// panels are the artist's albums (discography drill-down) and the artists related to them.
function DetailPane({
  selected,
  libItem,
  verdict,
  user,
  tab,
  ratePending,
  onTab,
  onRate,
  onExplore,
  onClose,
}: {
  selected: SelectedArtist | null
  libItem: ArtistListItem | undefined
  verdict: DiscoveryStatus | undefined
  user: boolean
  tab: DetailTab
  ratePending: boolean
  onTab: (tab: DetailTab) => void
  onRate: (name: string, verdict: Verdict, current?: DiscoveryStatus) => void
  onExplore: (sel: SelectedArtist) => void
  onClose: () => void
}) {
  // Resolve art + accent unconditionally (hooks run before the empty-state early return). Prefer the
  // library photo when the selection is an owned artist, falling back to whatever the card carried.
  const image = libItem?.artistImageUrl ?? selected?.imageUrl ?? null
  const accent = useArtAccent(image)
  if (!selected) {
    return (
      <aside className="disc-detail is-empty">
        <div className="disc-detail-empty">
          <span className="detail-empty-icon">🎧</span>
        </div>
      </aside>
    )
  }

  const accentStyle = accent ? ({ '--art-accent': accent } as CSSProperties) : undefined
  const name = selected.name
  const suspect = !!libItem && isSuspect(libItem)
  const deezerHref =
    libItem?.deezerLink ?? (libItem?.deezerId != null ? `https://www.deezer.com/artist/${libItem.deezerId}` : null)

  return (
    <aside className="disc-detail" style={accentStyle}>
      <button className="detail-close" title="Close" onClick={onClose}>✕</button>

      <div className="detail-header">
        <div className="detail-hero">
          <ArtistAvatar name={name} image={image} hero />
        </div>

        <div className="detail-headinfo">
          {!libItem && <span className="detail-chip">Not in your library</span>}
          <h2 className="detail-name">
            {deezerHref ? (
              <a
                className="artist-name-link"
                href={deezerHref}
                target="_blank"
                rel="noopener noreferrer"
                title={suspect && libItem?.deezerName ? `Deezer: ${libItem.deezerName} — likely the wrong artist` : libItem?.deezerName ?? undefined}
              >
                {name}
              </a>
            ) : (
              name
            )}
            {suspect && <span className="warn-badge" title="Deezer name doesn't match — likely the wrong artist"> ⚠</span>}
          </h2>

          {libItem?.deezerFans != null && (
            <div className="detail-meta">{formatFans(libItem.deezerFans)} fans on Deezer</div>
          )}

          {libItem && libItem.genres.length > 0 && (
            <div className="detail-chips">
              {libItem.genres.slice(0, 6).map((g) => (
                <span className="detail-chip" key={g}>{g}</span>
              ))}
            </div>
          )}

          {/* Thumbs work for any selected artist: an owned one records a library rating, a related
              stranger drilled into from the Related tab gets liked straight into the buy list.
              Source corrections live in the Sources tab (library artists only). */}
          {user && (
            <div className="detail-actions">
              <button
                className={verdict === 'Liked' ? 'disc-btn up active' : 'disc-btn up'}
                title={verdict === 'Liked' ? 'Clear rating' : 'Approve'}
                disabled={ratePending}
                onClick={() => onRate(name, 'up', verdict)}
              >
                <IconApprove />
              </button>
              <button
                className={verdict === 'Disliked' ? 'disc-btn down active' : 'disc-btn down'}
                title={verdict === 'Disliked' ? 'Clear rating' : 'Reject'}
                disabled={ratePending}
                onClick={() => onRate(name, 'down', verdict)}
              >
                <IconReject />
              </button>
            </div>
          )}

          {/* Open the owned artist where it lives (Plex / Navidrome), inline under the rate buttons —
              the same compact links as the Library tab, surfaced without a tab switch. */}
          {libItem && <LibraryLinks artist={name} />}
        </div>

        {/* The user's song ratings for this artist, pinned to the right of the art. Library artists
            only — Plex has no songs (so no ratings) for a not-yet-owned recommendation. */}
        {libItem && <PlexRatingStats artist={name} />}
      </div>

      {/* Sample the artist's top tracks (30s Deezer previews) right in the readout, like Discover.
          The whole pane is keyed by selected artist (see the DetailPane render), so it remounts on
          selection change — no inner key needed (and an inner key={name} here collides with the
          albums/related panel's, which is the same name, since they're siblings under <aside>). */}
      <DeezerSample artist={name} />

      <div className="artist-detail-tabs" role="tablist">
        <button
          role="tab"
          aria-selected={tab === 'albums'}
          className={tab === 'albums' ? 'artist-tab active' : 'artist-tab'}
          onClick={() => onTab('albums')}
        >
          Albums
        </button>
        <button
          role="tab"
          aria-selected={tab === 'related'}
          className={tab === 'related' ? 'artist-tab active' : 'artist-tab'}
          onClick={() => onTab('related')}
        >
          Related artists
        </button>
        {/* Meta applies to any artist, library or not: which Deezer/MusicBrainz act a name attaches
            to is what drives the sample player, the discography and the related expansion, and it is
            most worth correcting *before* the artist is owned. Library (Plex/Navidrome presence) and
            Tags read Plex, so those stay library-only. */}
        <button
          role="tab"
          aria-selected={tab === 'meta'}
          className={tab === 'meta' ? 'artist-tab active' : 'artist-tab'}
          onClick={() => onTab('meta')}
        >
          Meta
        </button>
        {libItem && (
          <button
            role="tab"
            aria-selected={tab === 'library'}
            className={tab === 'library' ? 'artist-tab active' : 'artist-tab'}
            onClick={() => onTab('library')}
          >
            Library
          </button>
        )}
        {libItem && (
          <button
            role="tab"
            aria-selected={tab === 'tags'}
            className={tab === 'tags' ? 'artist-tab active' : 'artist-tab'}
            onClick={() => onTab('tags')}
          >
            Tags
          </button>
        )}
      </div>

      {tab === 'meta' ? (
        user ? (
          <MetaTab artist={name} />
        ) : (
          <div className="disc-sub-albums"><em className="disc-sub-note">Log in to view this artist’s metadata sources.</em></div>
        )
      ) : tab === 'library' ? (
        libItem ? (
          user ? (
            <LibraryTab artist={name} />
          ) : (
            <div className="disc-sub-albums"><em className="disc-sub-note">Log in to view this artist’s libraries.</em></div>
          )
        ) : (
          <div className="disc-sub-albums"><em className="disc-sub-note">Library links apply to library artists.</em></div>
        )
      ) : tab === 'tags' ? (
        libItem ? (
          user ? (
            <TagsTab artist={name} />
          ) : (
            <div className="disc-sub-albums"><em className="disc-sub-note">Log in to edit this artist’s tags.</em></div>
          )
        ) : (
          <div className="disc-sub-albums"><em className="disc-sub-note">Tags apply to library artists.</em></div>
        )
      ) : tab === 'albums' ? (
        user ? (
          // The whole pane is keyed by selected artist, so the discography refetches/remounts cleanly
          // on selection change — no inner key needed.
          <ArtistAlbums artist={name} />
        ) : (
          <div className="disc-sub-albums"><em className="disc-sub-note">Log in to view this artist’s albums.</em></div>
        )
      ) : (
        <RelatedTab artist={name} onExplore={onExplore} />
      )}
    </aside>
  )
}

// Ad-hoc discovery of artists that aren't in the library and that nothing recommends yet (so they
// never reach the feed). Always shown below the library matches. Searches Deezer live, drops hits in the
// library (those show in the list above), and lets each be added with one thumb — pinning that exact
// Deezer artist and liking it, which seeds it into discovery + the buy list. Clicking a row opens it
// in the readout first, to sample before adding.
function UncatalogedResults({
  query,
  libraryNames,
  verdictByArtist,
  selectedName,
  onSelect,
}: {
  query: string
  libraryNames: Set<string>
  verdictByArtist: Map<string, DiscoveryStatus>
  selectedName: string | null
  onSelect: (sel: SelectedArtist) => void
}) {
  const queryClient = useQueryClient()
  const trimmed = query.trim()
  // The library list above filters as you type; this searches Deezer, so it waits for a pause. Typing
  // a full name straight through was ~20 live searches in a couple of seconds — enough to trip
  // Deezer's rate limit, which answers with an error that reads as "no such artist" and leaves the
  // artist you just typed missing from these results.
  const searched = useDebounced(trimmed)

  const search = useQuery({
    queryKey: ['deezer-search', searched],
    queryFn: () => searchSource('deezer', searched),
    enabled: searched.length > 0,
    staleTime: 5 * 60 * 1000,
  })

  const seed = useMutation({
    mutationFn: (c: SourceCandidate) => seedArtist('deezer', c.id, c.name ?? ''),
    onMutate: () => rateFeedback('up'),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ratings'] })
      queryClient.invalidateQueries({ queryKey: ['feed'] })
      queryClient.invalidateQueries({ queryKey: ['purchases'] })
    },
  })

  // Only artists not already in the library — same-name hits are already listed above.
  const matches = (search.data ?? []).filter(
    (c) => c.name && !libraryNames.has(normalize(c.name)),
  )

  // Deezer lists several artists under one name (a "Feist" with 25 fans next to the real one with
  // 200k), and every row here selects by name alone — so duplicates open the same readout and are
  // pure noise. Collapse each name to its most-followed entry, the same tie-break the backend's
  // PickBestMatch uses to resolve a name, so the row's "Add" pins what the readout is already
  // playing. A wrong guess is re-routed from the readout's Meta tab, which lists every candidate.
  const best = new Map<string, SourceCandidate>()
  for (const c of matches) {
    const key = normalize(c.name as string)
    const held = best.get(key)
    if (!held || (c.popularity ?? 0) > (held.popularity ?? 0)) best.set(key, c)
  }
  // Keep Deezer's relevance order: dedupe by first appearance of each name, not by fan count.
  const results = [...new Set(matches.map((c) => normalize(c.name as string)))].map(
    (key) => best.get(key) as SourceCandidate,
  )

  if (trimmed.length === 0) return null

  // Mid-debounce the results below still belong to the previous query, so read as busy until the
  // search catches up — otherwise a half-typed name flashes "No new artists on Deezer".
  const searching = search.isFetching || searched !== trimmed

  return (
    <div className="uncataloged-results">
      <div className="uncataloged-head">
        Not in your library
        {searching && <span className="artist-search-count">searching Deezer…</span>}
      </div>

      {search.isError && <p className="disc-sub-note"><em>Deezer search failed.</em></p>}

      {!searching && results.length === 0 && (
        <p className="disc-sub-note"><em>No new artists on Deezer for “{searched}”.</em></p>
      )}

      <div className="disc-list">
        {results.map((c) => {
          const name = c.name as string
          const added = verdictByArtist.get(name) === 'Liked'
          return (
            <div
              className={selectedName === name ? 'disc-row selected' : 'disc-row'}
              key={c.id}
              onClick={() => onSelect({ name, imageUrl: c.imageUrl })}
            >
              <ArtistAvatar name={name} image={c.imageUrl} size={52} />
              <div className="disc-row-main">
                <div className="disc-name">{name}</div>
                {c.detail && <div className="genre-tags"><span className="genre-tag">{c.detail}</span></div>}
              </div>
              <div className="disc-actions" onClick={(e) => e.stopPropagation()}>
                {c.link && (
                  <a className="deezer-link" href={c.link} target="_blank" rel="noopener noreferrer">
                    Deezer ↗
                  </a>
                )}
                {added ? (
                  <span className="album-owned" title="Added to your discovery">
                    <IconCheck size={15} /> Added
                  </span>
                ) : (
                  <button
                    className="disc-btn up"
                    title="Add — like this artist and seed it into discovery"
                    disabled={seed.isPending}
                    onClick={() => seed.mutate(c)}
                  >
                    <IconApprove />
                  </button>
                )}
              </div>
            </div>
          )
        })}
      </div>

      {seed.isError && <p className="error">{(seed.error as Error).message}</p>}
    </div>
  )
}

export default function Browse() {
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(0)
  // The artist open in the right-hand readout (desktop) / drawer (mobile), and which of its tabs is
  // showing. Carried as a lightweight selection so a related-artist card the user drills into — which
  // may not be in the library — can still drive the readout.
  const [selected, setSelected] = useState<SelectedArtist | null>(null)
  const [tab, setTab] = useState<DetailTab>('albums')

  // Editing the search resets to the first page so matches are never hidden on a later page.
  const onSearch = (next: string) => {
    setQuery(next)
    setPage(0)
  }

  const { data: artists, isPending, isError, error } = useQuery({
    queryKey: ['artists'],
    queryFn: getArtists,
  })

  // Ratings are per-user; fetch them only when signed in so we can show each band's verdict.
  const { data: ratings } = useQuery({
    queryKey: ['ratings'],
    queryFn: getRatings,
    enabled: !!user,
  })

  // artist name -> current verdict (artist ratings only; album verdicts live in the album drill-down).
  const verdictByArtist = new Map<string, DiscoveryStatus>()
  for (const r of ratings ?? []) {
    if (!r.album) verdictByArtist.set(r.artist.artistName, r.verdict)
  }

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['ratings'] })
    queryClient.invalidateQueries({ queryKey: ['feed'] })
    queryClient.invalidateQueries({ queryKey: ['purchases'] })
  }

  // Thumbing an artist — works for any selected artist, owned or not (a related artist drilled into
  // from the Related tab can be liked straight into the buy list, an alternative to the Discover
  // pipeline). The kind is cosmetic to rate()/clearRating() (they send only the name), but we set it
  // honestly from library membership. Clicking the verdict that's already set clears it back to neutral.
  const rateArtist = useMutation({
    mutationFn: ({ artist, verdict, current }: { artist: string; verdict: Verdict; current?: DiscoveryStatus }) => {
      const inLibrary = (artists ?? []).some((a) => a.artistKey.artistName === artist)
      const item: FeedItem = {
        kind: inLibrary ? 'LibraryArtist' : 'RecommendedArtist',
        artist: { artistName: artist },
        album: null,
        imageUrl: null,
        score: 0,
        sources: [],
        deezerAlbumId: null,
        year: null,
        reconsider: null,
      }
      return current === verdictStatus(verdict) ? clearRating(item) : rate(item, verdict)
    },
    // Same flare as Discover — but not when the click clears an existing verdict.
    onMutate: ({ verdict, current }) =>
      rateFeedback(current === verdictStatus(verdict) ? null : verdict),
    onSuccess: invalidate,
  })

  const filtered = (artists ?? []).filter((a) =>
    normalize(a.artistKey.artistName).includes(normalize(query)),
  )

  // Clamp to a valid page after the filter shrinks (e.g. a search that lands past the current page).
  const pageCount = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE))
  const safePage = Math.min(page, pageCount - 1)
  const paged = filtered.slice(safePage * PAGE_SIZE, safePage * PAGE_SIZE + PAGE_SIZE)

  // The full library item behind the current selection, when it's an owned artist (undefined for a
  // related-artist stranger drilled into from the Related tab). Drives the readout's Deezer link,
  // genres, fans and the rate/correct actions.
  const libItem = selected ? (artists ?? []).find((a) => a.artistKey.artistName === selected.name) : undefined

  // On first visit, drop the user onto a random page and open a random artist from it, so the page
  // is a fresh jumping-off point each time rather than always the alphabetical top. Runs once (gated
  // by `randomized` so it never re-randomizes when the user closes the readout or pages around). Using
  // state, not a ref, is deliberate: it defers the reopen-on-empty effect below to the *next* commit,
  // so it can't clobber the random selection in the same render. The random selection is desktop-only
  // — on mobile the readout is a drawer, so we leave it closed (just the random page) until a row is tapped.
  const [randomized, setRandomized] = useState(false)
  useEffect(() => {
    if (randomized) return
    const list = artists ?? []
    if (list.length === 0) return
    setRandomized(true)

    // Deep-link: /browse?artist=<name> (e.g. the Discover "Go to artist" link on a missing-album
    // card) opens straight onto that artist instead of a random pick — filter the list to them and
    // open the readout, on desktop and mobile alike since this is an explicit request. Strip the
    // param afterward so closing the readout / paging behaves normally.
    const focus = searchParams.get('artist')
    if (focus) {
      setQuery(focus)
      setPage(0)
      const hit = list.find((a) => normalize(a.artistKey.artistName) === normalize(focus))
      setSelected({ name: hit?.artistKey.artistName ?? focus, imageUrl: hit?.artistImageUrl ?? null })
      setSearchParams({}, { replace: true })
      return
    }

    const totalPages = Math.max(1, Math.ceil(list.length / PAGE_SIZE))
    const randPage = Math.floor(Math.random() * totalPages)
    setPage(randPage)
    if (typeof window !== 'undefined' && window.matchMedia('(min-width: 961px)').matches) {
      const pageItems = list.slice(randPage * PAGE_SIZE, randPage * PAGE_SIZE + PAGE_SIZE)
      const pick = pageItems[Math.floor(Math.random() * pageItems.length)]
      if (pick) setSelected({ name: pick.artistKey.artistName, imageUrl: pick.artistImageUrl })
    }
  }, [artists, randomized, searchParams, setSearchParams])

  // Once randomized, keep the readout populated: if it ends up empty (e.g. the user closes it), reopen
  // the current page's first artist so the pane never falls back to the bare placeholder. Desktop only.
  const firstItem = paged[0]
  useEffect(() => {
    if (!firstItem || selected || !randomized) return
    if (typeof window !== 'undefined' && !window.matchMedia('(min-width: 961px)').matches) return
    setSelected({ name: firstItem.artistKey.artistName, imageUrl: firstItem.artistImageUrl })
  }, [firstItem, selected, randomized])

  // On mobile the readout takes over the screen; lock the background list so it can't scroll
  // (or peek through the translucent top bar) behind it. CSS scopes the lock to the mobile breakpoint.
  useEffect(() => {
    document.body.classList.toggle('detail-open', selected != null)
    return () => document.body.classList.remove('detail-open')
  }, [selected])

  return (
    <section>
      <div className="artists-header">
        <h1>Browse</h1>
        {artists && artists.length > 0 && (
          <div className="artist-search">
            <input
              type="text"
              value={query}
              placeholder={`Search ${artists.length} artists…`}
              onChange={(e) => onSearch(e.target.value)}
            />
          </div>
        )}
      </div>

      {isPending && <p><em>Loading…</em></p>}

      {isError && (
        <p className="error">Failed to load artists: {(error as Error).message}</p>
      )}

      {artists && artists.length === 0 && (
        <p><em>Catalog is empty — hit “Refresh from Plex” to populate it.</em></p>
      )}

      {artists && artists.length > 0 && (
        <>
          <div className="disc-layout">
            <div className="disc-main">
              <div className="disc-list">
                {paged.map((artist) => {
                  const name = artist.artistKey.artistName
                  return (
                    <ArtistListRow
                      key={name}
                      artist={artist}
                      verdict={verdictByArtist.get(name)}
                      selected={selected?.name === name}
                      user={!!user}
                      ratePending={rateArtist.isPending}
                      onSelect={(a) => setSelected({ name: a.artistKey.artistName, imageUrl: a.artistImageUrl })}
                      onRate={(artistName, verdict, current) =>
                        rateArtist.mutate({ artist: artistName, verdict, current })
                      }
                    />
                  )
                })}

                {filtered.length === 0 && !user && (
                  <p className="disc-sub-note"><em>No artists match “{query}”.</em></p>
                )}
                {filtered.length === 0 && user && query && (
                  <p className="disc-sub-note"><em>No library artists match — see other results below.</em></p>
                )}
              </div>

              {pageCount > 1 && (
                <div className="disc-pager">
                  <button disabled={safePage === 0} onClick={() => setPage(safePage - 1)}>
                    ‹ prev
                  </button>
                  <span>page {safePage + 1} / {pageCount}</span>
                  <button disabled={safePage >= pageCount - 1} onClick={() => setPage(safePage + 1)}>
                    next ›
                  </button>
                </div>
              )}

              {user && (
                <UncatalogedResults
                  query={query}
                  libraryNames={new Set((artists ?? []).map((a) => normalize(a.artistKey.artistName)))}
                  verdictByArtist={verdictByArtist}
                  selectedName={selected?.name ?? null}
                  onSelect={setSelected}
                />
              )}
            </div>

            <DetailPane
              // Key the whole pane by the selected artist so switching selection remounts it as one
              // atomic unit. Relying on inner key={name} props (the player, albums, related tabs) left
              // a window where a previous artist's Deezer player could linger as a stale sibling —
              // showing two "Top tracks" lists. One key on the pane closes that.
              key={selected?.name ?? '∅'}
              selected={selected}
              libItem={libItem}
              verdict={selected ? verdictByArtist.get(selected.name) : undefined}
              user={!!user}
              tab={tab}
              ratePending={rateArtist.isPending}
              onTab={setTab}
              onRate={(artistName, verdict, current) =>
                rateArtist.mutate({ artist: artistName, verdict, current })
              }
              onExplore={setSelected}
              onClose={() => setSelected(null)}
            />
          </div>
        </>
      )}
    </section>
  )
}
