// Small inline SVG icons used by the rate / snooze / clear actions. They draw with `currentColor`
// and stroke, so they inherit the button's neon colour (and any glow comes from the button's CSS).
// Sized in px via `size`; default 18 sits well inside a .disc-btn.

type IconProps = { size?: number; className?: string }

function Svg({ size = 18, className, children }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.3"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden="true"
      focusable="false"
    >
      {children}
    </svg>
  )
}

// Approve — an upward chevron/spark. Reads as "boost / yes", no boomer thumb.
export function IconApprove(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M12 4 5 13h4v7h6v-7h4z" />
    </Svg>
  )
}

// Reject — a downward chevron/spark, the mirror of approve.
export function IconReject(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M12 20 5 11h4V4h6v7h4z" />
    </Svg>
  )
}

// Snooze — a crescent moon.
export function IconMoon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M20 14.2A8 8 0 1 1 9.8 4 6.3 6.3 0 0 0 20 14.2Z" />
    </Svg>
  )
}

// Clear a rating — an eraser/backspace wedge with a cross, distinct from Reject.
export function IconClear(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 5h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9L3 12z" />
      <path d="M14.5 9.5 9.5 14.5M9.5 9.5l5 5" />
    </Svg>
  )
}

// Chevron — the expand/collapse toggle for an artist's album drill-down. Points right when
// collapsed; rotate it via CSS when open.
export function IconChevron(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 6l6 6-6 6" />
    </Svg>
  )
}

// Check — marks an album the library already owns.
export function IconCheck(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M5 13l4 4L19 7" />
    </Svg>
  )
}

// Wrench — the "correct/fix the Deezer association" action.
export function IconWrench(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M14.7 6.3a4 4 0 0 0-5.2 5.2L4 17l3 3 5.5-5.5a4 4 0 0 0 5.2-5.2l-2.6 2.6-2.4-2.4z" />
    </Svg>
  )
}

// A plain close cross — dismiss a dialog.
export function IconX(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M6 6l12 12M18 6 6 18" />
    </Svg>
  )
}

// Block — a stop sign. The down-chevron beside it is the familiar "I don't want this" (a personal
// meh), so a block has to read as a different *kind* of action rather than a stronger flavour of the
// same one: an octagon shares no line with the chevrons, and the bar across it keeps it unmistakable
// at 15px, where a bare octagon would just look like a circle.
export function IconBlock(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M8.8 4.2h6.4l4.6 4.6v6.4l-4.6 4.6H8.8l-4.6-4.6V8.8z" />
      <path d="M8.5 12h7" />
    </Svg>
  )
}

// Download — an arrow into a tray. The "Download now" action on a queued album.
export function IconDownload(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M12 3v11M8 10l4 4 4-4" />
      <path d="M5 20h14" />
    </Svg>
  )
}

// Undo — a curved arrow doubling back, the "move an ordered album back to queued" action.
export function IconUndo(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 7 4 12l5 5" />
      <path d="M4 12h11a5 5 0 0 1 0 10h-1" />
    </Svg>
  )
}

// A spinning ring for "still waiting on Deezer" states. Worth a moving element rather than a line of
// text: a static "Loading albums…" is easy to read as a settled (and empty) answer, which is exactly
// the confusion an upstream call that takes seconds — or is being retried past a rate limit — creates.
export function Spinner({ size = 14, className }: IconProps) {
  return (
    <span
      className={className ? `inline-spinner ${className}` : 'inline-spinner'}
      style={{ width: size, height: size }}
      role="status"
      aria-label="Loading"
    />
  )
}
