import { Navigate, Route, Routes } from 'react-router-dom'
import Layout from './components/Layout'
import Browse from './pages/Browse'
import Discover from './pages/Discover'
import Purchases from './pages/Purchases'
import Playlists from './pages/Playlists'
import Other from './pages/Other'

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Discover />} />
        {/* Discover used to live here; keep old links/bookmarks working. */}
        <Route path="/discover" element={<Navigate to="/" replace />} />
        <Route path="/browse" element={<Browse />} />
        {/* Ratings was folded into the Browse drill-down + Download queue; keep old links working. */}
        <Route path="/ratings" element={<Navigate to="/browse" replace />} />
        <Route path="/downloads" element={<Purchases />} />
        {/* The download queue used to live at /purchases; keep old links/bookmarks working. */}
        <Route path="/purchases" element={<Navigate to="/downloads" replace />} />
        {/* Ready-made Plex smart playlists, built in the user's own linked Plex account. */}
        <Route path="/playlists" element={<Playlists />} />
        {/* Cleanup and the old similarity debugger were folded into this page. Keep old links working. */}
        <Route path="/cleanup" element={<Navigate to="/other" replace />} />
        <Route path="/related" element={<Navigate to="/other" replace />} />
        {/* Odds and ends: the takeout, for everyone, plus the operator tooling (Plex tags,
            sweeps, similarity debug) that only DEV_USERNAMES sees. Every dev endpoint re-checks
            server-side, so the page's own gate is cosmetic. */}
        <Route path="/other" element={<Other />} />
        {/* This was the dev panel before the takeout gave it a reason to exist for everyone. */}
        <Route path="/dev" element={<Navigate to="/other" replace />} />
      </Routes>
    </Layout>
  )
}
