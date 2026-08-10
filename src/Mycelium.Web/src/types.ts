// TS mirrors of the C# contracts in Mycelium.Interfaces (Artist.cs).
// System.Text.Json serializes record properties as camelCase by default.

export interface ArtistKey {
  artistName: string
}

export interface ArtistMetadata {
  artistKey: ArtistKey
  artistImageUrl: string | null
}

// Mirror ArtistListItem (LibraryProvider.cs) — one Artists-page row enriched with the artist's
// resolved Deezer identity, for the link-out and for spotting/fixing misassociations.
// All deezer* fields are null until the artist has been resolved.
export interface ArtistListItem {
  artistKey: ArtistKey
  artistImageUrl: string | null
  genres: string[]
  deezerId: number | null
  deezerName: string | null
  deezerFans: number | null
  deezerLink: string | null
  deezerOverride: boolean
}

// Mirror DeezerIdentity (Artist.cs) — a Deezer artist candidate in the "Correct association" picker.
export interface DeezerCandidate {
  id: number
  name: string | null
  fans: number | null
  link: string | null
  imageUrl: string | null
}

// Mirror SourceIdentity / SourceCandidate / ArtistSources (Artist.cs) — the cross-source identity
// view powering the Artists-page "Sources" tab (Deezer, MusicBrainz, ListenBrainz, …). `id` is null
// when a correctable source hasn't been resolved yet; non-correctable sources have no pin/clear.
export interface SourceIdentity {
  source: string
  id: string | null
  name: string | null
  detail: string | null
  link: string | null
  imageUrl: string | null
  isOverride: boolean
  correctable: boolean
  // Sticky "detached" decision: the artist has no match on this source, so it won't auto-resolve.
  unlinked: boolean
}

export interface SourceCandidate {
  id: string
  name: string | null
  detail: string | null
  link: string | null
  imageUrl: string | null
}

export interface ArtistSources {
  artist: ArtistKey
  sources: SourceIdentity[]
}

// Mirror LibraryLink / LibrarySource / ArtistLibraries (Artist.cs) — the per-library presence view
// powering the Artists-page "Library" tab (Plex now, Navidrome eventually), with deep links out.
export interface LibraryLink {
  label: string
  url: string
}

export interface LibrarySource {
  source: string
  label: string
  present: boolean
  links: LibraryLink[]
}

export interface ArtistLibraries {
  artist: ArtistKey
  sources: LibrarySource[]
}

// Mirror ArtistRatingStats (Artist.cs) — the user's per-song Plex rating summary (0–5 stars) for one
// artist, shown in the detail readout. `present` is false for artists not in Plex; `ratedCount` is 0
// when the artist is in Plex but nothing's rated. highest/lowest/average are null in both empty cases.
export interface ArtistRatingStats {
  artist: ArtistKey
  present: boolean
  highest: number | null
  lowest: number | null
  average: number | null
  ratedCount: number
  trackCount: number
}

// Mirrors CatalogSyncResult (IArtistCatalogRepo.cs) — returned by POST /catalog/refresh.
export interface CatalogSyncResult {
  upserted: number
  markedAbsent: number
  totalPresent: number
}

// Mirror UnifiedRelatedArtist / UnifiedRelations (RelatedArtist.cs) — returned by GET /related/{artist}.
export interface UnifiedRelatedArtist {
  artistKey: ArtistKey
  imageUrl: string | null
  sources: string[]
}

export interface UnifiedRelations {
  artist: ArtistKey
  related: UnifiedRelatedArtist[]
}

// Mirror DiscoveryCandidate / DiscoveryPage (Discovery.cs) — the per-user swipe queue.
// `sources` is the provenance shown in the UI ("via boygenius, Snail Mail").
export interface DiscoveryCandidate {
  artist: ArtistKey
  imageUrl: string | null
  score: number
  sources: string[]
  depth: number
}

export interface DiscoveryPage {
  items: DiscoveryCandidate[]
  page: number
  pageSize: number
  totalPending: number
}

// Mirror FeedKind / DiscoveryStatus / FeedItem / DiscoveryFeedPage / RatedItem (Discovery.cs).
export type FeedKind =
  | 'RecommendedArtist'
  | 'MissingAlbum'
  | 'LibraryArtist'
  | 'RecommendedLibraryArtist'
  | 'SeedLibraryArtist'
  | 'ReconsiderArtist'
  | 'SecondThoughtsArtist'
export type DiscoveryStatus = 'Pending' | 'Liked' | 'Disliked' | 'Snoozed'

