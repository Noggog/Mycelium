import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  clearCollectionRating,
  getCollections,
  rateCollection,
  rateOwnedCollection,
  resolveCollection,
  searchCollections,
} from '../api/collections'
import type { Verdict } from '../api/discovery'
import { isDeezerBusy } from '../api/deezer'
import { useDebounced } from '../hooks/useDebounced'
import { rateFeedback } from '../effects/effectsBus'
import type { CollectionItem, DiscoveryStatus } from '../types'
import { IconApprove, IconReject, Spinner } from './icons'

// Collections are the records the rest of the app structurally cannot show you. Every other view is
// reached *through* an artist — the catalog lists owned acts, the feed grows from the ones you like,
// the missing-album diff walks each owned artist's discography — and a compilation is credited to an
// umbrella ("Various Artists", "Original Soundtrack", a cast recording) whose discography is empty.
// So there is no walk that ends at one; you have to name the record. That's what these two views are:
// a results block under the artist search, and a tab listing what you own or have already judged.

const verdictStatus = (v: Verdict): DiscoveryStatus => (v === 'up' ? 'Liked' : 'Disliked')

// The queries a verdict invalidates: the collection views themselves, the ratings list behind the
// artist rows, and the buy list a like lands on.
const AFFECTED = ['collections', 'collection-search', 'collection-resolve', 'ratings', 'purchases']

function CollectionCover({ item }: { item: CollectionItem }) {
  if (item.coverUrl) {
    return <img className="disc-avatar" src={item.coverUrl} alt="" width={52} height={52} loading="lazy" />
  }
  return (
    <div className="disc-avatar disc-avatar-fallback" style={{ width: 52, height: 52, fontSize: 21 }}>
      {item.title.charAt(0).toUpperCase()}
    </div>
  )
}

// The sub-line under a collection's title: who it's credited to, when, how big, and what Deezer calls
// it. Everything is optional — a search hit carries no release date, an owned row that never came
// through this app carries almost nothing — so each part is dropped rather than shown empty.
function CollectionMeta({ item }: { item: CollectionItem }) {
  const parts = [
    item.artist.artistName,
    item.year ? String(item.year) : null,
    item.trackCount > 0 ? `${item.trackCount} track${item.trackCount === 1 ? '' : 's'}` : null,
  ].filter(Boolean) as string[]

  return (
    <div className="genre-tags">
      <span className="genre-tag">{parts.join(' · ')}</span>
      {/* The badge that explains why this row exists at all: nothing else in the app can reach it. */}
      {item.umbrella && (
        <span className="genre-tag" title="Credited to an umbrella, not an act — no artist page lists it">
          collection
        </span>
      )}
      {item.recordType && item.recordType.toLowerCase() !== 'album' && (
        <span className="genre-tag">{item.recordType}</span>
      )}
    </div>
  )
}

/**
 * One collection, with its thumbs. Clicking the verdict already set clears it, matching every other
 * rate control in the app.
 *
 * A like takes one of two paths depending on whether the record is already on the shelf. An unowned
 * one is rated by its Deezer id, which is what carries it through the purchase reconcile to the
 * downloader; an owned one that never came through this app has no id at all, so it goes through the
 * ordinary album-rating endpoint. Either way an umbrella-credited record gets its verdict stamped on
 * the *album* in Plex, since "Various Artists" is not something anyone has taste about.
 */
function CollectionRow({ item }: { item: CollectionItem }) {
  const queryClient = useQueryClient()

  const invalidate = () => {
    for (const key of AFFECTED) queryClient.invalidateQueries({ queryKey: [key] })
  }

  const mutate = useMutation({
    mutationFn: async (verdict: Verdict) => {
      if (item.verdict === verdictStatus(verdict)) {
        return clearCollectionRating(item.artist.artistName, item.title)
      }
      if (item.deezerAlbumId > 0) {
        await rateCollection(item.deezerAlbumId, verdict)
        return
      }
      return rateOwnedCollection(item.artist.artistName, item.title, item.coverUrl, verdict)
    },
    onMutate: (verdict) => rateFeedback(item.verdict === verdictStatus(verdict) ? null : verdict),
    onSuccess: invalidate,
  })

  return (
    <div className="disc-row">
      <CollectionCover item={item} />
      <div className="disc-row-main">
        <div className="disc-name">{item.title}</div>
        <CollectionMeta item={item} />
      </div>
      <div className="disc-actions">
        {item.owned &&
          (item.plexUrl ? (
            <a
              className="album-owned album-owned-link"
              href={item.plexUrl}
              target="_blank"
              rel="noopener noreferrer"
              title="Open in Plex"
            >
              In library
            </a>
          ) : (
            <span className="album-owned">In library</span>
          ))}
        {item.link && (
          <a className="deezer-link" href={item.link} target="_blank" rel="noopener noreferrer">
            Deezer ↗
          </a>
        )}
        <button
          className={item.verdict === 'Liked' ? 'disc-btn up active' : 'disc-btn up'}
          title={item.verdict === 'Liked' ? 'Clear rating' : 'Approve'}
          disabled={mutate.isPending}
          onClick={() => mutate.mutate('up')}
        >
          <IconApprove />
        </button>
        <button
          className={item.verdict === 'Disliked' ? 'disc-btn down active' : 'disc-btn down'}
          title={item.verdict === 'Disliked' ? 'Clear rating' : 'Reject'}
          disabled={mutate.isPending}
          onClick={() => mutate.mutate('down')}
        >
          <IconReject />
        </button>
      </div>
      {mutate.isError && <p className="error">{(mutate.error as Error).message}</p>}
    </div>
  )
}

