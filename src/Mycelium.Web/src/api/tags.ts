import type { ArtistTags, TagField } from '../types'

// The Browse readout's "Tags" tab: read/edit the descriptor tags a library artist carries in Plex
// (genres, styles, moods). Auth-gated server-side — these writes land in the shared Plex library.
// The app's own like/dislike verdict moods never appear here and can't be edited through it.

export async function getArtistTags(artist: string): Promise<ArtistTags> {
  const params = new URLSearchParams({ artist })
  const res = await fetch(`/api/artists/tags?${params}`)
  if (!res.ok) {
    throw new Error(`Failed to load tags for ${artist}: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ArtistTags
}

// Add and/or remove one tag on one field, returning the artist's tags as they now stand. The write is
// a delta in Plex, so the field's other tags (including the hidden verdict moods) are left alone.
export async function editArtistTag(
  artist: string,
  field: TagField,
  edit: { add?: string; remove?: string },
): Promise<ArtistTags> {
  const params = new URLSearchParams({ artist, field })
  if (edit.add) params.set('add', edit.add)
  if (edit.remove) params.set('remove', edit.remove)
  const res = await fetch(`/api/artists/tags?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to edit ${field} for ${artist}: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ArtistTags
}
