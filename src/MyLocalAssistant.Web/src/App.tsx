import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom'
import { AuthProvider, useAuth } from './contexts/AuthContext'
import LoginPage from './pages/LoginPage'
import ChangePasswordPage from './pages/ChangePasswordPage'
import ChatPage from './pages/ChatPage'
import AdminPage from './pages/AdminPage'
import type { ReactNode } from 'react'

function getRequestedPath(raw: string | null | undefined): string | null {
  if (!raw) return null
  if (!raw.startsWith('/')) return null
  if (raw.startsWith('//')) return null
  return raw
}

function RequireAuth({ children }: { children: ReactNode }) {
  const { isAuthenticated, user } = useAuth()
  console.log('[MLA] RequireAuth — isAuthenticated:', isAuthenticated, '| mustChangePassword:', user?.mustChangePassword ?? 'n/a', '| user:', user ? user.username : 'null')
  if (!isAuthenticated) { console.log('[MLA] Not authenticated → redirecting to /login'); return <Navigate to="/login" replace /> }
  if (user?.mustChangePassword) { console.log('[MLA] mustChangePassword=true → redirecting to /change-password'); return <Navigate to="/change-password" replace /> }
  console.log('[MLA] Auth OK → rendering chat page')
  return <>{children}</>
}

function RequireGuest({ children }: { children: ReactNode }) {
  const { isAuthenticated, user } = useAuth()
  const location = useLocation()
  const stateFrom = (location.state as { from?: string } | null)?.from
  const queryFrom = new URLSearchParams(location.search).get('from')
  const from = getRequestedPath(queryFrom) ?? getRequestedPath(stateFrom)
  if (isAuthenticated && user?.mustChangePassword) return <Navigate to="/change-password" replace />
  if (isAuthenticated && from?.startsWith('/admin') && user?.isAdmin) return <Navigate to={from} replace />
  if (isAuthenticated && from?.startsWith('/admin') && !user?.isAdmin) return <Navigate to="/admin-required" replace />
  if (isAuthenticated) return <Navigate to="/" replace />
  return <>{children}</>
}

function RequireAdmin({ children }: { children: ReactNode }) {
  const location = useLocation()
  const { isAuthenticated, user } = useAuth()
  if (!isAuthenticated) {
    const from = `${location.pathname}${location.search}`
    return <Navigate to={`/login?from=${encodeURIComponent(from)}`} replace state={{ from }} />
  }
  if (user?.mustChangePassword) return <Navigate to="/change-password" replace />
  if (!user?.isAdmin) return <Navigate to="/admin-required" replace />
  return <>{children}</>
}

function AdminAccessRequiredPage() {
  const { user, signOut } = useAuth()
  return (
    <div className="min-h-full bg-zinc-950 text-zinc-100 flex items-center justify-center px-4">
      <div className="w-full max-w-lg rounded-xl border border-zinc-800 bg-zinc-900 p-6 space-y-4">
        <h1 className="text-xl font-semibold">Admin Access Required</h1>
        <p className="text-sm text-zinc-400">
          The current account {user?.username ? `'${user.username}'` : ''} does not have admin permissions.
        </p>
        <p className="text-sm text-zinc-400">
          Sign out and log in with an admin or global-admin account to use the admin interface.
        </p>
        <div className="flex items-center gap-2">
          <button
            onClick={signOut}
            className="px-3 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500"
          >
            Sign Out
          </button>
          <a
            href="/"
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700"
          >
            Go To Chat
          </a>
        </div>
      </div>
    </div>
  )
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<RequireGuest><LoginPage /></RequireGuest>} />
          <Route path="/change-password" element={<ChangePasswordPage />} />
          <Route path="/admin" element={<RequireAdmin><AdminPage /></RequireAdmin>} />
          <Route path="/admin-required" element={<RequireAuth><AdminAccessRequiredPage /></RequireAuth>} />
          <Route path="/" element={<RequireAuth><ChatPage /></RequireAuth>} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}
