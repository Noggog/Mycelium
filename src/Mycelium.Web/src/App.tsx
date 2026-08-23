import { Navigate, Route, Routes } from 'react-router-dom'
import Layout from './components/Layout'
import Browse from './pages/Browse'
import Discover from './pages/Discover'
import Purchases from './pages/Purchases'
import Playlists from './pages/Playlists'
import Dev from './pages/Dev'

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
        <Route path="/purchases" element={<Purchases />} />
        {/* Ready-made Plex smart playlists, built in the user's own linked Plex account. */}
        <Route path="/playlists" element={<Playlists />} />
        {/* Cleanup and the old similarity debugger were folded into the dev panel. Keep old links working. */}
        <Route path="/cleanup" element={<Navigate to="/dev" replace />} />
        <Route path="/related" element={<Navigate to="/dev" replace />} />
        {/* Dev panel (Plex tag tooling + similarity debug). Visible only to DEV_USERNAMES; the
            page itself gates on isDev and every endpoint re-checks server-side. */}
        <Route path="/dev" element={<Dev />} />
      </Routes>
    </Layout>
  )
}
