import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type { StatsDto } from '../../api/types'

type WindowDays = 7 | 30 | 90

export default function AdminUsageTab() {
  const [days, setDays] = useState<WindowDays>(30)
  const [stats, setStats] = useState<StatsDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setStats(await api.getStats(days))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load usage stats.')
    } finally {
      setLoading(false)
    }
  }, [days])

  useEffect(() => {
    load()
  }, [load])

  const dailyRows = useMemo(() => stats?.dailyChats ?? [], [stats])
  const maxDaily = useMemo(() => Math.max(1, ...dailyRows.map(r => r.count)), [dailyRows])

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold">Usage</h1>
          <p className="text-sm text-zinc-500 mt-1">Operational usage metrics by time window and agent.</p>
        </div>
        <div className="flex items-center gap-2">
          <select
            value={String(days)}
            onChange={e => setDays(Number.parseInt(e.target.value, 10) as WindowDays)}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-900 border border-zinc-700"
          >
            <option value="7">7 days</option>
            <option value="30">30 days</option>
            <option value="90">90 days</option>
          </select>
          <button
            onClick={load}
            disabled={loading}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            {loading ? 'Loading…' : 'Refresh'}
          </button>
        </div>
      </div>

      {error && <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">{error}</div>}

      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <Card label={`Chats (${days}d)`} value={stats?.totalChats ?? 0} />
        <Card label="Active users" value={stats?.activeUsers ?? 0} />
        <Card label="Error rate" value={`${(((stats?.errorRate ?? 0) * 100).toFixed(1))}%`} />
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <section className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-hidden">
          <div className="px-4 py-3 border-b border-zinc-800 text-sm font-semibold">Per-agent breakdown</div>
          <table className="w-full text-sm">
            <thead className="bg-zinc-800/70 text-zinc-300">
              <tr>
                <th className="text-left px-3 py-2">Agent</th>
                <th className="text-left px-3 py-2">Chats</th>
                <th className="text-left px-3 py-2">Errors</th>
                <th className="text-left px-3 py-2">Error %</th>
              </tr>
            </thead>
            <tbody>
              {(stats?.byAgent ?? []).map(row => {
                const pct = row.count === 0 ? '0.0%' : `${((row.errors * 100) / row.count).toFixed(1)}%`
                return (
                  <tr key={row.agentId} className="border-t border-zinc-800">
                    <td className="px-3 py-2">{row.agentId}</td>
                    <td className="px-3 py-2">{row.count}</td>
                    <td className="px-3 py-2">{row.errors}</td>
                    <td className="px-3 py-2">{pct}</td>
                  </tr>
                )
              })}
              {(stats?.byAgent.length ?? 0) === 0 && (
                <tr>
                  <td colSpan={4} className="px-3 py-6 text-center text-zinc-500">No usage rows in this window.</td>
                </tr>
              )}
            </tbody>
          </table>
        </section>

        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold mb-3">Daily chats ({days}d)</h2>
          <div className="space-y-2 max-h-96 overflow-auto pr-1">
            {dailyRows.map(row => (
              <div key={row.day} className="text-xs">
                <div className="flex items-center justify-between text-zinc-400 mb-1">
                  <span>{row.day}</span>
                  <span>{row.count}</span>
                </div>
                <div className="h-2 rounded bg-zinc-800">
                  <div
                    className="h-2 rounded bg-blue-500"
                    style={{ width: `${Math.max(2, Math.round((row.count / maxDaily) * 100))}%` }}
                  />
                </div>
              </div>
            ))}
            {dailyRows.length === 0 && <div className="text-sm text-zinc-500">No daily data available.</div>}
          </div>
        </section>
      </div>
    </div>
  )
}

function Card({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <div className="text-zinc-500 text-xs uppercase tracking-wide">{label}</div>
      <div className="text-lg font-semibold mt-2">{value}</div>
    </div>
  )
}
