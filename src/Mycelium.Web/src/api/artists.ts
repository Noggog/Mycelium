import type { ArtistListItem, CatalogSyncResult, DeezerCandidate } from '../types'

// Calls the backend's GET /artists through the Vite dev proxy (/api -> backend).
// This now serves from the local catalog store, not live Plex; refresh it via
// refreshCatalog() below. Each row carries the artist's resolved Deezer identity.
export async function getArtists(): Promise<ArtistListItem[]> {
  const res = await fetch('/api/artists')
  if (!res.ok) {
    throw new Error(`Failed to load artists: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ArtistListItem[]
}

// Triggers the Library Catalog sync job (POST /catalog/refresh): the backend pulls
// the artist list from Plex and upserts the catalog. The only Plex-touching call.
//
// The failure worth reading is an expired Plex credential, which the backend answers as a 502 whose
// ProblemDetails `detail` names the remedy — so prefer that text over the status line, which on its
// own says nothing a person can act on.
export async function refreshCatalog(): Promise<CatalogSyncResult> {
  const res = await fetch('/api/catalog/refresh', { method: 'POST' })
  if (!res.ok) {
    throw new Error(await problemDetail(res, 'Failed to refresh catalog'))
  }
  return (await res.json()) as CatalogSyncResult
}

// Reads an ASP.NET ProblemDetails body for its human-facing `detail`, falling back to the status
// line when the response carries none (or isn't JSON at all — a proxy error page, say).
async function problemDetail(res: Response, fallback: string): Promise<string> {
  try {
    const body = (await res.json()) as { detail?: string; title?: string }
    const message = body.detail ?? body.title
    if (message) return message
  } catch {
    // Body wasn't JSON; fall through to the status line.
  }
  return `${fallback}: ${res.status} ${res.statusText}`
}

// Pin a library artist to a specific Deezer artist id (fix a misassociation). The backend stores
// a sticky override and re-ingests that artist's similarity edges. Returns the pinned identity.
export async function setDeezerId(artist: string, id: number): Promise<DeezerCandidate> {
  const params = new URLSearchParams({ artist, id: String(id) })
  const res = await fetch(`/api/artists/deezer-id?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to set Deezer id for ${artist}: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DeezerCandidate
}

// Clear a Deezer override so the artist re-resolves from a name search next time.
export async function clearDeezerId(artist: string): Promise<void> {
  const params = new URLSearchParams({ artist })
  const res = await fetch(`/api/artists/deezer-id?${params}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to clear Deezer id for ${artist}: ${res.status} ${res.statusText}`)
  }
}
