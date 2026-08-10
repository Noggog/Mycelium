import { useEffect, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { getMergeCandidates, mergeAlbum } from '../api/discovery'
import { IconCheck, IconX } from './icons'

// "Already in library?" — resolve by hand a release the diff calls missing but the library already
// has. Two shapes of mismatch land here: a near-miss title the normalizer can't collapse (Deezer's
// "DOOM (Original Game Soundtrack)" vs. Plex's "Doom: Original Game Soundtrack"), and a copy filed
// under a different act (Plex's "Matthewdavid's Mindflight" vs. Deezer's "Matthewdavid"). The pane
// opens on the suggestions for both cases; the search box spans the whole library when neither fits.
//
// Picking one records a durable merge, so the album leaves the download queue, stops showing as
// missing in Browse and Discover, and is never downloaded automatically. Shared by all three pages.
export function MergeAlbumPane({
  artist,
  album,
  onClose,
  onMerged,
}: {
  artist: string
  album: string
  onClose: () => void
  onMerged: () => void
}) {
  const [query, setQuery] = useState('')
  // Debounced so typing a title doesn't fire a library query per keystroke.
  const [search, setSearch] = useState('')
  useEffect(() => {
    const id = window.setTimeout(() => setSearch(query.trim()), 250)
    return () => window.clearTimeout(id)
  }, [query])

  const candidates = useQuery({
    queryKey: ['merge-candidates', artist, album, search],
    queryFn: () => getMergeCandidates(artist, album, search),
  })
  const merge = useMutation({
    mutationFn: (libraryAlbum: string) => mergeAlbum(artist, album, libraryAlbum),
    onSuccess: onMerged,
  })

  const options = candidates.data ?? []

  return (
    <div className="picker-backdrop" onClick={onClose}>
      <div className="picker-panel" onClick={(e) => e.stopPropagation()}>
        <div className="picker-head">
          <h2>Match Existing Album</h2>
          <button className="disc-btn" title="Close" onClick={onClose}>
            <IconX />
          </button>
        </div>
        <p className="picker-pinned">
          Merge “{album}” ({artist}) into an album already in your library.
        </p>

        <input
          className="picker-search"
          type="text"
          value={query}
          placeholder="Search your library by album or artist…"
          onChange={(e) => setQuery(e.target.value)}
        />

        {candidates.isPending && <p><em>Loading library…</em></p>}
        {candidates.isError && <p className="error">Failed to load library albums.</p>}
        {candidates.data && options.length === 0 && (
          <p>
            <em>
              {search
                ? `Nothing in the library matches “${search}”.`
                : `No likely match in the library — search for the album you already have.`}
            </em>
          </p>
        )}

        <ul className="picker-results">
          {options.map((o) => (
            <li key={`${o.artist}::${o.album}`} className="picker-result">
              <div className="picker-meta">
                <span className="picker-name">{o.album}</span>
                <span className="picker-sub">{o.artist}</span>
              </div>
              {/* Check the suggestion against the copy you actually own before merging into it — a
                  near-miss title is only worth merging if it really is the same record. A new tab so
                  the pane (and everything behind it) stays put. Absent when the album's Plex key
                  isn't captured yet, or Plex couldn't be reached. */}
              {o.plexUrl && (
                <a
                  className="deezer-link"
                  href={o.plexUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  title={`Open “${o.album}” in Plex`}
                >
                  Plex ↗
                </a>
              )}
              <button
                className="disc-btn up"
                title="Merge into this album"
                disabled={merge.isPending}
                onClick={() => merge.mutate(o.album)}
              >
                <IconCheck />
              </button>
            </li>
          ))}
        </ul>

        {merge.isError && <p className="error">Merge failed — try again.</p>}
      </div>
    </div>
  )
}
