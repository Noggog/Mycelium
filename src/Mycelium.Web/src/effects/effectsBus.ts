/* ============================================================================
   Effects bus — thin imperative bridge to the live spore field(s).

   Components anywhere in the tree (swipe cards, audio player, pointer handlers)
   can call these helpers to drive the backdrop without prop-drilling or context.
   The backdrop runs two parallax layers (far/near), so each helper broadcasts
   to every registered field. Calls are no-ops until a field registers, so
   importing and calling from anywhere is always safe.
   ============================================================================ */

import type { SporeFieldHandle } from './sporeField'
import type { MyceliumFieldHandle } from './myceliumField'

const fields = new Set<SporeFieldHandle>()
const myceliumFields = new Set<MyceliumFieldHandle>()

// Last known cursor position, kept so effects fired from a click (e.g. approve)
// can originate at the pointer without each caller threading coordinates.
let lastX = 0
let lastY = 0

/** Called by MyceliumBackdrop for each layer on mount/unmount. */
export function registerSporeField(handle: SporeFieldHandle): () => void {
  fields.add(handle)
  return () => {
    fields.delete(handle)
  }
}

/** Update the cursor "nutrient" position — spores are repelled from it. */
export function setSporePointer(x: number, y: number) {
  lastX = x
  lastY = y
  for (const f of fields) f.setPointer(x, y)
}

/** Cursor left the window — stop repulsion. */
export function clearSporePointer() {
  for (const f of fields) f.clearPointer()
}

/** Last known cursor position, e.g. for click-triggered bursts. */
export function lastPointer(): { x: number; y: number } {
  return { x: lastX, y: lastY }
}

/** Scatter spores away from a point (cursor, swipe origin). */
export function disturbSpores(x: number, y: number, strength?: number) {
  for (const f of fields) f.disturb(x, y, strength)
}

/** Swirl existing spores in a transient whirlpool around a point. */
export function vortex(x: number, y: number, strength?: number) {
  for (const f of fields) f.vortex(x, y, strength)
}

/** Puff a burst of spores at a point — e.g. on approve. */
export function burstSpores(
  x: number,
  y: number,
  count?: number,
  color?: readonly [number, number, number],
) {
  for (const f of fields) f.burst(x, y, count, color)
}

/** Set global glow/drift intensity 0..1 — e.g. audio level. */
export function setSporeIntensity(level: number) {
  for (const f of fields) f.setIntensity(level)
}

/** Called by MyceliumBackdrop for the growth layer on mount/unmount. */
export function registerMyceliumField(handle: MyceliumFieldHandle): () => void {
  myceliumFields.add(handle)
  return () => {
    myceliumFields.delete(handle)
  }
}

/** Fire a colour flare that races outward along the mycelium strands from a
 *  point — e.g. on approve/reject, at the cursor. No-op until a field registers. */
export function pulseMycelium(
  x: number,
  y: number,
  color?: readonly [number, number, number],
) {
  for (const f of myceliumFields) f.pulse(x, y, color)
}

const APPROVE_FLARE = [255, 209, 148] as const // warm gold
const REJECT_FLARE = [255, 105, 102] as const // red-salmon
const INDIFFERENT_FLARE = [150, 160, 172] as const // cool slate

/**
 * The standard verdict feedback, fired at the last known cursor position:
 * approve swirls the nearby spores and sends a warm gold flare down the mycelium;
 * reject scatters the spores and sends a red flare. Shared by every decision
 * surface (Discover, the artist page, …) so the reaction is consistent. Pass a
 * cleared/neutral toggle through as `null` to skip the feedback.
 *
 * Indifferent gets the flare and nothing else — no vortex, no scatter. It is a
 * real decision, so it has to answer, but the spores are what make approve and
 * reject feel like taking a side, and a shrug that shoved them around would
 * overclaim. A dim slate pulse is the whole reaction, which is the point.
 */
export function rateFeedback(verdict: 'up' | 'down' | 'indifferent' | null) {
  if (!verdict) return
  const { x, y } = lastPointer()
  if (verdict === 'up') {
    vortex(x, y, 1)
    pulseMycelium(x, y, APPROVE_FLARE)
  } else if (verdict === 'indifferent') {
    pulseMycelium(x, y, INDIFFERENT_FLARE)
  } else {
    disturbSpores(x, y, 1.4)
    pulseMycelium(x, y, REJECT_FLARE)
  }
}
