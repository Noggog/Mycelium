// Deezer play info for an artist: up to 5 30-second previews to sample plus a link to the artist's
// Deezer page. Returns null when Deezer has no match (the backend answers 404). artist goes in the
// query string so names with '/' work.

import type { DeezerCandidate } from '../types'

// A 503 from any Deezer-backed endpoint: Deezer itself never answered — it rate-limits bursts at
// ~50 calls per 5 seconds, and one artist's discography is a burst on its own. The backend refuses to
// invent an answer for that, because an empty list is indistinguishable from "this artist has
// nothing" once the client caches it — which is how a five-second blip used to blank an artist's
// albums until a hard reload. Distinct from a 404, which is Deezer answering "no such artist": that
// one is a fact worth keeping, this one is worth retrying.
export class DeezerBusyError extends Error {
  constructor(message = 'Deezer is rate-limiting requests right now.') {
    super(message)
    this.name = 'DeezerBusyError'
  }
}

export const isDeezerBusy = (err: unknown): boolean => err instanceof DeezerBusyError

export interface DeezerPreviewTrack {
  title: string
  previewUrl: string
}

export interface DeezerPlayInfo {
  id: number
  artistLink: string
  imageUrl: string | null
  tracks: DeezerPreviewTrack[]
}

// `fresh` re-mints the (short-lived, signed) preview urls server-side — used to retry a preview
// whose url expired while the readout was open.
export async function getDeezerPlayInfo(artist: string, fresh = false): Promise<DeezerPlayInfo | null> {
  const params = new URLSearchParams({ artist })
  if (fresh) params.set('fresh', 'true')
  const res = await fetch(`/api/deezer/artist?${params}`)
  if (res.status === 404) {
    return null
  }
  if (res.status === 503) {
    throw new DeezerBusyError()
  }
  if (!res.ok) {
    throw new Error(`Failed to resolve Deezer artist: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DeezerPlayInfo
}

// Play info for a specific album id: its previewable tracks plus a link to the album's Deezer page.
export interface DeezerAlbumPlayInfo {
  id: number
  albumLink: string
  tracks: DeezerPreviewTrack[]
}

export async function getDeezerAlbumPlayInfo(albumId: number, fresh = false): Promise<DeezerAlbumPlayInfo> {
  const params = new URLSearchParams({ id: String(albumId) })
  if (fresh) params.set('fresh', 'true')
  const res = await fetch(`/api/deezer/album?${params}`)
  if (res.status === 503) {
    throw new DeezerBusyError()
  }
  if (!res.ok) {
    throw new Error(`Failed to resolve Deezer album: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DeezerAlbumPlayInfo
}

// Free-text Deezer artist search powering the "Correct association" picker — candidates in
// relevance order (id, name, fans, link, photo).
export async function searchDeezerArtists(q: string): Promise<DeezerCandidate[]> {
  const res = await fetch(`/api/deezer/search?${new URLSearchParams({ q })}`)
  if (!res.ok) {
    throw new Error(`Failed to search Deezer artists: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DeezerCandidate[]
}
