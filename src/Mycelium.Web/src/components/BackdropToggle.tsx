import { setBackdropEnabled, useBackdropEnabled } from '../effects/backdropPreference'

// A spore cluster: one bright bloom with smaller ones drifting off it. Switched
// off, a slash crosses it — same read as the muted speaker next door.
function SporeIcon({ on }: { on: boolean }) {
  return (
    <svg
      className="backdrop-icon"
      width="18"
      height="18"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="10" cy="13" r="3.5" fill="currentColor" stroke="none" />
      <circle cx="17" cy="7" r="2" fill="currentColor" stroke="none" opacity={on ? 0.85 : 0.4} />
      <circle cx="18" cy="16" r="1.3" fill="currentColor" stroke="none" opacity={on ? 0.6 : 0.3} />
      <circle cx="6" cy="5.5" r="1.3" fill="currentColor" stroke="none" opacity={on ? 0.6 : 0.3} />
      {!on && <path d="M3 21 21 3" />}
    </svg>
  )
}

/**
 * Topbar switch for the ambient spore/mycelium backdrop. It's three full-screen
 * canvas simulations, which is real CPU — off by default on mobile, and this is
 * how a desktop user gets their fan back (see effects/backdropPreference.ts).
 */
export default function BackdropToggle() {
  const enabled = useBackdropEnabled()

  return (
    <button
      className={enabled ? 'backdrop-toggle is-on' : 'backdrop-toggle'}
      onClick={() => setBackdropEnabled(!enabled)}
      title={
        enabled
          ? 'Background effects on — click to turn off (saves CPU)'
          : 'Background effects off — click to turn on'
      }
      aria-label="Background effects"
      aria-pressed={enabled}
    >
      <SporeIcon on={enabled} />
    </button>
  )
}
