import type { CurrentUser } from '../types'

// Two separate sessionStorage flags, because "the user deliberately signed out" and "the auto-login
// redirect just came back empty" need opposite handling — and they used to share one key, which is
// what killed auto-login: a single logout (or one failed round trip) pinned the flag for the rest of
// the tab's life, and only a *successful* session ever cleared it.
//
// SIGNED_OUT is sticky on purpose: cleared only by an explicit login click, or by turning out to have
// a session anyway. AUTO_LOGIN_AT is a short cooldown that exists purely to break a redirect loop, so
// it expires — a reload a minute later retries instead of stranding the user on the fallback button.
const SIGNED_OUT = 'mc.signedOut'
const AUTO_LOGIN_AT = 'mc.autoLoginAt'
const AUTO_LOGIN_COOLDOWN_MS = 20_000

// sessionStorage throws outright in some privacy modes; auth must not die with it. A browser that
// can't remember the flags just gets the un-suppressed behaviour, which is the safe direction.
function read(key: string): string | null {
  try {
    return sessionStorage.getItem(key)
  } catch {
    return null
  }
}

function write(key: string, value: string): void {
  try {
    sessionStorage.setItem(key, value)
  } catch {
    /* ignore */
  }
}

function drop(key: string): void {
  try {
    sessionStorage.removeItem(key)
  } catch {
    /* ignore */
  }
}

/** True while a just-made auto-login attempt is still inside its loop-breaking cooldown. */
export function autoLoginCoolingDown(): boolean {
  const at = Number(read(AUTO_LOGIN_AT))
  if (!at) return false
  // A restored tab or a clock change can make this negative; anything out of range counts as expired.
  const age = Date.now() - at
  return age >= 0 && age < AUTO_LOGIN_COOLDOWN_MS
}

/** True when the user asked to be signed out — the one case where we must not bounce them back in. */
export function signedOutDeliberately(): boolean {
  return read(SIGNED_OUT) === '1'
}

export function markAutoLoginAttempt(): void {
  write(AUTO_LOGIN_AT, String(Date.now()))
}

/** Called once a session is confirmed: neither suppressor applies any more. */
export function clearAuthFlags(): void {
  drop(SIGNED_OUT)
  drop(AUTO_LOGIN_AT)
}

// Current session from the BFF. The backend answers 401 (not a redirect) when signed out,
// which we surface as null rather than an error — the caller has to be able to tell a real
// "you're signed out" from "the session check itself failed".
export async function getMe(): Promise<CurrentUser | null> {
  const res = await fetch('/auth/me')
  if (res.status === 401) return null
  if (!res.ok) {
    throw new Error(`Failed to load session: ${res.status} ${res.statusText}`)
  }
  return (await res.json()) as CurrentUser
}

// Login/logout are full-page navigations (not fetch): the BFF performs the OIDC redirect dance,
// so the browser must actually leave the SPA and come back.
export function login(returnUrl: string = window.location.pathname): void {
  // An explicit login takes back any earlier "keep me signed out" decision.
  drop(SIGNED_OUT)
  window.location.href = `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`
}

export function logout(): void {
  // Deliberate sign-out: suppress auto-login so we don't yank them straight back in. Unlike the old
  // shared flag, this says nothing about whether the redirect itself works, so it never disables
  // auto-login for a user who simply hit a bad round trip.
  write(SIGNED_OUT, '1')
  drop(AUTO_LOGIN_AT)
  window.location.href = '/auth/logout'
}