/**
 * The "Albums & collections" block under Browse's artist search: Deezer album hits for whatever is in
 * the search box, umbrella-credited ones first.
 *
 * Debounced like the artist search beside it, and for the same reason — Deezer answers a burst with an
 * error the client can only read as "no such record", so an undebounced box makes the album you're
 * typing look like it doesn't exist.
 */
export function CollectionResults({ query }: { query: string }) {
  const trimmed = query.trim()
  const searched = useDebounced(trimmed)

  const search = useQuery({
    queryKey: ['collection-search', searched],
    queryFn: () => searchCollections(searched),
    enabled: searched.length > 0,
    staleTime: 5 * 60 * 1000,
  })

  if (trimmed.length === 0) return null

  // Mid-debounce the rows below still belong to the previous query, so read as busy until the search
  // catches up rather than flashing "nothing found" at a half-typed title.
  const searching = search.isFetching || searched !== trimmed
  const results = search.data ?? []

  return (
    <div className="uncataloged-results">
      <div className="uncataloged-head">
        Albums &amp; collections
        {searching && (
          <span className="artist-search-count"><Spinner size={11} /> searching Deezer…</span>
        )}
      </div>

      {search.isError && (
        <p className="disc-sub-note">
          <em>{isDeezerBusy(search.error) ? 'Deezer is busy — try again in a moment.' : 'Album search failed.'}</em>
        </p>
      )}

      {!searching && !search.isError && results.length === 0 && (
        <p className="disc-sub-note"><em>No albums on Deezer for “{searched}”.</em></p>
      )}

      <div className="disc-list">
        {results.map((item) => (
          <CollectionRow key={item.deezerAlbumId} item={item} />
        ))}
      </div>
    </div>
  )
}

/**
 * The Collections tab: everything you own or have already judged, plus a paste box for a record even
 * Deezer's search won't put in front of you.
 *
 * Owned-but-unrated rows are the reason the list exists rather than just the search. A compilation
 * sitting in the library is invisible to the rest of the app — no artist page lists it, no feed offers
 * it — so without this there is no way to say you like something you already have, and it could never
 * reach a "My Library" playlist.
 */
export function CollectionsView({ query }: { query: string }) {
  const [pasted, setPasted] = useState('')
  const [resolved, setResolved] = useState<CollectionItem | null>(null)
  const [pasteError, setPasteError] = useState<string | null>(null)

  const { data, isPending, isError, error } = useQuery({
    queryKey: ['collections'],
    queryFn: getCollections,
  })

  const resolve = useMutation({
    mutationFn: () => resolveCollection(pasted),
    onSuccess: (item) => {
      setResolved(item)
      setPasteError(item ? null : 'No Deezer album in that link.')
      if (item) setPasted('')
    },
    onError: (e: Error) => setPasteError(e.message),
  })

  const filter = query.trim().toLowerCase()
  const listed = (data ?? []).filter(
    (c) =>
      filter.length === 0
      || c.title.toLowerCase().includes(filter)
      || c.artist.artistName.toLowerCase().includes(filter),
  )

  return (
    <div className="disc-main">
      <p className="disc-sub-note collections-intro">
        Compilations, soundtracks and cast recordings — records credited to “Various Artists” rather
        than to a band, which no artist page can lead you to. Thumb one up to queue it and mark it as
        yours; the tag lands on the album, since there is no artist to put it on.
      </p>

      <form
        className="collection-paste"
        onSubmit={(e) => {
          e.preventDefault()
          if (pasted.trim()) resolve.mutate()
        }}
      >
        <input
          type="text"
          value={pasted}
          placeholder="Paste a Deezer album link…"
          onChange={(e) => {
            setPasted(e.target.value)
            setPasteError(null)
          }}
        />
        <button className="disc-btn" type="submit" disabled={!pasted.trim() || resolve.isPending}>
          {resolve.isPending ? <Spinner size={13} /> : 'Look up'}
        </button>
      </form>
      {pasteError && <p className="disc-sub-note"><em>{pasteError}</em></p>}

      {resolved && (
        <div className="uncataloged-results">
          <div className="uncataloged-head">From your link</div>
          <div className="disc-list">
            <CollectionRow key={resolved.deezerAlbumId} item={resolved} />
          </div>
        </div>
      )}

      {/* Searching from here reaches all of Deezer; the list below is only what's already yours. */}
      <CollectionResults query={query} />

      <div className="uncataloged-results">
        <div className="uncataloged-head">
          Yours
          {data && data.length > 0 && <span className="artist-search-count">{listed.length}</span>}
        </div>

        {isPending && <p className="disc-sub-note"><em>Loading…</em></p>}
        {isError && <p className="error">Failed to load collections: {(error as Error).message}</p>}

        {data && listed.length === 0 && (
          <p className="disc-sub-note">
            <em>
              {data.length === 0
                ? 'No collections yet — search above, or paste a Deezer album link.'
                : `No collections match “${query.trim()}”.`}
            </em>
          </p>
        )}

        <div className="disc-list">
          {listed.map((item) => (
            <CollectionRow key={`${item.artist.artistName}/${item.title}`} item={item} />
          ))}
        </div>
      </div>
    </div>
  )
}