// One thing to react to in the discovery feed. `album` is set only for MissingAlbum items;
// `score`/`sources` rank and explain recommended artists (0/empty otherwise).
export interface FeedItem {
  kind: FeedKind
  artist: ArtistKey
  album: string | null
  imageUrl: string | null
  score: number
  sources: string[]
  // Deezer album id for MissingAlbum items (lets the UI sample/link the album); null otherwise.
  deezerAlbumId: number | null
  // Release year for album items, null when Deezer supplied no date (or for artist items).
  year: number | null
  // The evidence behind a ReconsiderArtist / SecondThoughtsArtist card (why a thumbed verdict is
  // being questioned), snapshotted by the weekly sweep that flagged it. Null for every other kind.
  reconsider: ReconsiderSignal | null
}

// Mirror ReconsiderSignal (Discovery.cs) — the Plex rating snapshot that got a thumbed artist flagged,
// either as a well-rated dislike (second chance) or a poorly-rated like (second thoughts). Stored on
// the queue row by the sweep, not computed per request.
export interface ReconsiderSignal {
  average: number
  ratedCount: number
  trackCount: number
}

// A paged feed section for a single FeedKind.
export interface DiscoveryFeedPage {
  kind: FeedKind
  items: FeedItem[]
  page: number
  pageSize: number
  total: number
}

// A rating the user has made, for the Ratings review page.
export interface RatedItem {
  kind: FeedKind
  artist: ArtistKey
  album: string | null
  imageUrl: string | null
  verdict: DiscoveryStatus
  // ISO timestamp set only for Snoozed items — when the artist resurfaces in the feed.
  snoozeUntil: string | null
}

// Mirror ArtistAlbumItem (Discovery.cs) — one album in an artist's full discography for the
// Artists-page drill-down. `owned` marks albums in the library; `verdict` is the user's rating on a
// missing album (null = undecided or owned). Owned-only albums carry no deezerAlbumId/imageUrl.
export interface ArtistAlbumItem {
  artist: ArtistKey
  album: string
  imageUrl: string | null
  deezerAlbumId: number | null
  owned: boolean
  verdict: DiscoveryStatus | null
  // Deezer's release year; null for owned-only albums Deezer doesn't list, or when it gave no date.
  year: number | null
  // Blocked for everyone (not just you) — filtered out of the feeds entirely, and surfaced here only
  // so the block can be lifted. Always false for owned albums.
  blocked: boolean
}

// Mirror LibraryAlbumOption (Album.cs) — one album already in the library, offered as a merge target
// for a release the diff calls missing. Carries the artist because the copy we own can be filed under
// a different act than the one whose discography surfaced it.
export interface LibraryAlbumOption {
  artist: string
  album: string
  // Deep link to the album in Plex, so a near-miss title can be checked before merging into it. Null
  // when the album's Plex rating key isn't captured yet, or Plex couldn't be reached.
  plexUrl: string | null
}

// Mirror PurchaseStatus / PurchaseItem (IPurchaseRepo.cs) — the shared "to buy" list with a
// persisted acquisition lifecycle. `kind` is 'RecommendedArtist' (no album) or 'MissingAlbum'.
export type PurchaseStatus = 'Pending' | 'Queued' | 'Downloading' | 'Sent' | 'InLibrary' | 'Failed'

// Mirror DownloadSnapshot (IPurchaseRepo.cs) — the live download-monitor payload.
export interface DownloadSnapshot {
  automatic: boolean
  backend: string
  batchSize: number
  itemDelaySeconds: number
  batchIntervalMinutes: number
  // ± spread applied to the two timings above, as a percentage (0 = exact).
  jitterPercent: number
  queued: number
  downloading: number
  complete: number
  failed: number
  current: PurchaseItem[]
  // ISO timestamps for when the drainer next acts (null when nothing is scheduled).
  nextItemAt: string | null
  nextBatchAt: string | null
}

export interface PurchaseItem {
  id: string
  kind: FeedKind
  artist: ArtistKey
  album: string | null
  imageUrl: string | null
  score: number
  sources: string[]
  status: PurchaseStatus
  requestedAt: string
  sentAt: string | null
  // Deezer album id for downloadable (MissingAlbum) items; null for artists.
  deezerAlbumId: number | null
}

// The signed-in user, as returned by GET /auth/me (the BFF). Null when not authenticated.
export interface CurrentUser {
  subject: string
  username: string | null
  email: string | null
  displayName: string | null
  // True when this user is in DEV_USERNAMES — unlocks the in-app dev panel.
  isDev: boolean
}
