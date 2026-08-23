import { useEffect, useState } from 'react'

// Holds a value back until it stops changing, so a search box drives one request per pause rather
// than one per keystroke. That matters for the source searches specifically: they proxy to Deezer
// (~50 calls per 5s per IP, shared by every user behind this backend) and MusicBrainz (1/s), and
// both answer a burst with an error the caller can only read as "nothing found" — so an undebounced
// box doesn't just waste calls, it makes the artist you're typing look like it doesn't exist.
export function useDebounced<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs)
    return () => clearTimeout(timer)
  }, [value, delayMs])

  return debounced
}
