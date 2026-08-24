import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  autoLoginCoolingDown,
  clearAuthFlags,
  getMe,
  login,
  logout,
  markAutoLoginAttempt,
  signedOutDeliberately,
} from '../api/auth'
import type { CurrentUser } from '../types'

interface AuthState {
  user: CurrentUser | null
  isLoading: boolean
  /** The session check itself failed (not a 401): we can't tell signed-in from signed-out. */
  isError: boolean
  /** An auto-login redirect is under way — the page is about to be replaced. */
  isRedirecting: boolean
  /** Auto-login was suppressed or didn't take, so the manual "Log in" fallback is the way in. */
  needsManualLogin: boolean
  login: (returnUrl?: string) => void
  logout: () => void
}

const AuthContext = createContext<AuthState | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  // Retry a *failing* session check rather than treating one bad response as final. A 401 isn't a
  // failure (getMe returns null for it, no retry), so this only covers the transient cases — a cold
  // container, a proxy 502 mid-deploy — which previously left the user on the fallback button with
  // no way back except a manual click.
  const { data, isLoading, isError } = useQuery({
    queryKey: ['me'],
    queryFn: getMe,
    staleTime: 5 * 60 * 1000,
    retry: 2,
    retryDelay: (attempt) => Math.min(500 * 2 ** attempt, 4000),
  })

  const user = data ?? null

  // Set the moment we hand the browser to the IdP, so the header shows "Signing in…" instead of
  // flashing the fallback button during the navigation.
  const [isRedirecting, setIsRedirecting] = useState(false)

  // The app sits behind Authentik, so an unauthenticated visitor usually still has an SSO session at
  // the IdP — redirecting straight into the OIDC flow bounces them back signed in with no visible
  // login step. Two things hold it back, and only two: a deliberate logout, and a cooldown that stops
  // a misconfigured round trip becoming a redirect loop. The cooldown expires (see api/auth.ts), so a
  // transient failure costs one page load, not the rest of the session.
  useEffect(() => {
    if (isLoading) return
    if (user) {
      clearAuthFlags()
      return
    }
    // A failed session check can't be read as "signed out" — bouncing through the IdP on a 502 would
    // just land back here, and might well succeed in logging them into nothing.
    if (isError) return
    if (signedOutDeliberately()) return
    if (autoLoginCoolingDown()) return

    markAutoLoginAttempt()
    setIsRedirecting(true)
    login(window.location.pathname + window.location.search)
  }, [user, isLoading, isError])

  // Recomputed each render (the flags are read, not subscribed to) — which is enough, because every
  // transition that changes them also re-renders this provider or navigates away entirely.
  const needsManualLogin =
    !isLoading &&
    !user &&
    !isRedirecting &&
    (isError || signedOutDeliberately() || autoLoginCoolingDown())

  return (
    <AuthContext.Provider
      value={{ user, isLoading, isError, isRedirecting, needsManualLogin, login, logout }}
    >
      {children}
    </AuthContext.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return ctx
}
