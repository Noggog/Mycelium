import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { getDeezerAlbumPlayInfo, getDeezerPlayInfo, isDeezerBusy } from '../api/deezer'
import { Spinner } from './icons'
import { getVolume, useVolume } from '../audio/volume'
import { startAudioReactive, stopAudioReactive } from '../effects/audioReactive'

// Only one preview should play at a time across the whole page; track the active element so
// starting a new one pauses the previous (e.g. expanding a second row in the list view).
let currentAudio: HTMLAudioElement | null = null

// Plays 30-second Deezer previews in a plain <audio> (no iframe/login/cookies, works on mobile) and
// links out to the full Deezer page. Sample an artist's top tracks with `artist`, or a specific
// album's tracks with `albumId`. Playback only ever starts from a user clicking a track.
export function DeezerSample({ artist, albumId }: { artist?: string; albumId?: number }) {
  const isAlbum = albumId != null
  // Two queries, one enabled at a time. The artist query shares its cache entry (key + fetcher) with
  // the feed avatar's image lookup, so expanding the player after the photo loaded is instant.
  const artistQuery = useQuery({
    queryKey: ['deezer-play', artist],
    queryFn: () => getDeezerPlayInfo(artist!),
    enabled: !isAlbum && !!artist,
    staleTime: 60 * 60 * 1000,
  })
  const albumQuery = useQuery({
    queryKey: ['deezer-album', albumId],
    queryFn: () => getDeezerAlbumPlayInfo(albumId!),
    enabled: isAlbum,
    staleTime: 60 * 60 * 1000,
  })
  const queryClient = useQueryClient()
  const queryKey = isAlbum ? ['deezer-album', albumId] : ['deezer-play', artist]
  const { data, isPending, isError, error, isFetching, refetch } = isAlbum ? albumQuery : artistQuery
  const link = data ? ('albumLink' in data ? data.albumLink : data.artistLink) : null
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [playing, setPlaying] = useState(false)
  // The track index whose preview failed to load/play, so we can flag it instead of failing
  // silently (a dead or geo-blocked Deezer CDN url otherwise just does nothing on click).
  const [failed, setFailed] = useState<number | null>(null)
  const volume = useVolume()

  // Keep the live element in sync while the global slider moves mid-playback.
  useEffect(() => {
    if (audioRef.current) audioRef.current.volume = volume
  }, [volume])

  // Point the element at a preview url and start it. Returns the play() promise so callers can react
  // to a load failure. The Web Audio tap is (re)started here, inside the click gesture, so the
  // AudioContext is allowed to start (doing it from the onPlay media event can leave it suspended).
  const start = (el: HTMLAudioElement, url: string, index: number) => {
    if (currentAudio && currentAudio !== el) currentAudio.pause()
    currentAudio = el
    el.volume = getVolume()
    el.src = url
    setSelected(index)
    setFailed(null)
    startAudioReactive(el)
    return el.play()
  }

  const play = (index: number) => {
    const el = audioRef.current
    const track = data?.tracks[index]
    if (!el || !track) return
    start(el, track.previewUrl, index).catch((err) => onPlayError(index, track.title, err, true))
  }

  // A play() rejection: a blocked-autoplay one is benign (the click is the gesture, so it's rare).
  // Anything else is a bad preview url — overwhelmingly a Deezer signed url that expired while the
  // readout sat open (their tokens live ~15 min). Re-fetch fresh urls once and retry this track
  // before giving up; only flag it unavailable if the fresh url fails too.
  const onPlayError = async (index: number, title: string, err: unknown, allowRefresh: boolean) => {
    if ((err as { name?: string })?.name === 'NotAllowedError') return
    if (allowRefresh) {
      try {
        const fresh = isAlbum
          ? await getDeezerAlbumPlayInfo(albumId!, true)
          : await getDeezerPlayInfo(artist!, true)
        const el = audioRef.current
        const track = fresh?.tracks[index]
        if (el && track) {
          // Repaint the list with the fresh urls so other tracks benefit from the new tokens too.
          queryClient.setQueryData(queryKey, fresh)
          start(el, track.previewUrl, index).catch((e) => onPlayError(index, title, e, false))
          return
        }
      } catch {
        /* fall through to flagging it */
      }
    }
    console.warn(`Deezer preview failed to play: ${title}`, err)
    setFailed(index)
  }

  const toggle = (index: number) => {
    if (selected === index && playing) audioRef.current?.pause()
    else play(index)
  }

  // Stop our audio (and release the "current" slot) when this player unmounts.
  useEffect(() => {
    return () => {
      const el = audioRef.current
      if (el) el.pause()
      if (currentAudio === el) currentAudio = null
    }
  }, [])

  if (isPending) {
    return <span className="deezer-sample muted disc-sub-busy"><Spinner size={12} /> Loading Deezer…</span>
  }
  // "Not on Deezer" is a verdict — Deezer answered and has no such artist. A failed call is not that,
  // and rendering it as that made a momentary rate-limit look permanent (the client caches it, so it
  // survived until a reload). Say which one it is, and offer the retry.
  if (isError) {
    return (
      <span className="deezer-sample muted disc-sub-busy">
        {isFetching ? <Spinner size={12} /> : null}
        {isDeezerBusy(error) ? 'Deezer is busy — try again in a moment.' : 'Couldn’t reach Deezer.'}
        {!isFetching && (
          <button className="disc-btn" onClick={() => refetch()}>Retry</button>
        )}
      </span>
    )
  }
  if (!data) {
    return <span className="deezer-sample muted">Not on Deezer</span>
  }

  return (
    <div className="deezer-sample">
      <div className="sample-head">
        <span className="sample-label">{isAlbum ? 'Album tracks' : 'Top tracks'}</span>
        <a className="deezer-link" href={link ?? undefined} target="_blank" rel="noopener noreferrer">
          Deezer ↗
        </a>
      </div>

      {data.tracks.length === 0 ? (
        <span className="sample-title muted">No previews available</span>
      ) : (
        <ul className="sample-list">
          {data.tracks.map((track, i) => (
            <li key={i}>
              <button
                className="sample-btn"
                onClick={() => toggle(i)}
                aria-label={selected === i && playing ? `Pause ${track.title}` : `Play ${track.title}`}
              >
                {selected === i && playing ? '⏸' : '▶'}
              </button>
              <span className={selected === i ? 'sample-title active' : 'sample-title'} title={track.title}>
                {track.title}
              </span>
              {failed === i && <span className="sample-title muted" title="Preview unavailable"> — unavailable</span>}
            </li>
          ))}
        </ul>
      )}

      <audio
        ref={audioRef}
        preload="none"
        onPlay={() => setPlaying(true)}
        onPause={() => {
          setPlaying(false)
          stopAudioReactive()
        }}
        onEnded={() => {
          setPlaying(false)
          stopAudioReactive()
        }}
        // A load/decode error settles the field; the failure itself is flagged (and a fresh-url retry
        // attempted) from the play() rejection in onPlayError, which is the single source of truth —
        // flagging here too would race that retry and flash "unavailable" just as it recovers.
        onError={() => {
          setPlaying(false)
          stopAudioReactive()
        }}
      />
    </div>
  )
}
