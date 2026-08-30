import { useEffect, useRef, useState, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { usePlexLink } from '../auth/usePlexLink'
import VolumeControl from './VolumeControl'
import MyceliumBackdrop from './MyceliumBackdrop'

const navClass = ({ isActive }: { isActive: boolean }) =>
  isActive ? 'nav-link active' : 'nav-link'

// The header's identity slot. Signing into the app itself is normally invisible — AuthProvider
// redirects an unauthenticated visitor straight through Authentik — so the slot's real job is the
// *Plex* account, which is the one the user has to connect by hand and the one that decides whose
// star ratings and playlists the app acts on. The app-level "Log in" button survives only as the
// fallback for when auto-login couldn't run (a deliberate logout, or a failed round trip).
function AuthBox() {
  const { user, isLoading, isRedirecting, needsManualLogin, login, logout } = useAuth()
  const plex = usePlexLink()
  const [menuOpen, setMenuOpen] = useState(false)
  const [tokenOpen, setTokenOpen] = useState(false)
  const [token, setToken] = useState('')
  const [label, setLabel] = useState('')
  const boxRef = useRef<HTMLDivElement>(null)

  const closeMenu = () => {
    setMenuOpen(false)
    setTokenOpen(false)
    setToken('')
    setLabel('')
  }

  // Close the menu on an outside click or Escape. Bound only while it's open, so the listeners cost
  // nothing in the state the header spends virtually all its time in.
  useEffect(() => {
    if (!menuOpen) return
    const onDown = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) closeMenu()
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') closeMenu()
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [menuOpen])

  if (isLoading || isRedirecting) {
    return (
      <div className="auth-box">
        <span className="auth-name">{isRedirecting ? 'Signing in…' : '…'}</span>
      </div>
    )
  }

  if (!user) {
    // Only reachable when auto-login was suppressed or failed; otherwise the redirect has already
    // taken the page and there's nothing to render.
    return (
      <div className="auth-box">
        {needsManualLogin && (
          <button className="auth-btn" onClick={() => login()}>Log in</button>
        )}
      </div>
    )
  }

  const linked = plex.status?.linked === true
  const appName = user.displayName ?? user.username ?? user.email ?? 'Signed in'

  return (
    <div className="auth-box" ref={boxRef}>
      {linked ? (
        <button
          className="auth-chip"
          onClick={() => (menuOpen ? closeMenu() : setMenuOpen(true))}
          title={`Authentik: ${appName} · Plex: ${plex.status!.username ?? 'connected'}`}
          aria-expanded={menuOpen}
        >
          <span className="auth-chip-mark" aria-hidden="true">⬡</span>
          <span className="auth-name">{plex.status!.username ?? 'Plex'}</span>
          <span className="auth-chip-caret" aria-hidden="true">▾</span>
        </button>
      ) : (
        <>
          <button
            className="auth-btn"
            onClick={() => plex.connect()}
            disabled={plex.starting || plex.waiting || plex.isLoading}
          >
            {plex.waiting ? 'Waiting for Plex…' : plex.starting ? 'Starting…' : 'Log into Plex'}
          </button>
          <button
            className="auth-chip is-bare"
            onClick={() => (menuOpen ? closeMenu() : setMenuOpen(true))}
            title={`Signed in as ${appName}`}
            aria-label="Account menu"
            aria-expanded={menuOpen}
          >
            <span className="auth-chip-caret" aria-hidden="true">▾</span>
          </button>
        </>
      )}

      {menuOpen && (
        <div className="auth-menu">
          {/* Two accounts are in play and they are rarely the same person — the app's own Authentik
              identity, and the Plex account whose ratings and playlists the app acts on. Naming both
              beside their values is the whole point of the panel: a bare username here used to leave
              you guessing which of the two it was. */}
          <dl className="auth-menu-ids">
            <dt>Authentik</dt>
            <dd title={user.email ?? user.username ?? undefined}>{appName}</dd>
            <dt>Plex</dt>
            <dd
              className={linked ? undefined : 'is-absent'}
              title={plex.status?.email ?? undefined}
            >
              {linked ? plex.status!.username ?? 'Connected' : 'Not connected'}
            </dd>
          </dl>

          {/* The approval flow can only ever link whoever is signed in at app.plex.tv in this browser.
              Pasting a token is how you link a *different* account — a Plex Home / managed user who
              has no browser session of their own. Kept behind a disclosure because it's the unusual
              path, and because a token box on permanent display invites pasting the wrong thing. */}
          <button className="auth-menu-item" onClick={() => setTokenOpen((o) => !o)}>
            {linked ? 'Switch Plex account by token' : 'Use a Plex token instead'}
          </button>

          {tokenOpen && (
            <form
              className="auth-token"
              onSubmit={async (e) => {
                e.preventDefault()
                if (await plex.linkWithToken(token, label)) closeMenu()
              }}
            >
              <input
                type="password"
                className="auth-token-input"
                value={token}
                onChange={(e) => setToken(e.target.value)}
                placeholder="X-Plex-Token"
                autoComplete="off"
                spellCheck={false}
                aria-label="Plex token"
                autoFocus
              />
              {/* Used only when plex.tv can't identify the token — a server access token verifies
                  against the server but can't be attributed to anyone, so someone has to name it. */}
              <input
                type="text"
                className="auth-token-input"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                placeholder="Name (if Plex can't say)"
                autoComplete="off"
                aria-label="Account name"
              />
              <button
                type="submit"
                className="auth-btn"
                disabled={plex.linkingToken || token.trim() === ''}
              >
                {plex.linkingToken ? 'Checking…' : 'Link'}
              </button>
              {/* Inside the menu, not in the floating note below it: the menu is the higher layer,
                  so a note underneath would be hidden behind exactly the panel you're reading. */}
              {plex.problem && <p className="auth-token-error">{plex.problem}</p>}
            </form>
          )}

          {linked && (
            <button
              className="auth-menu-item"
              onClick={() => plex.disconnect()}
              disabled={plex.disconnecting}
            >
              {plex.disconnecting ? 'Disconnecting…' : 'Disconnect Plex'}
            </button>
          )}
          <button className="auth-menu-item" onClick={() => logout()}>Log out</button>
        </div>
      )}

      {/* The approval tab can be blocked, and the poll can time out; both need saying somewhere, and
          the header is where the click happened. */}
      {plex.fallbackUrl && (
        <a className="auth-note" href={plex.fallbackUrl} target="_blank" rel="noreferrer">
          Popup blocked — approve here
        </a>
      )}
      {plex.problem && !tokenOpen && <span className="auth-note is-error">{plex.problem}</span>}
    </div>
  )
}

export default function Layout({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const topbarRef = useRef<HTMLElement>(null)

  // The top bar wraps to two rows on narrow screens, so its height is variable. Publish the
  // measured height to --topbar-h so the sticky offsets and the mobile full-screen detail pane
  // (which starts below the bar) line up regardless of how many rows the bar takes.
  useEffect(() => {
    const el = topbarRef.current
    if (!el) return
    const apply = () =>
      document.documentElement.style.setProperty('--topbar-h', `${el.offsetHeight}px`)
    apply()
    const ro = new ResizeObserver(apply)
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  return (
    <div className="app">
      <MyceliumBackdrop />
      <header className="topbar" ref={topbarRef}>
        <div className="brand">Mycelium</div>
        <nav className="nav">
          <NavLink to="/" className={navClass} end>
            Discover
          </NavLink>
          <NavLink to="/browse" className={navClass}>
            Browse
          </NavLink>
          <NavLink to="/downloads" className={navClass}>
            Download
          </NavLink>
          <NavLink to="/playlists" className={navClass}>
            Playlists
          </NavLink>
          {/* Dev panel — shown only to DEV_USERNAMES users (Plex tag tooling + similarity debug). */}
          {user?.isDev && (
            <NavLink to="/dev" className={navClass}>
              Dev
            </NavLink>
          )}
        </nav>
        <div className="topbar-end">
          <VolumeControl />
          <AuthBox />
        </div>
      </header>
      <main className="content">{children}</main>
    </div>
  )
}
