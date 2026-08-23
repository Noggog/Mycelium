// Per-user discovery feed + ratings. All calls require an authenticated session (cookie sent
// automatically, same-origin). artist/album go in the query string so names with '/' work.
import type {
  ArlUpdateResult,
  ArtistAlbumItem,
  DiscoveryFeedPage,
  DownloadSnapshot,
  FeedItem,
  FeedKind,
  LibraryAlbumOption,
  ManualAddOutcome,
  PurchaseItem,
  RatedItem,
} from '../types'

export type Verdict = 'up' | 'down'

// How long a snoozed recommendation stays hidden before it resurfaces in the feed.
export type SnoozeDuration = 'week' | 'month' | 'year'

// A paged feed section for one category (recommended artists, missing albums, unrated owned artists).
export async function getFeed(kind: FeedKind, page = 0, pageSize = 20): Promise<DiscoveryFeedPage> {
  const params = new URLSearchParams({ kind, page: String(page), pageSize: String(pageSize) })
  const res = await fetch(`/api/discovery?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load ${kind} feed: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DiscoveryFeedPage
}

// A single mixed feed across the selected categories, round-robin interleaved + shuffled by `seed`
// (same seed → same order across pages). Each item carries its own `kind`.
export async function getMixedFeed(
  kinds: FeedKind[],
  page = 0,
  pageSize = 20,
  seed = 0,
): Promise<DiscoveryFeedPage> {
  const params = new URLSearchParams({
    kinds: kinds.join(','),
    page: String(page),
    pageSize: String(pageSize),
    seed: String(seed),
  })
  const res = await fetch(`/api/discovery/mixed?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load discovery feed: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DiscoveryFeedPage
}

// A liked non-owned artist's acquirable albums (their Deezer discography minus anything owned),
// surfaced inline under the just-rated card. Fetched on demand only when an artist is liked.
export async function getArtistAlbums(artist: string): Promise<FeedItem[]> {
  const params = new URLSearchParams({ artist })
  const res = await fetch(`/api/discovery/artist-albums?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load albums for ${artist}: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as FeedItem[]
}

// An owned artist's full discography (owned + missing albums, each flagged) for the Artists-page
// drill-down. One Deezer call server-side; missing albums carry the user's verdict if already rated.
export async function getArtistDiscography(artist: string): Promise<ArtistAlbumItem[]> {
  const params = new URLSearchParams({ artist })
  const res = await fetch(`/api/discovery/artist-discography?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load discography for ${artist}: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ArtistAlbumItem[]
}

// Dev-only: rebuild the pending recommendations for every user from their liked artists (keeps
// ratings). Returns the number of users rebuilt.
export async function refreshQueue(): Promise<{ rebuilt: number }> {
  const res = await fetch('/api/discovery/refresh', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to rebuild recommendations: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as { rebuilt: number }
}

// Thumb an artist or — when album is supplied — a missing album.
export async function rate(item: FeedItem | RatedItem, verdict: Verdict): Promise<void> {
  const params = new URLSearchParams({ artist: item.artist.artistName, verdict })
  if (item.album) {
    params.set('album', item.album)
    if (item.imageUrl) params.set('albumArt', item.imageUrl)
  }
  const res = await fetch(`/api/discovery/rate?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to rate ${item.artist.artistName}: ${res.status} ${res.statusText}`)
  }
}

// Ad-hoc seed: add an artist that's not in the library and that nothing recommends yet, by pinning a
// chosen source candidate (by id) and liking it — it then grows the frontier and joins the buy list.
// Used by the Artists "Search all of Deezer" results, where each hit carries its source id.
export async function seedArtist(source: string, id: string, artist: string): Promise<void> {
  const params = new URLSearchParams({ source, id, artist })
  const res = await fetch(`/api/discovery/seed?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to add ${artist}: ${res.status} ${res.statusText}`)
  }
}

// Snooze an artist or — when album is supplied — a missing album (hidden for the chosen duration;
// resurfaces when the window lapses).
export async function snooze(item: FeedItem | RatedItem, duration: SnoozeDuration): Promise<void> {
  const params = new URLSearchParams({ artist: item.artist.artistName, duration })
  if (item.album) {
    params.set('album', item.album)
    if (item.imageUrl) params.set('albumArt', item.imageUrl)
  }
  const res = await fetch(`/api/discovery/snooze?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to snooze ${item.artist.artistName}: ${res.status} ${res.statusText}`)
  }
}

// Clear a rating, returning the artist/album to the feed.
export async function clearRating(item: FeedItem | RatedItem): Promise<void> {
  const params = new URLSearchParams({ artist: item.artist.artistName })
  if (item.album) params.set('album', item.album)
  const res = await fetch(`/api/discovery/rate?${params}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to clear rating for ${item.artist.artistName}: ${res.status} ${res.statusText}`)
  }
}

