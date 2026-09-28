// The reconciliation page: which MusicBrainz artist each library artist is, and the ones a person
// has to settle. Mirrors ArtistIdentityAuditor / ArtistResolution on the backend. Every route is
// dev-gated server-side.

// The nav badge's query key, shared so the page can refresh the badge after settling an artist.
export const RECONCILE_COUNT_KEY = ['dev', 'identity', 'count']

export type ResolutionStatus = 'Pinned' | 'Resolved' | 'Ambiguous' | 'Mixed' | 'Missing' | 'Unlinked'
export type ResolutionConfidence = 'High' | 'Medium' | 'Low'

export interface ResolutionCandidate {
  mbid: string
  name: string | null
  disambiguation: string | null
  // How many of the library's albums are on this artist; null when it wasn't counted.
  albumOverlap: number | null
  // Why it was considered: 'name' | 'alias' | 'deezer' | 'current'.
  evidence: string[]
  // The library's titles that are on it; null when it wasn't checked.
  matchedAlbums: string[] | null
  // How many release groups it has on MusicBrainz.
  releaseGroups: number | null
}

export interface ArtistResolution {
  // The name, or name + Plex artist when several Plex artists share the name.
  id: string
  artist: string
  // Set when the name covers several Plex artists and this is one of them.
  plexArtistKey: number | null
  // The library's album titles this was judged on.
  albums: string[] | null
  status: ResolutionStatus
  confidence: ResolutionConfidence | null
  mbid: string | null
  name: string | null
  disambiguation: string | null
  // The MBID linked today. When it differs from mbid, one of the two is wrong.
  currentMbid: string | null
  ownedAlbums: number
  candidates: ResolutionCandidate[]
  reason: string
  checkedAt: string
  needsAttention: boolean
  disagreesWithCurrent: boolean
}

export interface IdentityPassStatus {
  running: boolean
  processed: number
  total: number
  unreachable: number
  errors: number
  currentArtist: string | null
  startedAt: string | null
  finishedAt: string | null
}

export interface IdentityReport {
  libraryArtists: number
  checked: number
  pinned: number
  high: number
  medium: number
  low: number
  ambiguous: number
  mixed: number
  missing: number
  unlinked: number
  disagreesWithCurrent: number
  // Names covering more than one Plex artist, each checked separately.
  sharedNames: number
  needsAttention: number
  pass: IdentityPassStatus
}

async function json<T>(res: Response, what: string): Promise<T> {
  if (!res.ok) {
    let detail = res.statusText
    try {
      const body = await res.json()
      detail = body?.error ?? body?.detail ?? detail
    } catch {
      // Not JSON; the status text will do.
    }
    throw new Error(`${what}: ${detail}`)
  }
  return (await res.json()) as T
}

export async function getIdentityReport(): Promise<IdentityReport> {
  return json(await fetch('/api/dev/identity/report'), 'Failed to load the identity report')
}

export async function startIdentityPass(all: boolean): Promise<IdentityPassStatus> {
  const params = new URLSearchParams({ all: String(all) })
  return json(await fetch(`/api/dev/identity/pass?${params}`, { method: 'POST' }), 'Failed to start the pass')
}

export async function getReconcileList(): Promise<ArtistResolution[]> {
  return json(await fetch('/api/dev/identity/reconcile'), 'Failed to load the artists to reconcile')
}

export async function getReconcileCount(): Promise<number> {
  const body = await json<{ count: number }>(
    await fetch('/api/dev/identity/reconcile/count'),
    'Failed to count the artists to reconcile',
  )
  return body.count
}

function artistParams(artist: string, plexArtistKey: number | null, extra: Record<string, string> = {}) {
  const params = new URLSearchParams({ artist, ...extra })
  if (plexArtistKey !== null) params.set('plexArtistKey', String(plexArtistKey))
  return params
}

export async function recheckArtist(artist: string, plexArtistKey: number | null): Promise<ArtistResolution> {
  const params = artistParams(artist, plexArtistKey)
  return json(await fetch(`/api/dev/identity/recheck?${params}`, { method: 'POST' }), `Re-check of ${artist} failed`)
}

export async function acceptArtist(
  artist: string,
  plexArtistKey: number | null,
  mbid: string,
): Promise<ArtistResolution> {
  const params = artistParams(artist, plexArtistKey, { mbid })
  return json(await fetch(`/api/dev/identity/accept?${params}`, { method: 'POST' }), `Could not settle ${artist}`)
}

