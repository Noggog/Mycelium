import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  completePlexLink,
  getPlexLink,
  startPlexLink,
  unlinkPlex,
  type PlexLinkStatus,
} from '../api/playlists'
import { useAuth } from './AuthContext'

const UNLINKED: PlexLinkStatus = { linked: false, username: null, email: null, linkedAt: null }

export interface PlexLinkController {
  status: PlexLinkStatus | undefined
  isLoading: boolean
  error: Error | null
  /** The approval round trip is in flight — the user is in the other tab. */
  waiting: boolean
  /** The click has been made but the auth URL hasn't come back yet. */
  starting: boolean
  /** Set only when the browser blocked the approval tab, so the user can open it by hand. */
  fallbackUrl: string | null
  /** Whatever went wrong with the link attempt, in words. */
  problem: string | null
  connect: (forwardUrl?: string) => Promise<void>
  cancel: () => void
  disconnect: () => void
  disconnecting: boolean
  disconnectError: Error | null
}

/**
 * The plex.tv PIN flow, shared by the header's connect button and the Playlists page so both drive
 * one state machine against one cached ['plex-link'] query — connect in the header and the page
 * updates with it.
 *
 * Everything here acts as the user's *own* Plex account rather than the server's, because playlists,
 * star ratings and play history are all per-account in Plex.
 */
export function usePlexLink(): PlexLinkController {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const [waiting, setWaiting] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [fallbackUrl, setFallbackUrl] = useState<string | null>(null)
  // The approval happens in another tab; this holds it so we can close it once the link lands.
  const authTab = useRef<Window | null>(null)

  // The endpoint is auth-gated, so there's nothing to ask until we know who's asking.
  const link = useQuery<PlexLinkStatus>({
    queryKey: ['plex-link'],
    queryFn: getPlexLink,
    enabled: !!user,
  })

  // While a link is in flight, ask the server whether plex.tv has handed over a token yet. The user
  // is approving in another tab, so there's nothing to react to here except the clock.
  useEffect(() => {
    if (!waiting) return
    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const { outcome, status } = await completePlexLink()
        if (cancelled || outcome === 'pending') return

        setWaiting(false)
        if (outcome === 'linked') {
          queryClient.setQueryData(['plex-link'], status)
          queryClient.invalidateQueries({ queryKey: ['stock-playlists'] })
          authTab.current?.close()
        } else if (outcome === 'noserveraccess') {
          setProblem("That Plex account can't see this server's music library.")
        } else {
          setProblem('Timed out — try again.')
        }
      } catch (e) {
        if (!cancelled) {
          setWaiting(false)
          setProblem((e as Error).message)
        }
      }
    }, 2000)

    // Plex codes last about half an hour, but a tab left open that long is abandoned, not pending.
    const giveUp = setTimeout(() => {
      if (!cancelled) {
        setWaiting(false)
        setProblem('Timed out — try again.')
      }
    }, 5 * 60 * 1000)

    return () => {
      cancelled = true
      clearInterval(timer)
      clearTimeout(giveUp)
    }
  }, [waiting, queryClient])

  // The approval tab is opened *synchronously* on the click, before the round trip that fetches the
  // URL. A tab opened in the promise continuation is a popup as far as the browser is concerned, and
  // gets blocked — so it's opened blank up front and navigated once the URL arrives. If it was blocked
  // anyway, we fall back to a link the user clicks themselves.
  const connect = async (forwardUrl?: string) => {
    setProblem(null)
    setFallbackUrl(null)
    const tab = window.open('about:blank', '_blank')
    setStarting(true)
    try {
      const authUrl = await startPlexLink(forwardUrl ?? window.location.href)
      if (tab && !tab.closed) {
        // Severs the opener reference before handing the tab to plex.tv.
        tab.opener = null
        tab.location.href = authUrl
        authTab.current = tab
      } else {
        setFallbackUrl(authUrl)
      }
      setWaiting(true)
    } catch (e) {
      tab?.close()
      setProblem((e as Error).message)
    } finally {
      setStarting(false)
    }
  }

  const disconnect = useMutation({
    mutationFn: unlinkPlex,
    onSuccess: () => {
      queryClient.setQueryData(['plex-link'], UNLINKED)
      queryClient.invalidateQueries({ queryKey: ['stock-playlists'] })
    },
  })

  return {
    status: link.data,
    isLoading: link.isLoading,
    error: (link.error as Error) ?? null,
    waiting,
    starting,
    fallbackUrl,
    problem,
    connect,
    cancel: () => setWaiting(false),
    disconnect: () => disconnect.mutate(),
    disconnecting: disconnect.isPending,
    disconnectError: (disconnect.error as Error) ?? null,
  }
}