// Every rating the user has made, for the review page (albums that now exist are filtered out).
export async function getRatings(): Promise<RatedItem[]> {
  const res = await fetch('/api/discovery/ratings')
  if (!res.ok) {
    throw new Error(`Failed to load ratings: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as RatedItem[]
}

// The shared "to buy" list: liked non-owned artists + liked albums not yet acquired, with a
// persisted status (pending → sent → in-library). In-library items have dropped off.
export async function getPurchases(): Promise<PurchaseItem[]> {
  const res = await fetch('/api/purchases')
  if (!res.ok) {
    throw new Error(`Failed to load wishlist: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PurchaseItem[]
}

// A live snapshot of the download subsystem for the monitor panel (polled).
export async function getDownloadStatus(): Promise<DownloadSnapshot> {
  const res = await fetch('/api/purchases/status')
  if (!res.ok) {
    throw new Error(`Failed to load download status: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as DownloadSnapshot
}

// Flip the background drainer between automatic and manual. Stored server-side (Mongo), so the
// choice survives a redeploy and applies to everyone — this is the maintainer's shared queue.
export async function setDownloadsAutomatic(automatic: boolean): Promise<void> {
  const res = await fetch(`/api/purchases/automatic?automatic=${automatic}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to change download mode: ${res.status} ${res.statusText}`)
  }
}

// Replace the Deezer ARL streamrip authenticates with, after an expiry has blocked downloads. The
// token goes in a POST body, never a query string — a URL would be logged by the request logger and
// kept in browser history. The server validates it against Deezer before saving, so a rejection here
// means the token is genuinely bad rather than merely unwritten; it's returned as a 400 whose body is
// the same shape as success, carrying a message written for the user.
export async function setDeezerArl(arl: string): Promise<ArlUpdateResult> {
  const res = await fetch('/api/purchases/deezer-arl', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ arl }),
  })
  const body = (await res.json().catch(() => null)) as ArlUpdateResult | null
  if (!res.ok) {
    throw new Error(body?.error ?? `Failed to save the ARL: ${res.status} ${res.statusText}`)
  }
  return body!
}

// Manually queue an item for download now (non-blocking — the drainer does the fetch). Also the
// "retry" action for failed items. Works whether or not automatic downloads are on.
export async function downloadPurchase(id: string): Promise<void> {
  const res = await fetch(`/api/purchases/download?id=${encodeURIComponent(id)}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to queue download: ${res.status} ${res.statusText}`)
  }
}

// Queue an album by hand from a pasted Deezer album link (or bare id) — the way in for releases no
// owned artist's discography lists, chiefly various-artists compilations, which appear in no
// contributor's discography and so can never reach the feed. The paste goes in a POST body: a Deezer
// URL carries path separators and its own share query string.
//
// Both outcomes the user can act on come back as a parsed body rather than a throw — 'Added' and
// 'AlreadyQueued' arrive as 200, the refusals ('BadLink', 'NotFound', 'AlreadyOwned') as 400 with the
// same shape — so the paste box can explain which happened instead of showing an HTTP error.
export async function addManualPurchase(url: string): Promise<ManualAddOutcome> {
  const res = await fetch('/api/purchases/add', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url }),
  })
  const body = (await res.json().catch(() => null)) as ManualAddOutcome | null
  if (!body) {
    throw new Error(`Failed to add album: ${res.status} ${res.statusText}`)
  }
  return body
}

// Drop a hand-added row. Only these can be deleted directly — every other row leaves by clearing the
// rating behind it (see clearRating), which the reconcile then prunes.
export async function removeManualPurchase(id: string): Promise<void> {
  const res = await fetch(`/api/purchases/manual?id=${encodeURIComponent(id)}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to remove album: ${res.status} ${res.statusText}`)
  }
}

// Undo — move a downloaded/queued item back to "pending".
export async function unsendPurchase(id: string): Promise<void> {
  const res = await fetch(`/api/purchases/unsend?id=${encodeURIComponent(id)}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to revert item: ${res.status} ${res.statusText}`)
  }
}

// The library albums a missing album can be merged into: suggestions for this album (what's owned
// under its act, plus same-titled copies filed under another act) when `query` is empty, or a
// whole-library search on artist/title when it isn't. Fetched when the "Already in library?" pane opens.
export async function getMergeCandidates(
  artist: string,
  album: string,
  query = '',
): Promise<LibraryAlbumOption[]> {
  const params = new URLSearchParams({ artist, album })
  if (query) params.set('q', query)
  const res = await fetch(`/api/albums/merge-candidates?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load library albums: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as LibraryAlbumOption[]
}

// Block an album for everyone. A thumbs-down ("meh") is per-user and invisible to anyone else; this
// takes the release off every user's feed for good, and survives the nightly Deezer re-diff. Existing
// verdicts and queued downloads are left alone — it stops the album being offered, not choices made.
export async function blockAlbum(artist: string, album: string): Promise<void> {
  const params = new URLSearchParams({ artist, album })
  const res = await fetch(`/api/albums/block?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to block ${album}: ${res.status} ${res.statusText}`)
  }
}

// Lift a global block, returning the album to everyone's feeds.
export async function unblockAlbum(artist: string, album: string): Promise<void> {
  const params = new URLSearchParams({ artist, album })
  const res = await fetch(`/api/albums/block?${params}`, { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to unblock ${album}: ${res.status} ${res.statusText}`)
  }
}

// Merge a missing album into one already in the library under a different title. Records a durable
// match override (honoured by the reconcile and the missing-album diff), so the album stops being
// offered anywhere and never reaches the downloader.
export async function mergeAlbum(artist: string, album: string, libraryAlbum: string): Promise<void> {
  const params = new URLSearchParams({ artist, album, libraryAlbum })
  const res = await fetch(`/api/albums/merge?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to merge album: ${res.status} ${res.statusText}`)
  }
}

