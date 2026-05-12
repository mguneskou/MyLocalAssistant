import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { AuthProvider, useAuth } from './contexts/AuthContext'
import LoginPage from './pages/LoginPage'
import ChangePasswordPage from './pages/ChangePasswordPage'
import ChatPage from './pages/ChatPage'
import type { ReactNode } from 'react'

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
  if (isAuthenticated && user?.mustChangePassword) return <Navigate to="/change-password" replace />
  if (isAuthenticated) return <Navigate to="/" replace />
  return <>{children}</>
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<RequireGuest><LoginPage /></RequireGuest>} />
          <Route path="/change-password" element={<ChangePasswordPage />} />
          <Route path="/*" element={<RequireAuth><ChatPage /></RequireAuth>} />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}
