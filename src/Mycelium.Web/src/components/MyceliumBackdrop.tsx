import { useEffect, useRef } from 'react'
import { createSporeField, type SporeFieldOptions } from '../effects/sporeField'
import { createMyceliumField, type MyceliumFieldOptions } from '../effects/myceliumField'
import {
  registerSporeField,
  registerMyceliumField,
  setSporePointer,
  clearSporePointer,
} from '../effects/effectsBus'
import { useBackdropEnabled } from '../effects/backdropPreference'

/**
 * Ambient floating-spore layers with parallax depth:
 *
 *  - "far"  — the calm, dense bulk, drifting BEHIND the content (z-index -1) so
 *             it reads as atmosphere rather than clutter.
 *  - "near" — a sparse handful of larger, faster spores IN FRONT of everything
 *             (z-index 100), softly blurred, to sell depth without being in your
 *             face.
 *
 * Both honour prefers-reduced-motion (single static frame) and pause while the
 * tab is hidden. Mount once near the app root; positioning lives in index.css.
 *
 * The whole backdrop is also switchable — off by default on mobile, and toggled
 * from the topbar (see effects/backdropPreference.ts). Switched off it renders
 * nothing at all, so the simulations are destroyed rather than merely paused.
 */
const FAR: SporeFieldOptions = {
  // Calm bulk. Nudged brighter than the near layer since it reads through the
  // scanline grid and translucent panels.
  alphaScale: 1.3,
}

const NEAR: SporeFieldOptions = {
  density: 2.2, // very few
  maxSpores: 22,
  speed: 16, // closer ⇒ faster parallax
  minRadius: 3,
  maxRadius: 7,
  flareChance: 0.025,
  alphaScale: 0.7,
}

// Mycelium growth layer — slow, deep atmosphere behind everything (see
// effects/myceliumField.ts). Kept dim so it reads as a living substrate the
// spores float over rather than competing with the content.
const GROWTH: MyceliumFieldOptions = {
  alphaScale: 0.85,
}

export default function MyceliumBackdrop() {
  const enabled = useBackdropEnabled()
  const growthRef = useRef<HTMLCanvasElement>(null)
  const farRef = useRef<HTMLCanvasElement>(null)
  const nearRef = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    // Switched off the canvases aren't rendered, so the refs are null and this
    // bails before allocating a field or binding a listener.
    const growthCanvas = growthRef.current
    const farCanvas = farRef.current
    const nearCanvas = nearRef.current
    if (!growthCanvas || !farCanvas || !nearCanvas) return

    const spores = [
      createSporeField(farCanvas, FAR),
      createSporeField(nearCanvas, NEAR),
    ]
    const growth = createMyceliumField(growthCanvas, GROWTH)
    // All layers share resize / reduced-motion / visibility plumbing; only the
    // spore fields register on the effects bus (the bus drives spore reactions).
    const fields = [...spores, growth]
    const unregister = [...spores.map(registerSporeField), registerMyceliumField(growth)]

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)')

    const sync = () => {
      const dpr = window.devicePixelRatio || 1
      for (const f of fields) f.resize(window.innerWidth, window.innerHeight, dpr)
      if (reduceMotion.matches) for (const f of fields) f.renderStatic()
    }

    const onVisibility = () => {
      if (reduceMotion.matches) return
      for (const f of fields) (document.hidden ? f.stop() : f.start())
    }

    // Canvases are fixed at the viewport origin, so client coords map straight
    // to field coords. Skip cursor repulsion under reduced-motion.
    const onPointerMove = (e: PointerEvent) => {
      if (reduceMotion.matches) return
      setSporePointer(e.clientX, e.clientY)
      growth.setPointer(e.clientX, e.clientY)
    }
    const onPointerLeave = () => {
      clearSporePointer()
      growth.clearPointer()
    }

    sync()
    if (!reduceMotion.matches && !document.hidden) for (const f of fields) f.start()

    window.addEventListener('resize', sync)
    document.addEventListener('visibilitychange', onVisibility)
    reduceMotion.addEventListener('change', onVisibility)
    window.addEventListener('pointermove', onPointerMove, { passive: true })
    document.addEventListener('pointerleave', onPointerLeave)
    window.addEventListener('blur', onPointerLeave)

    return () => {
      window.removeEventListener('resize', sync)
      document.removeEventListener('visibilitychange', onVisibility)
      reduceMotion.removeEventListener('change', onVisibility)
      window.removeEventListener('pointermove', onPointerMove)
      document.removeEventListener('pointerleave', onPointerLeave)
      window.removeEventListener('blur', onPointerLeave)
      for (const off of unregister) off()
      for (const f of fields) f.destroy()
    }
  }, [enabled])

  if (!enabled) return null

  return (
    <>
      <canvas ref={growthRef} className="spore-backdrop mycelium-growth" aria-hidden="true" />
      <canvas ref={farRef} className="spore-backdrop spore-far" aria-hidden="true" />
      <canvas ref={nearRef} className="spore-backdrop spore-near" aria-hidden="true" />
    </>
  )
}
