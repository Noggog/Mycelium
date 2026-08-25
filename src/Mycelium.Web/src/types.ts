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
  // The source's own follower count (Deezer fans), null where the source has none — the tie-break
  // for collapsing several same-named candidates onto the canonical act.
  popularity: number | null
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

// Mirror ArtistTags (Artist.cs) — the editable Plex descriptor tags for one library artist, behind
// the Browse readout's "Tags" tab. `present` is false for artists that aren't in the library (nothing
// to tag). `moods` never includes the app's own mood tags — the "<user>_liked"/"_disliked" verdicts or
// the permanent "<user>_added" credits: the backend strips both, and they're owned by the thumbs and by
// the acquisition list, not by this editor.
export interface ArtistTags {
  artist: ArtistKey
  present: boolean
  genres: string[]
  styles: string[]
  moods: string[]
}

// The Plex tag fields the editor writes, matching ArtistTagsService's field constants.
export type TagField = 'genre' | 'style' | 'mood'

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
  | 'UpgradeAlbum'
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
  // For an UpgradeAlbum card: what quality the copy already in the library is, so the card can say
  // what it's offering to replace. Null on every other kind.
  ownedQuality: AudioQuality | null
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
  // Deezer's own classification: 'album' | 'ep' | 'single' | 'compilation'. This listing carries every
  // type while the Discover feed takes only albums and EPs, so the badge built from this is what tells
  // a single apart from an LP here. Null for an owned album Deezer doesn't list.
  recordType: string | null
  // Deep link into Plex for an owned album, so the "In library" marker opens the copy we have. Null on
  // a missing album, on one whose Plex rating key isn't captured yet, and when Plex was unreachable.
  plexUrl: string | null
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
// persisted acquisition lifecycle. `kind` is 'RecommendedArtist' (no album), 'MissingAlbum' (a gap
// to fill) or 'UpgradeAlbum' (a better copy of something already held).
export type PurchaseStatus = 'Pending' | 'Queued' | 'Downloading' | 'Sent' | 'InLibrary' | 'Failed'

// Mirror DownloadFailure (IDownloader.cs) — why the last download attempt failed. 'DeezerAuth' and
// 'DeezerCredentialsMissing' are systemic: every queued album fails identically until the ARL in
// streamrip's config is replaced, so a retry is wasted effort and the UI says so instead of offering
// one. The rest are per-album and worth retrying.
export type DownloadFailure =
  | 'None'
  | 'Unknown'
  | 'DeezerAuth'
  | 'DeezerCredentialsMissing'
  | 'NoTracksAvailable'
  // Upgrade-only. 'NoBetterQualityAvailable' means Deezer had nothing better than the copy already
  // held — the album is snoozed rather than retried. 'UpgradeNotPossible' means a better copy came
  // down but couldn't be swapped in (the existing files weren't locatable, or lie outside
  // PLEX_PATH_MAP); the library is untouched and the fix is configuration, not a retry.
  | 'NoBetterQualityAvailable'
  | 'UpgradeNotPossible'

