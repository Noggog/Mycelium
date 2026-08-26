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

// Strip every verdict ("_liked"/"_disliked") tag from every artist — moods, plus the legacy
// same-named collections — for a clean slate. The permanent "<user>_added" credits are left alone:
// they're stamped on albums when an acquisition lands, and nothing could put them back.
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

export interface QualitySweepResult {
  artists: number
}

// Re-derive every owned album's audio quality from a full read of the Plex library. Needed once, to
// fill in a library that predates quality tracking; ordinary syncs gap-fill new arrivals after that.
export async function runQualitySweep(): Promise<QualitySweepResult> {
  const res = await fetch('/api/dev/catalog/quality-sweep', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Quality sweep failed: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as QualitySweepResult
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

// ---- The server's own Plex credential ----
// The token every library read is made with, as opposed to the per-user tokens in api/playlists.ts.
// It lives in Mongo once linked (PLEX_TOKEN is only the bootstrap), so it can be re-minted here
// instead of by editing the environment and redeploying.

export type PlexTokenOrigin = 'Linked' | 'Environment' | 'None'

export interface PlexServerTokenStatus {
  configured: boolean
  /** null until something has actually asked Plex — "present" is not the same claim as "works". */
  valid: boolean | null
  origin: PlexTokenOrigin
  username: string | null
  email: string | null
  linkedAt: string | null
  checkedAt: string | null
  problem: string | null
}

export type PlexLinkOutcome = 'linked' | 'pending' | 'expired' | 'noserveraccess' | 'invalidtoken'

export interface PlexServerTokenCompletion {
  outcome: PlexLinkOutcome
  status: PlexServerTokenStatus
}

export async function getPlexServerToken(): Promise<PlexServerTokenStatus> {
  const res = await fetch('/api/dev/plex/server-token')
  if (!res.ok) {
    throw new Error(`Failed to load Plex token status: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexServerTokenStatus
}

// Asks Plex now, rather than reporting the last verdict.
export async function verifyPlexServerToken(): Promise<PlexServerTokenStatus> {
  const res = await fetch('/api/dev/plex/server-token/verify', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to check the Plex token: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexServerTokenStatus
}

export async function startPlexServerTokenLink(forwardUrl?: string): Promise<string> {
  const params = new URLSearchParams()
  if (forwardUrl) params.set('forwardUrl', forwardUrl)
  const res = await fetch(`/api/dev/plex/server-token/start?${params}`, { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to start the Plex link: ${res.status} ${res.statusText}`)
  }
  return ((await res.json()) as { authUrl: string }).authUrl
}

export async function completePlexServerTokenLink(): Promise<PlexServerTokenCompletion> {
  const res = await fetch('/api/dev/plex/server-token/complete', { method: 'POST' })
  if (!res.ok) {
    throw new Error(`Failed to complete the Plex link: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexServerTokenCompletion
}

// A POST body, never a query parameter — the credential must not reach logs, proxies or history.
// 400 carries the outcome too, so a refusal is a verdict rather than an error to re-word.
export async function setPlexServerToken(token: string): Promise<PlexServerTokenCompletion> {
  const res = await fetch('/api/dev/plex/server-token/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  })
  const body = (await res.json().catch(() => null)) as PlexServerTokenCompletion | null
  if (!res.ok && !body) {
    throw new Error(`Failed to set the Plex token: ${res.status} ${res.statusText}`)
  }
  return body!
}

// Forget the stored token and fall back to PLEX_TOKEN, if the environment still sets one.
export async function clearPlexServerToken(): Promise<PlexServerTokenStatus> {
  const res = await fetch('/api/dev/plex/server-token', { method: 'DELETE' })
  if (!res.ok) {
    throw new Error(`Failed to clear the Plex token: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as PlexServerTokenStatus
}
