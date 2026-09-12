import { useSyncExternalStore } from 'react'

/* ============================================================================
   Backdrop on/off preference.

   The spore + mycelium canvases are three full-screen simulations running at
   animation-frame rate; on a phone that is the single most expensive thing the
   app does, and the pointer reactions they exist for aren't even reachable
   without a mouse. So the default is OFF on mobile and ON on desktop, and the
   topbar toggle lets anyone override it either way (persisted, like volume).
   ============================================================================ */

const KEY = 'mycelium.backdrop'

/**
 * Is this a machine we should light the backdrop up on by default?
 *
 * Narrow viewport ⇒ phone. Coarse pointer ⇒ touch, where the cursor-follow and
 * repulsion have nothing to follow, so the cost buys nothing. 961px matches the
 * desktop breakpoint the rest of the app already switches layouts at.
 */
function defaultEnabled(): boolean {
  if (typeof window === 'undefined' || !window.matchMedia) return true
  return (
    window.matchMedia('(min-width: 961px)').matches &&
    !window.matchMedia('(pointer: coarse)').matches
  )
}

function read(): boolean {
  try {
    const raw = localStorage.getItem(KEY)
    if (raw === '1') return true
    if (raw === '0') return false
  } catch {
    // localStorage can throw outright in private mode / with storage disabled.
  }
  return defaultEnabled()
}

let enabled = read()
const listeners = new Set<() => void>()

export function getBackdropEnabled(): boolean {
  return enabled
}

export function setBackdropEnabled(v: boolean): void {
  enabled = v
  try {
    localStorage.setItem(KEY, v ? '1' : '0')
  } catch {
    // Not persisting is survivable; the toggle still holds for this session.
  }
  listeners.forEach((l) => l())
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export function useBackdropEnabled(): boolean {
  return useSyncExternalStore(subscribe, getBackdropEnabled, getBackdropEnabled)
}
