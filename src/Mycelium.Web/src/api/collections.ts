// Collections: records no artist's discography can reach — various-artists compilations,
// soundtracks, cast recordings. Every other view in this app is reached *through* an artist, and
// Deezer credits these to an umbrella act whose discography is empty, so the only way in is to name
// the record. All calls require an authenticated session (cookie sent automatically, same-origin).
import type { CollectionItem } from '../types'
import { DeezerBusyError } from './deezer'
import type { Verdict } from './discovery'

// Everything the signed-in user can act on: umbrella-credited albums the library already holds, plus
// every one they've thumbed. The owned-but-unrated rows matter — without them there's no way to say
// you like a compilation you already own.
export async function getCollections(): Promise<CollectionItem[]> {
  const res = await fetch('/api/collections')
  if (!res.ok) {
    throw new Error(`Failed to load collections: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as CollectionItem[]
}

// Deezer album search, umbrella-credited hits first.
export async function searchCollections(q: string): Promise<CollectionItem[]> {
  const params = new URLSearchParams({ q })
  const res = await fetch(`/api/collections/search?${params}`)
  // 503 = Deezer didn't answer. Thrown rather than returned empty so the caller retries and says so,
  // instead of caching "no such record" as the answer.
  if (res.status === 503) {
    throw new DeezerBusyError()
  }
  if (!res.ok) {
    throw new Error(`Collection search failed: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as CollectionItem[]
}

// Resolve a pasted Deezer album link (or bare id) into a rateable row — the path for a record search
// won't surface. POST body because a pasted URL carries '/' and Deezer's share tracking params.
// Null when the paste holds no album id, or Deezer doesn't know it.
export async function resolveCollection(url: string): Promise<CollectionItem | null> {
  const res = await fetch('/api/collections/resolve', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url }),
  })
  if (res.status === 404) {
    return null
  }
  if (!res.ok) {
    throw new Error(`Failed to read that link: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as CollectionItem
}

// Thumb a collection by its Deezer album id. A like queues it to buy the same way a missing album
// does, and — for an umbrella-credited record — stamps the verdict onto the album in Plex.
export async function rateCollection(deezerAlbumId: number, verdict: Verdict): Promise<CollectionItem> {
  const params = new URLSearchParams({ id: String(deezerAlbumId), verdict })
  const res = await fetch(`/api/collections/rate?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to rate collection: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as CollectionItem
}

// Thumb a collection the library already holds but that never came through this app, so there is no
// Deezer id to key it by. Goes through the ordinary album-rating endpoint — which stamps the album
// mood for an umbrella-credited record exactly as the collection path does — because the record is
// already on the shelf and there is nothing to acquire.
export async function rateOwnedCollection(
  artist: string,
  album: string,
  albumArt: string | null,
  verdict: Verdict,
): Promise<void> {
  const params = new URLSearchParams({ artist, album, verdict })
  if (albumArt) params.set('albumArt', albumArt)
  const res = await fetch(`/api/discovery/rate?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to rate ${album}: ${res.status} ${res.statusText}`)
  }
}

// Clear a collection's verdict, stripping the album mood with it. Same endpoint every other album
// rating is cleared through.
export async function clearCollectionRating(artist: string, album: string): Promise<void> {
  const params = new URLSearchParams({ artist, album })
  const res = await fetch(`/api/discovery/rate?${params}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to clear rating for ${album}: ${res.status} ${res.statusText}`)
  }
}
