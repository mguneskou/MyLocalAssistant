import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type { AuditEntryDto } from '../../api/types'

type Ternary = 'any' | 'true' | 'false'

function toIso(value: string): string | undefined {
  if (!value) return undefined
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString()
}

export default function AdminAuditTab() {
  const [items, setItems] = useState<AuditEntryDto[]>([])
  const [total, setTotal] = useState(0)
  const [skip, setSkip] = useState(0)
  const [take, setTake] = useState(100)

  const [actions, setActions] = useState<string[]>([])

  const [fromLocal, setFromLocal] = useState('')
  const [toLocal, setToLocal] = useState('')
  const [action, setAction] = useState('')
  const [userFilter, setUserFilter] = useState('')
  const [success, setSuccess] = useState<Ternary>('any')
  const [isAdminAction, setIsAdminAction] = useState<Ternary>('any')

  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const hasPrev = skip > 0
  const hasNext = skip + take < total

  const query = useMemo(() => {
    const successValue = success === 'any' ? undefined : success === 'true'
    const adminValue = isAdminAction === 'any' ? undefined : isAdminAction === 'true'
    return {
      from: toIso(fromLocal),
      to: toIso(toLocal),
      action: action || undefined,
      user: userFilter.trim() || undefined,
      success: successValue,
      isAdminAction: adminValue,
      skip,
      take,
    }
  }, [fromLocal, toLocal, action, userFilter, success, isAdminAction, skip, take])

  const loadActions = useCallback(async () => {
    try {
      setActions(await api.listAuditActions())
    } catch {
      // Actions list is optional for the main page.
    }
  }, [])

  const loadAudit = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const page = await api.listAudit(query)
      setItems(page.items)
      setTotal(page.total)
      setSkip(page.skip)
      setTake(page.take)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load audit entries.')
    } finally {
      setLoading(false)
    }
  }, [query])

  useEffect(() => {
    loadActions()
  }, [loadActions])

  useEffect(() => {
    loadAudit()
  }, [loadAudit])

  async function onExportCsv() {
    setBusy(true)
    setError(null)
    try {
      const blob = await api.downloadAuditCsv({
        from: toIso(fromLocal),
        to: toIso(toLocal),
        action: action || undefined,
        user: userFilter.trim() || undefined,
        success: success === 'any' ? undefined : success === 'true',
        isAdminAction: isAdminAction === 'any' ? undefined : isAdminAction === 'true',
      })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `audit-${new Date().toISOString().replace(/[:.]/g, '-')}.csv`
      document.body.appendChild(anchor)
      anchor.click()
      document.body.removeChild(anchor)
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'CSV export failed.')
    } finally {
      setBusy(false)
    }
  }

  function applyFilters() {
    setSkip(0)
  }

  function clearFilters() {
    setFromLocal('')
    setToLocal('')
    setAction('')
    setUserFilter('')
    setSuccess('any')
    setIsAdminAction('any')
    setSkip(0)
  }

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold">Audit</h1>
          <p className="text-sm text-zinc-500 mt-1">Search and export admin/user audit records.</p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={loadAudit}
            disabled={loading || busy}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            {loading ? 'Loading…' : 'Refresh'}
          </button>
          <button
            onClick={onExportCsv}
            disabled={busy}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            Export CSV
          </button>
        </div>
      </div>

      {error && <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">{error}</div>}

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 space-y-3">
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-3">
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">From</span>
            <input
              type="datetime-local"
              value={fromLocal}
              onChange={e => setFromLocal(e.target.value)}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            />
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">To</span>
            <input
              type="datetime-local"
              value={toLocal}
              onChange={e => setToLocal(e.target.value)}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            />
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">Action</span>
            <select
              value={action}
              onChange={e => setAction(e.target.value)}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            >
              <option value="">(all)</option>
              {actions.map(a => (
                <option key={a} value={a}>{a}</option>
              ))}
            </select>
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">User / UserId</span>
            <input
              type="text"
              value={userFilter}
              onChange={e => setUserFilter(e.target.value)}
              placeholder="Contains username or GUID"
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            />
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">Success</span>
            <select
              value={success}
              onChange={e => setSuccess(e.target.value as Ternary)}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            >
              <option value="any">Any</option>
              <option value="true">Success only</option>
              <option value="false">Errors only</option>
            </select>
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">Scope</span>
            <select
              value={isAdminAction}
              onChange={e => setIsAdminAction(e.target.value as Ternary)}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            >
              <option value="any">Any</option>
              <option value="true">Admin actions</option>
              <option value="false">Non-admin actions</option>
            </select>
          </label>
          <label className="text-sm">
            <span className="block text-zinc-400 mb-1">Page size</span>
            <select
              value={String(take)}
              onChange={e => {
                setTake(Number.parseInt(e.target.value, 10) || 100)
                setSkip(0)
              }}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
            >
              <option value="50">50</option>
              <option value="100">100</option>
              <option value="200">200</option>
              <option value="500">500</option>
            </select>
          </label>
        </div>

        <div className="flex items-center gap-2">
          <button
            onClick={applyFilters}
            disabled={busy}
            className="px-3 py-2 rounded bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-sm"
          >
            Apply filters
          </button>
          <button
            onClick={clearFilters}
            disabled={busy}
            className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50 text-sm"
          >
            Clear
          </button>
        </div>
      </div>

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-zinc-800/70 text-zinc-300">
            <tr>
              <th className="text-left px-3 py-2">Timestamp</th>
              <th className="text-left px-3 py-2">User</th>
              <th className="text-left px-3 py-2">Action</th>
              <th className="text-left px-3 py-2">Agent</th>
              <th className="text-left px-3 py-2">Success</th>
              <th className="text-left px-3 py-2">Admin</th>
              <th className="text-left px-3 py-2">Detail</th>
            </tr>
          </thead>
          <tbody>
            {items.map(entry => (
              <tr key={entry.id} className="border-t border-zinc-800 align-top">
                <td className="px-3 py-2 whitespace-nowrap">{new Date(entry.timestamp).toLocaleString()}</td>
                <td className="px-3 py-2 whitespace-nowrap">{entry.username || entry.userId || '-'}</td>
                <td className="px-3 py-2 whitespace-nowrap">{entry.action}</td>
                <td className="px-3 py-2 whitespace-nowrap">{entry.agentId || '-'}</td>
                <td className="px-3 py-2 whitespace-nowrap">{entry.success ? 'Yes' : 'No'}</td>
                <td className="px-3 py-2 whitespace-nowrap">{entry.isAdminAction ? 'Yes' : 'No'}</td>
                <td className="px-3 py-2 text-zinc-400 max-w-[32rem] break-words">{entry.detail || '-'}</td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr>
                <td colSpan={7} className="px-3 py-6 text-center text-zinc-500">No records for this filter.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="flex items-center justify-between text-sm text-zinc-400">
        <div>
          Showing {items.length} of {total} total
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setSkip(Math.max(0, skip - take))}
            disabled={!hasPrev || loading}
            className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            Previous
          </button>
          <button
            onClick={() => setSkip(skip + take)}
            disabled={!hasNext || loading}
            className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            Next
          </button>
        </div>
      </div>
    </div>
  )
}
