// Takeout: a zip of everything Mycelium has recorded about the signed-in user, in the same layout the
// nightly metadata archive commits. The server takes the identity from the session — there is no
// subject parameter, and so no way to ask for anybody else's.

// What the download would contain. `artists` and `albums` are the whole shared library, which the
// export carries as the frame the rest hangs on; everything below them is the caller's alone.
export interface TakeoutSummary {
  fileName: string
  artists: number
  albums: number
  liked: number
  disliked: number
  indifferent: number
  songRatings: number
  playlists: number
  acquisitions: number
  blocks: number
}

export async function getTakeoutSummary(): Promise<TakeoutSummary> {
  const res = await fetch('/api/takeout/summary')
  if (!res.ok) {
    throw new Error(`Failed to read your data summary: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as TakeoutSummary
}

// A plain link rather than a fetch: the session cookie rides along, the browser handles the save
// dialog and the progress, and nothing has to be held in a blob in the tab's memory first — which
// matters, since a full library runs to tens of thousands of files.
export const TAKEOUT_URL = '/api/takeout'
