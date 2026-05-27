import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type { ActiveEmbeddingStatusDto, ActiveModelStatusDto, StatsDto } from '../../api/types'

export default function AdminOverviewTab() {
  const [stats, setStats] = useState<StatsDto | null>(null)
  const [chatStatus, setChatStatus] = useState<ActiveModelStatusDto | null>(null)
  const [embeddingStatus, setEmbeddingStatus] = useState<ActiveEmbeddingStatusDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [statsResp, chatResp, embedResp] = await Promise.all([
        api.getStats(30),
        api.getModelStatus(),
        api.getEmbeddingStatus(),
      ])
      setStats(statsResp)
      setChatStatus(chatResp)
      setEmbeddingStatus(embedResp)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load admin overview.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const dailyRows = useMemo(() => (stats?.dailyChats ?? []).slice(-14), [stats])
  const maxCount = useMemo(
    () => Math.max(1, ...dailyRows.map(r => r.count)),
    [dailyRows],
  )

  return (
    <div className="p-6 space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">Overview</h1>
          <p className="text-sm text-zinc-500 mt-1">Health and usage snapshot for the last 30 days.</p>
        </div>
        <button
          onClick={load}
          className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          disabled={loading}
        >
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {error && (
        <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-3">
        <Card label="Total Chats (30d)" value={stats?.totalChats ?? 0} />
        <Card label="Active Users" value={stats?.activeUsers ?? 0} />
        <Card label="Error Rate" value={`${Math.round((stats?.errorRate ?? 0) * 10000) / 100}%`} />
        <Card label="Top Agent" value={stats?.byAgent?.[0]?.agentId ?? '(none)'} />
      </div>

      <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
        <h2 className="text-sm font-semibold text-zinc-200 mb-3">Model Runtime</h2>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 text-sm">
          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3">
            <div className="text-zinc-500">Active LLM</div>
            <div className="font-medium mt-1">{chatStatus?.activeModelId ?? '(none)'}</div>
            <div className="text-zinc-400 mt-1">Status: {chatStatus?.status ?? 'unknown'}</div>
            <div className="text-zinc-500 mt-1">Backend: {chatStatus?.backend ?? '-'}</div>
            {chatStatus?.lastError && <div className="text-red-300 mt-1">{chatStatus.lastError}</div>}
          </div>
          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3">
            <div className="text-zinc-500">Active Embedding</div>
            <div className="font-medium mt-1">{embeddingStatus?.activeModelId ?? '(none)'}</div>
            <div className="text-zinc-400 mt-1">Status: {embeddingStatus?.status ?? 'unknown'}</div>
            <div className="text-zinc-500 mt-1">Dimension: {embeddingStatus?.embeddingDimension ?? 0}</div>
            {embeddingStatus?.lastError && <div className="text-red-300 mt-1">{embeddingStatus.lastError}</div>}
          </div>
        </div>
      </section>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-200 mb-3">Top Agents</h2>
          <div className="space-y-2">
            {(stats?.byAgent ?? []).slice(0, 8).map(a => (
              <div key={a.agentId} className="flex items-center justify-between text-sm border-b border-zinc-800 pb-2">
                <div className="truncate pr-3">{a.agentId}</div>
                <div className="text-zinc-400 whitespace-nowrap">{a.count} chats</div>
              </div>
            ))}
            {(stats?.byAgent?.length ?? 0) === 0 && (
              <div className="text-sm text-zinc-500">No activity in this window.</div>
            )}
          </div>
        </section>

        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
          <h2 className="text-sm font-semibold text-zinc-200 mb-3">Daily Chats (Last 14 days)</h2>
          <div className="space-y-2">
            {dailyRows.map(row => (
              <div key={row.day} className="text-xs">
                <div className="flex items-center justify-between text-zinc-400 mb-1">
                  <span>{row.day}</span>
                  <span>{row.count}</span>
                </div>
                <div className="h-2 bg-zinc-800 rounded">
                  <div
                    className="h-2 bg-blue-500 rounded"
                    style={{ width: `${Math.max(6, Math.round((row.count / maxCount) * 100))}%` }}
                  />
                </div>
              </div>
            ))}
            {dailyRows.length === 0 && (
              <div className="text-sm text-zinc-500">No daily data available.</div>
            )}
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
