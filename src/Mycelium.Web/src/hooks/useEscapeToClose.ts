import { useEffect, useRef } from 'react'

// Escape closes the modal pickers. Worth its own hook because these panes autofocus their search
// box on open: the keyboard is already where the work happens, so needing the mouse purely to back
// out is the one step that breaks the flow. Bound on window while the pane is mounted, so it fires
// no matter which control inside has focus.
//
// (The auth menu in Layout.tsx keeps its own handler — it closes on outside-click too, and the two
// are the same dismissal there.)
export function useEscapeToClose(onClose: () => void): void {
  // Every call site passes an inline arrow, so the handler is a new function on each render. Held in
  // a ref and read at keypress time, the listener binds once per open instead of once per render.
  const handler = useRef(onClose)
  handler.current = onClose

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') handler.current()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
}