// Pulls an artist MBID out of whatever was pasted: a musicbrainz.org/artist/… URL or a bare id.
export function parseArtistMbid(text: string): string | null {
  const match = text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)
  return match ? match[0].toLowerCase() : null
}

export const musicBrainzArtistUrl = (mbid: string) => `https://musicbrainz.org/artist/${mbid}`
export const musicBrainzSearchUrl = (name: string) =>
  `https://musicbrainz.org/search?${new URLSearchParams({ query: name, type: 'artist', method: 'indexed' })}`
export const MUSICBRAINZ_ADD_ARTIST_URL = 'https://musicbrainz.org/artist/create'

// ---- Albums: owned albums the discography build couldn't settle (AlbumReconciliation on the backend,
// MUSICBRAINZ-IDENTITY.md §6.1) ----

export const RECONCILE_ALBUMS_KEY = ['dev', 'identity', 'albums']

// Pick: the title fits several release groups. Missing: none fits. Loose: linked on a near title only.
export type AlbumReconcileKind = 'Pick' | 'Missing' | 'Loose'

export interface ReleaseGroupBrief {
  mbid: string
  title: string | null
  primaryType: string | null
  secondaryTypes: string[]
  firstReleaseDate: string | null
  // The most tracks any of its Deezer editions has; null when it has none.
  tracks: number | null
  hasDeezer: boolean
}

export interface AlbumReconcileItem {
  title: string
  // The library artist it is filed under (ArtistResolution.id).
  libraryArtist: string
  kind: AlbumReconcileKind
  // Loose: the group it is linked to.
  linked: ReleaseGroupBrief | null
  // Pick: the tied groups, likeliest first.
  candidates: ReleaseGroupBrief[]
  // Missing: a Deezer album of the same title that fits no release group, for a Harmony import.
  deezerAlbumId: number | null
}

export interface AlbumReconcileArtist {
  mbid: string
  name: string | null
  pick: number
  missing: number
  loose: number
  albums: AlbumReconcileItem[]
}

export async function getAlbumReconcileList(): Promise<AlbumReconcileArtist[]> {
  return json(await fetch('/api/dev/identity/albums'), 'Failed to load the albums to reconcile')
}

async function albumAction(
  action: 'link' | 'leave' | 'unlink',
  artistMbid: string,
  album: AlbumReconcileItem,
  extra: Record<string, string> = {},
): Promise<void> {
  const params = new URLSearchParams({
    artistMbid,
    libraryArtist: album.libraryArtist,
    title: album.title,
    ...extra,
  })
  const res = await fetch(`/api/dev/identity/albums/${action}?${params}`, { method: 'POST' })
  if (!res.ok) await json(res, `Could not update ${album.title}`)
}

export const linkAlbum = (artistMbid: string, album: AlbumReconcileItem, releaseGroup: string) =>
  albumAction('link', artistMbid, album, { releaseGroup })
export const leaveAlbum = (artistMbid: string, album: AlbumReconcileItem) => albumAction('leave', artistMbid, album)
export const unlinkAlbum = (artistMbid: string, album: AlbumReconcileItem) => albumAction('unlink', artistMbid, album)

// Rebuilds the artist's discography, asking MusicBrainz again: for straight after an edit there.
export async function recheckDiscography(artistMbid: string): Promise<void> {
  const res = await fetch(`/api/dev/discography/artist/${artistMbid}?fresh=true`, { method: 'POST' })
  if (!res.ok) await json(res, 'Re-check failed')
}

// Pulls a release group MBID out of a musicbrainz.org/release-group/… URL or a bare id. A release URL is
// refused: its id is a release's, not the group's.
export function parseReleaseGroupMbid(text: string): string | null {
  if (/\/release\//i.test(text)) return null
  return parseArtistMbid(text)
}

export const musicBrainzReleaseGroupUrl = (mbid: string) => `https://musicbrainz.org/release-group/${mbid}`
export const musicBrainzReleaseGroupSearchUrl = (artist: string, title: string) =>
  `https://musicbrainz.org/search?${new URLSearchParams({
    query: `releasegroup:"${title}" AND artist:"${artist}"`,
    type: 'release_group',
    method: 'advanced',
  })}`
export const MUSICBRAINZ_ADD_RELEASE_URL = 'https://musicbrainz.org/release/add'
export const harmonyImportUrl = (deezerAlbumId: number) =>
  `https://harmony.pulsewidth.org.uk/release?${new URLSearchParams({
    url: `https://www.deezer.com/album/${deezerAlbumId}`,
  })}`
