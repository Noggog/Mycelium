import { useEffect, useRef, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import VolumeControl from './VolumeControl'
import MyceliumBackdrop from './MyceliumBackdrop'

const navClass = ({ isActive }: { isActive: boolean }) =>
  isActive ? 'nav-link active' : 'nav-link'

function AuthBox() {
  const { user, isLoading, login, logout } = useAuth()

  if (isLoading) {
    return <div className="auth-box"><span className="auth-name">…</span></div>
  }

  if (!user) {
    return (
      <div className="auth-box">
        <button className="auth-btn" onClick={() => login()}>Log in</button>
      </div>
    )
  }

  return (
    <div className="auth-box">
      <span className="auth-name" title={user.email ?? undefined}>
        {user.displayName ?? user.username ?? user.email ?? 'Signed in'}
      </span>
      <button className="auth-btn" onClick={() => logout()}>Log out</button>
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
          <NavLink to="/purchases" className={navClass}>
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
