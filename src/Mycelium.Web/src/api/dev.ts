// Dev-panel endpoints. All are gated server-side by the "DevUser" policy (DEV_USERNAMES), so a
// non-dev hitting them gets a 403 regardless of the UI.

import type { AudioQuality, UserQualityList } from '../types'

// Who is allowed to download lossless. The list comes from the app's own user store, which is
// populated on login — someone who has never signed in won't appear here until they do.
export async function getUserQualities(): Promise<UserQualityList> {
  const res = await fetch('/api/dev/users/')
  if (!res.ok) {
    throw new Error(`Failed to load user qualities: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as UserQualityList
}

// Set one user's ceiling. Pass null to clear it, returning them to the deployment default.
export async function setUserQuality(subject: string, quality: AudioQuality | null): Promise<void> {
  const res = await fetch(`/api/dev/users/${encodeURIComponent(subject)}/quality`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ quality }),
  })
  if (!res.ok) {
    throw new Error(`Failed to set quality: ${res.status} ${res.statusText}`)
  }
}

export interface ClearResult {
  cleared: number
}

export interface ReapplyResult {
  applied: number
}

export interface RebuildResult {
  cleared: number
  applied: number
}

// Strip every managed ("_liked"/"_disliked") tag from every artist — moods, plus the legacy
// same-named collections — for a clean slate.
export async function clearPlexTags(): Promise<ClearResult> {
  const res = await fetch('/api/dev/plex-tags/clear', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to clear Plex tags: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ClearResult
}

// Reapply tags from every user's stored ratings (additive — doesn't remove stale ones).
export async function reapplyPlexTags(): Promise<ReapplyResult> {
  const res = await fetch('/api/dev/plex-tags/reapply', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to reapply Plex tags: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as ReapplyResult
}

// Nuke then reapply — the full reset that brings Plex in line with current ratings.
export async function rebuildPlexTags(): Promise<RebuildResult> {
  const res = await fetch('/api/dev/plex-tags/rebuild', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to rebuild Plex tags: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as RebuildResult
}

// Progress of a whole-library similarity warm (mirrors SimilarityWarmStatus on the backend).
export interface SimilarityWarmStatus {
  running: boolean
  processed: number
  total: number
  errors: number
  currentArtist: string | null
  forceRefresh: boolean
  startedAt: string | null
  finishedAt: string | null
}

// Kick off (or, if already running, just re-read) a whole-catalog warm of every similarity source.
// force=true re-fetches edges even when they're still fresh; default gap-fills only what's missing.
export async function startSimilarityWarm(force: boolean): Promise<SimilarityWarmStatus> {
  const res = await fetch(`/api/dev/similarity/warm?force=${force}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to start similarity warm: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as SimilarityWarmStatus
}

// Poll the in-flight (or last) warm's progress.
export async function getSimilarityWarmStatus(): Promise<SimilarityWarmStatus> {
  const res = await fetch('/api/dev/similarity/warm')
  if (!res.ok) {
    throw new Error(`Failed to get similarity warm status: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as SimilarityWarmStatus
}
