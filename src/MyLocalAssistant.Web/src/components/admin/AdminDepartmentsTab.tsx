import { useCallback, useEffect, useState } from 'react'
import * as api from '../../api/client'
import type { DepartmentDto } from '../../api/types'

export default function AdminDepartmentsTab() {
  const [departments, setDepartments] = useState<DepartmentDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setDepartments(await api.listDepartments())
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load departments.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Departments</h1>
          <p className="text-sm text-zinc-500 mt-1">Read-only seeded department catalog used for agent visibility.</p>
        </div>
        <button
          onClick={load}
          className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          disabled={loading}
        >
          {loading ? 'Loading…' : 'Refresh'}
        </button>
      </div>

      {error && (
        <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">
          {error}
        </div>
      )}

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-zinc-800/70 text-zinc-300">
            <tr>
              <th className="text-left px-3 py-2">Department</th>
              <th className="text-left px-3 py-2">Members</th>
            </tr>
          </thead>
          <tbody>
            {departments.map(dep => (
              <tr key={dep.id} className="border-t border-zinc-800">
                <td className="px-3 py-2 text-zinc-100">{dep.name}</td>
                <td className="px-3 py-2 text-zinc-300">{dep.userCount}</td>
              </tr>
            ))}
            {departments.length === 0 && (
              <tr>
                <td colSpan={2} className="px-3 py-6 text-center text-zinc-500">
                  No departments available.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}