// Mirror ArlUpdateResult (DeezerCredentialService.cs) — the answer to submitting a replacement ARL.
// The token is never echoed back; what comes back is whether Deezer accepted it and who it belongs to.
export interface ArlUpdateResult {
  saved: boolean
  // The Deezer account the new token authenticates as, so a paste can be confirmed as the right
  // account rather than just a well-formed string. Null when Deezer didn't name one.
  accountName: string | null
  // Whether that account can stream lossless — the usual reason downloads keep landing as MP3.
  lossless: boolean
  // How many downloads blocked by the old credential were returned to the queue.
  requeued: number
  // Why it wasn't saved, phrased for the user. Null on success.
  error: string | null
}

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
  // Non-'None' when downloads are blocked by something no retry fixes (a rejected/absent Deezer
  // credential). Drives the one banner on the panel rather than a note on every failed row.
  blocking: DownloadFailure
  // ISO timestamps for when the drainer next acts (null when nothing is scheduled).
  nextItemAt: string | null
  nextBatchAt: string | null
  // When the temporary fast-mode burst lapses, null when it isn't running. Fast mode queues the whole
  // pending list at once instead of one batch per sweep; the pace between albums is unchanged.
  fastUntil: string | null
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
  // Why the last attempt failed; 'None' unless this row is Failed. Cleared on any other transition,
  // so it never explains a failure that has since been retried.
  failure: DownloadFailure
  // Added by hand from a pasted Deezer link rather than derived from a rating — so nothing in the
  // feed wants it and the reconcile must not prune it. Marked in the list, because "remove" is the
  // only way it leaves other than arriving in the library.
  manual: boolean
  // What quality this row will be fetched at — the best entitlement among whoever asked for it.
  targetQuality: AudioQuality | null
  // What the last successful download actually produced, which the fallback ladder means can be
  // below the target. Null until it has downloaded.
  acquiredQuality: AudioQuality | null
  // For an UpgradeAlbum row: what the copy already in the library is. Null on a gap.
  ownedQuality: AudioQuality | null
  // Who asked for this record — pasted its link, or pressed Download on it. Rides the row until the
  // album lands in the library, at which point it becomes the album's permanent "<user>_added" mood in
  // Plex. Null when nothing was pressed (an album downloaded automatically off a like).
  addedBy: string | null
}

// Mirror ManualAddResult / ManualAddOutcome (IPurchaseRepo.cs) — the answer to pasting a Deezer
// album link. Each case says something different back to the user, so they aren't collapsed into
// one boolean.
export type ManualAddResult =
  | 'Added'
  | 'BadLink'
  | 'NotFound'
  | 'AlreadyQueued'
  | 'AlreadyOwned'

export interface ManualAddOutcome {
  result: ManualAddResult
  // The row, for 'Added' and 'AlreadyQueued'; null otherwise.
  item: PurchaseItem | null
}

// The signed-in user, as returned by GET /auth/me (the BFF). Null when not authenticated.
export interface CurrentUser {
  subject: string
  username: string | null
  email: string | null
  displayName: string | null
  // True when this user is in DEV_USERNAMES — unlocks the in-app dev panel.
  isDev: boolean
  // What quality this account's requests download at, already resolved server-side (their own tier
  // if set, else the deployment default) — so the UI never has to know the fallback rule.
  maxQuality: AudioQuality
}

// How good a copy of an album is. Ordered: 'Lossless' beats 'Lossy'. Mirrors AudioQuality on the
// backend, where the same two names are the enum members.
export type AudioQuality = 'Lossy' | 'Lossless'

// One row of the dev panel's per-user quality table (mirrors GET /api/dev/users).
export interface UserQualityEntry {
  subject: string
  username: string | null
  displayName: string | null
  email: string | null
  lastLoginAt: string
  // null when this user has never been given an explicit tier — they follow the deployment default.
  maxQuality: AudioQuality | null
  // What they actually download at: their own tier, or the default when they have none.
  effectiveQuality: AudioQuality
}

export interface UserQualityList {
  defaultQuality: AudioQuality
  users: UserQualityEntry[]
}

// Mirror CollectionItem (Collection.cs) — a record no artist's discography can reach: a
// various-artists compilation, a soundtrack, a cast recording. Deezer credits these to an umbrella
// act whose discography is empty, so the artist-rooted walk behind every other view in the app never
// surfaces one; they're found by naming the record in Browse's Collections view, or pasting its link.
//
// `umbrella` is what makes a like land on the *album* in Plex rather than the artist — "Various
// Artists" is nothing anyone has taste about. Non-umbrella rows still appear in search results (they
// just carry their verdict on the artist, as everywhere else). `deezerAlbumId` is 0 for a collection
// already on the shelf that never came through this app: nothing to download.
export interface CollectionItem {
  deezerAlbumId: number
  title: string
  artist: ArtistKey
  coverUrl: string | null
  link: string | null
  umbrella: boolean
  owned: boolean
  verdict: DiscoveryStatus | null
  year: number | null
  trackCount: number
  recordType: string | null
  plexUrl: string | null
}
