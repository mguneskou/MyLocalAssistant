import { createContext, useContext, useState, useCallback, type ReactNode } from 'react'
import type { UserDto } from '../api/types'
import * as api from '../api/client'

interface AuthState {
  user: UserDto | null
  isAuthenticated: boolean
  signIn: (u: UserDto) => void
  signOut: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserDto | null>(() => api.getStoredUser())

  const signIn = useCallback((u: UserDto) => {
    api.saveUser(u)
    setUser(u)
  }, [])

  const signOut = useCallback(() => {
    api.logout()
    api.clearUser()
    setUser(null)
  }, [])

  return (
    <AuthContext.Provider value={{ user, isAuthenticated: !!user, signIn, signOut }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
