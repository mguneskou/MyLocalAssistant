import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import * as api from '../../api/client'
import { useAuth } from '../../contexts/AuthContext'
import type { CreateUserRequest, DepartmentDto, UpdateUserRequest, UserAdminDto } from '../../api/types'

type EditorMode = 'create' | 'edit'

interface UserDraft {
  username: string
  displayName: string
  password: string
  isAdmin: boolean
  isDisabled: boolean
  mustChangePassword: boolean
  workRoot: string
  departments: string[]
}

const emptyDraft: UserDraft = {
  username: '',
  displayName: '',
  password: '',
  isAdmin: false,
  isDisabled: false,
  mustChangePassword: false,
  workRoot: '',
  departments: [],
}

export default function AdminUsersTab() {
  const { user } = useAuth()
  const [users, setUsers] = useState<UserAdminDto[]>([])
  const [departments, setDepartments] = useState<DepartmentDto[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState('')

  const [editorMode, setEditorMode] = useState<EditorMode | null>(null)
  const [draft, setDraft] = useState<UserDraft>(emptyDraft)

  const selected = useMemo(
    () => users.find(u => u.id === selectedId) ?? null,
    [users, selectedId],
  )
  const selectedIsSelf = !!selected && !!user && selected.id === user.id

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [usersResp, depResp] = await Promise.all([
        api.listUsers(),
        api.listDepartments(),
      ])
      setUsers(usersResp)
      setDepartments(depResp)
      setSelectedId(prev => {
        if (prev && usersResp.some(u => u.id === prev)) return prev
        return usersResp[0]?.id ?? null
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load users.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  function openCreate() {
    setDraft(emptyDraft)
    setEditorMode('create')
  }

  function openEdit() {
    if (!selected) return
    setDraft({
      username: selected.username,
      displayName: selected.displayName,
      password: '',
      isAdmin: selected.isAdmin,
      isDisabled: selected.isDisabled,
      mustChangePassword: selected.mustChangePassword,
      workRoot: selected.workRoot ?? '',
      departments: selected.departments,
    })
    setEditorMode('edit')
  }

  function closeEditor() {
    setEditorMode(null)
    setDraft(emptyDraft)
  }

  function toggleDepartment(name: string) {
    setDraft(prev => ({
      ...prev,
      departments: prev.departments.includes(name)
        ? prev.departments.filter(d => d !== name)
        : [...prev.departments, name],
    }))
  }

  function replaceRow(next: UserAdminDto) {
    setUsers(prev => prev.map(u => (u.id === next.id ? next : u)))
  }

  async function saveEditor() {
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      if (editorMode === 'create') {
        const req: CreateUserRequest = {
          username: draft.username.trim(),
          displayName: draft.displayName.trim(),
          password: draft.password,
          departments: draft.isAdmin ? [] : draft.departments,
          isAdmin: draft.isAdmin,
          workRoot: draft.workRoot.trim() || null,
        }
        const created = await api.createUser(req)
        setUsers(prev => [...prev, created])
        setSelectedId(created.id)
        setStatus(`Created '${created.username}'.`)
      } else if (editorMode === 'edit' && selected) {
        const req: UpdateUserRequest = {
          displayName: draft.displayName.trim(),
          departments: draft.isAdmin ? [] : draft.departments,
          isAdmin: draft.isAdmin,
          isDisabled: draft.isDisabled,
          workRoot: draft.workRoot.trim() || null,
          mustChangePassword: draft.mustChangePassword,
        }
        const updated = await api.updateUser(selected.id, req)
        replaceRow(updated)
        setStatus(`Saved '${updated.username}'.`)
      }
      closeEditor()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save user.')
    } finally {
      setBusy(false)
    }
  }

  async function onResetPassword() {
    if (!selected) return
    const pwd = window.prompt(`New password for ${selected.username}:`)
    if (!pwd) return
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.resetUserPassword(selected.id, pwd)
      replaceRow({ ...selected, mustChangePassword: true })
      setStatus(`Password reset for '${selected.username}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Password reset failed.')
    } finally {
      setBusy(false)
    }
  }

  async function onToggleDisabled() {
    if (!selected || selectedIsSelf) return
    const next = !selected.isDisabled
    if (!window.confirm(`${next ? 'Disable' : 'Enable'} '${selected.username}'?`)) return
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateUser(selected.id, { isDisabled: next })
      replaceRow(updated)
      setStatus(`${next ? 'Disabled' : 'Enabled'} '${selected.username}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'State change failed.')
    } finally {
      setBusy(false)
    }
  }

  async function onDelete() {
    if (!selected || selectedIsSelf) return
    if (!window.confirm(`Delete '${selected.username}'? This cannot be undone.`)) return
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.deleteUser(selected.id)
      setUsers(prev => prev.filter(u => u.id !== selected.id))
      setSelectedId(prev => (prev === selected.id ? null : prev))
      setStatus(`Deleted '${selected.username}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Delete failed.')
    } finally {
      setBusy(false)
    }
  }

  const canSaveCreate =
    editorMode === 'create' &&
    draft.username.trim().length > 0 &&
    draft.displayName.trim().length > 0 &&
    draft.password.length >= 8
  const canSaveEdit = editorMode === 'edit' && draft.displayName.trim().length > 0

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Users</h1>
          <p className="text-sm text-zinc-500 mt-1">Manage local users, departments, access flags, and password reset.</p>
        </div>
        <button
          onClick={load}
          className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          disabled={loading || busy}
        >
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      <div className="flex flex-wrap gap-2">
        <button onClick={openCreate} disabled={busy} className="px-3 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500 disabled:opacity-50">New user</button>
        <button onClick={openEdit} disabled={!selected || busy} className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50">Edit</button>
        <button onClick={onResetPassword} disabled={!selected || busy} className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50">Reset password</button>
        <button onClick={onToggleDisabled} disabled={!selected || selectedIsSelf || busy} className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50">
          {selected?.isDisabled ? 'Enable' : 'Disable'}
        </button>
        <button onClick={onDelete} disabled={!selected || selectedIsSelf || busy} className="px-3 py-2 rounded-lg text-sm bg-red-700/80 hover:bg-red-700 disabled:opacity-50">Delete</button>
      </div>

      {selectedIsSelf && (
        <div className="rounded-lg border border-zinc-800 bg-zinc-900 px-4 py-3 text-sm text-zinc-400">
          Self-protection is active: you cannot disable or delete your own account.
        </div>
      )}

      {error && <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">{error}</div>}
      {status && <div className="rounded-lg border border-emerald-800 bg-emerald-950/20 text-emerald-300 px-4 py-3 text-sm">{status}</div>}

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-zinc-800/70 text-zinc-300">
            <tr>
              <th className="text-left px-3 py-2">Username</th>
              <th className="text-left px-3 py-2">Display name</th>
              <th className="text-left px-3 py-2">Departments</th>
              <th className="text-left px-3 py-2">Admin</th>
              <th className="text-left px-3 py-2">Disabled</th>
              <th className="text-left px-3 py-2">Last login</th>
            </tr>
          </thead>
          <tbody>
            {users.map(u => {
              const isSelected = u.id === selectedId
              return (
                <tr
                  key={u.id}
                  onClick={() => setSelectedId(u.id)}
                  className={`cursor-pointer border-t border-zinc-800 ${isSelected ? 'bg-zinc-800/70' : 'hover:bg-zinc-800/40'}`}
                >
                  <td className="px-3 py-2 text-zinc-100">{u.username}</td>
                  <td className="px-3 py-2 text-zinc-200">{u.displayName}</td>
                  <td className="px-3 py-2 text-zinc-400">{u.isAdmin ? '(all — admin)' : (u.departments.join(', ') || '(none)')}</td>
                  <td className="px-3 py-2 text-zinc-300">{u.isAdmin ? 'Yes' : 'No'}</td>
                  <td className="px-3 py-2 text-zinc-300">{u.isDisabled ? 'Yes' : 'No'}</td>
                  <td className="px-3 py-2 text-zinc-400">{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : '-'}</td>
                </tr>
              )
            })}
            {users.length === 0 && (
              <tr>
                <td colSpan={6} className="px-3 py-6 text-center text-zinc-500">No users found.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {editorMode && (
        <div className="fixed inset-0 z-30 bg-black/50 flex items-center justify-center p-4">
          <div className="w-full max-w-2xl rounded-xl border border-zinc-700 bg-zinc-900 p-4 space-y-4">
            <h2 className="text-lg font-semibold">{editorMode === 'create' ? 'Create user' : `Edit ${selected?.username ?? ''}`}</h2>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <Field label="Username">
                <input
                  type="text"
                  value={draft.username}
                  disabled={editorMode === 'edit'}
                  onChange={e => setDraft(prev => ({ ...prev, username: e.target.value }))}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                />
              </Field>
              <Field label="Display name">
                <input
                  type="text"
                  value={draft.displayName}
                  onChange={e => setDraft(prev => ({ ...prev, displayName: e.target.value }))}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
                />
              </Field>
              <Field label="Password">
                <input
                  type="password"
                  value={draft.password}
                  onChange={e => setDraft(prev => ({ ...prev, password: e.target.value }))}
                  placeholder={editorMode === 'edit' ? 'Leave blank for unchanged' : 'Minimum 8 characters'}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
                />
              </Field>
              <Field label="Work root">
                <input
                  type="text"
                  value={draft.workRoot}
                  onChange={e => setDraft(prev => ({ ...prev, workRoot: e.target.value }))}
                  placeholder="Optional"
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
                />
              </Field>
            </div>

            <div className="flex flex-wrap gap-4 text-sm">
              <label className="inline-flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={draft.isAdmin}
                  onChange={e => setDraft(prev => ({ ...prev, isAdmin: e.target.checked }))}
                />
                Admin
              </label>
              {editorMode === 'edit' && (
                <>
                  <label className="inline-flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={draft.isDisabled}
                      onChange={e => setDraft(prev => ({ ...prev, isDisabled: e.target.checked }))}
                    />
                    Disabled
                  </label>
                  <label className="inline-flex items-center gap-2">
                    <input
                      type="checkbox"
                      checked={draft.mustChangePassword}
                      onChange={e => setDraft(prev => ({ ...prev, mustChangePassword: e.target.checked }))}
                    />
                    Must change password
                  </label>
                </>
              )}
            </div>

            <div>
              <div className="text-sm text-zinc-400 mb-2">Departments</div>
              {draft.isAdmin ? (
                <div className="text-sm text-zinc-500 rounded border border-zinc-800 bg-zinc-950 px-3 py-2">Admin users can access all departments.</div>
              ) : (
                <div className="grid grid-cols-1 md:grid-cols-2 gap-2 max-h-48 overflow-auto rounded border border-zinc-800 bg-zinc-950 p-2">
                  {departments.map(dep => (
                    <label key={dep.id} className="inline-flex items-center gap-2 text-sm text-zinc-300">
                      <input
                        type="checkbox"
                        checked={draft.departments.includes(dep.name)}
                        onChange={() => toggleDepartment(dep.name)}
                      />
                      {dep.name}
                    </label>
                  ))}
                </div>
              )}
            </div>

            <div className="flex justify-end gap-2">
              <button onClick={closeEditor} className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 text-sm">Cancel</button>
              <button
                onClick={saveEditor}
                disabled={busy || !(canSaveCreate || canSaveEdit)}
                className="px-3 py-2 rounded bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-sm"
              >
                {busy ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block text-sm">
      <span className="block text-zinc-400 mb-1">{label}</span>
      {children}
    </label>
  )
}
