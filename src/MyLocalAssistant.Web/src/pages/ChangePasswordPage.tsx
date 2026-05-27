import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'
import * as api from '../api/client'

export default function ChangePasswordPage() {
  const { user, signIn, signOut } = useAuth()
  const navigate = useNavigate()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)

  const forced = user?.mustChangePassword ?? false

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    if (next !== confirm) { setError('Passwords do not match.'); return }
    if (next.length < 8) { setError('Password must be at least 8 characters.'); return }
    setError('')
    setLoading(true)
    try {
      await api.changePassword(current, next)
      if (user) signIn({ ...user, mustChangePassword: false })
      navigate('/', { replace: true })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to change password')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-full flex items-center justify-center bg-zinc-950 px-4">
      <div className="w-full max-w-sm">
        <div className="mb-8">
          <h1 className="text-2xl font-semibold text-zinc-100">
            {forced ? 'Set a new password' : 'Change password'}
          </h1>
          {forced && (
            <p className="mt-2 text-sm text-zinc-400">
              You must change your password before continuing.
            </p>
          )}
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-zinc-300 mb-1.5">Current password</label>
            <input
              type="password"
              autoFocus
              value={current}
              onChange={e => setCurrent(e.target.value)}
              className="w-full rounded-lg bg-zinc-800 border border-zinc-700 text-zinc-100
                         px-3.5 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-zinc-300 mb-1.5">New password</label>
            <input
              type="password"
              value={next}
              onChange={e => setNext(e.target.value)}
              className="w-full rounded-lg bg-zinc-800 border border-zinc-700 text-zinc-100
                         px-3.5 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-zinc-300 mb-1.5">Confirm new password</label>
            <input
              type="password"
              value={confirm}
              onChange={e => setConfirm(e.target.value)}
              className="w-full rounded-lg bg-zinc-800 border border-zinc-700 text-zinc-100
                         px-3.5 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
            />
          </div>

          {error && (
            <div className="rounded-lg bg-red-950 border border-red-800 px-3.5 py-2.5 text-sm text-red-300">
              {error}
            </div>
          )}

          <button
            type="submit"
            disabled={loading || !current || !next || !confirm}
            className="w-full rounded-lg bg-blue-600 hover:bg-blue-500 disabled:opacity-50 disabled:cursor-not-allowed
                       text-white font-medium py-2.5 text-sm transition-colors"
          >
            {loading ? 'Saving…' : 'Save new password'}
          </button>

          {!forced && (
            <button
              type="button"
              onClick={() => navigate(-1)}
              className="w-full rounded-lg border border-zinc-700 text-zinc-300 hover:text-zinc-100
                         hover:border-zinc-600 font-medium py-2.5 text-sm transition-colors"
            >
              Cancel
            </button>
          )}
          {forced && (
            <button
              type="button"
              onClick={signOut}
              className="w-full text-center text-sm text-zinc-500 hover:text-zinc-400 transition-colors"
            >
              Sign out
            </button>
          )}
        </form>
      </div>
    </div>
  )
}
