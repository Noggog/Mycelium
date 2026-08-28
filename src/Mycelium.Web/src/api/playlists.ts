// The Playlists page: connecting a user's own Plex account, and the stock smart playlists the app
// can build in it. Every call is auth-gated server-side.

// ---- Plex account linking --------------------------------------------------------------------

export interface PlexLinkStatus {
  linked: boolean
  username: string | null
  email: string | null
  linkedAt: string | null
}

// 'pending' is the normal answer while the user is still approving in their browser tab.
// 'invalidtoken' only comes back from the paste path — the PIN flow can't produce a bad token.
export type PlexLinkOutcome = 'linked' | 'pending' | 'expired' | 'noserveraccess' | 'invalidtoken'

export interface PlexLinkCompletion {
  outcome: PlexLinkOutcome
  status: PlexLinkStatus
}

export async function getPlexLink(): Promise<PlexLinkStatus> {
  const res = await fetch('/api/plex/link')
  if (!res.ok) {
    throw new Error(`Failed to read the Plex link: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexLinkStatus
}

// Starts the plex.tv PIN flow and returns the URL to send the user to. The code itself stays on the
// server, keyed against this user — the poll below needs no arguments.
export async function startPlexLink(forwardUrl?: string): Promise<string> {
  const params = new URLSearchParams()
  if (forwardUrl) params.set('forwardUrl', forwardUrl)
  const res = await fetch(`/api/plex/link/start?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Couldn't start the Plex link: ${res.status} ${res.statusText}`)
  }
  return ((await res.json()) as { authUrl: string }).authUrl
}

export async function completePlexLink(): Promise<PlexLinkCompletion> {
  const res = await fetch('/api/plex/link/complete', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Couldn't finish the Plex link: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexLinkCompletion
}

// Links a pasted token instead of running the PIN flow — for signing in as a Plex Home / managed user
// who has no app.plex.tv browser session to approve with. POST body, never a query string: the token
// would otherwise be written verbatim into the backend's request log. The server answers 400 when it
// rejects the token, but the body still carries the outcome, so read it before checking res.ok.
// `label` names the account when plex.tv can't identify the token — a Plex server access token
// verifies against the server but can't be attributed, so it's a display label, not a claim.
export async function linkPlexWithToken(token: string, label?: string): Promise<PlexLinkCompletion> {
  const res = await fetch('/api/plex/link/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, label }),
  })
  const body = (await res.json().catch(() => null)) as PlexLinkCompletion | null
  if (!body) {
    throw new Error(`Couldn't link that Plex token: ${res.status} ${res.statusText}`)
  }
  return body
}

export async function unlinkPlex(): Promise<void> {
  const res = await fetch('/api/plex/link', { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Couldn't disconnect Plex: ${res.status} ${res.statusText}`)
  }
}

// ---- Stock smart playlists -------------------------------------------------------------------

// Exists  — a playlist with these exact rules is already there, whatever it's called.
// Differs — something holds the name but selects different tracks; offer to rewrite its rules.
export type StockPlaylistState = 'NotCreated' | 'Exists' | 'Differs' | 'Unavailable'

export interface StockPlaylist {
  id: string
  title: string
  description: string
  state: StockPlaylistState
  matchedTitle: string | null
  matchedRatingKey: string | null
  trackCount: number | null
  note: string | null
  // app.plex.tv link to the matched playlist, when one was matched and the server named itself.
  plexUrl: string | null
}

export interface PlaylistSurvey {
  linked: boolean
  plexUsername: string | null
  freshMonths: number
  playlists: StockPlaylist[]
}

// The "not played in the last N months" windows the Fresh variants offer.
export const FRESH_WINDOWS = [1, 3, 6, 12] as const

async function readError(res: Response, fallback: string): Promise<never> {
  // The server returns { error } for the cases the user can act on (no linked account, unknown id).
  const body = await res.json().catch(() => null)
  throw new Error((body as { error?: string } | null)?.error ?? fallback)
}

export async function getStockPlaylists(freshMonths: number): Promise<PlaylistSurvey> {
  const res = await fetch(`/api/playlists/stock?freshMonths=${freshMonths}`)
  if (!res.ok) {
    throw new Error(`Failed to load playlists: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlaylistSurvey
}

// Idempotent: if the survey already recognises the playlist, the server returns the existing one
// rather than making a second copy.
export async function createStockPlaylist(
  id: string,
  freshMonths: number,
): Promise<StockPlaylist> {
  const res = await fetch(`/api/playlists/stock/${encodeURIComponent(id)}?freshMonths=${freshMonths}`, {
    method: 'POST',
  })
  if (!res.ok) return readError(res, `Couldn't create the playlist: ${res.status} ${res.statusText}`)
  return (await res.json()) as StockPlaylist
}

// Rewrites the rules of the playlist currently holding this definition's name.
export async function updateStockPlaylist(
  id: string,
  freshMonths: number,
): Promise<StockPlaylist> {
  const res = await fetch(`/api/playlists/stock/${encodeURIComponent(id)}?freshMonths=${freshMonths}`, {
    method: 'PUT',
  })
  if (!res.ok) return readError(res, `Couldn't update the playlist: ${res.status} ${res.statusText}`)
  return (await res.json()) as StockPlaylist
}
