import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import { useAuth } from '../../contexts/AuthContext'
import type { ToolCallStatsSnapshot, ToolDto } from '../../api/types'

interface ToolDraft {
  enabled: boolean
  configJson: string
}

function toDraft(tool: ToolDto): ToolDraft {
  return {
    enabled: tool.enabled,
    configJson: tool.configJson ?? '',
  }
}

export default function AdminToolsTab() {
  const { user } = useAuth()
  const canEdit = !!user?.isGlobalAdmin

  const [tools, setTools] = useState<ToolDto[]>([])
  const [stats, setStats] = useState<ToolCallStatsSnapshot | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [draft, setDraft] = useState<ToolDraft | null>(null)

  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [reloading, setReloading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState('')

  const selected = useMemo(
    () => tools.find(t => t.id === selectedId) ?? null,
    [tools, selectedId],
  )

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [toolsResp, statsResp] = await Promise.all([
        api.listTools(),
        api.getToolStats(),
      ])
      setTools(toolsResp)
      setStats(statsResp)
      setSelectedId(prev => {
        if (prev && toolsResp.some(t => t.id === prev)) return prev
        return toolsResp[0]?.id ?? null
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load tools.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
  }, [load])

  useEffect(() => {
    if (!selected) {
      setDraft(null)
      return
    }
    setDraft(toDraft(selected))
  }, [selected])

  async function onQuickToggle(tool: ToolDto, enabled: boolean) {
    if (!canEdit) return
    setSaving(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateTool(tool.id, {
        enabled,
        configJson: tool.configJson ?? null,
      })
      setTools(prev => prev.map(t => (t.id === updated.id ? updated : t)))
      if (selectedId === updated.id) setDraft(toDraft(updated))
      setStatus(`${enabled ? 'Enabled' : 'Disabled'} '${updated.name}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Tool toggle failed.')
    } finally {
      setSaving(false)
    }
  }

  async function onSave() {
    if (!selected || !draft || !canEdit) return
    setSaving(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateTool(selected.id, {
        enabled: draft.enabled,
        configJson: draft.configJson.trim() || null,
      })
      setTools(prev => prev.map(t => (t.id === updated.id ? updated : t)))
      setDraft(toDraft(updated))
      setStatus(`Saved '${updated.name}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Tool update failed.')
    } finally {
      setSaving(false)
    }
  }

  async function onResetStats() {
    if (!canEdit) return
    if (!window.confirm('Reset all in-memory tool call stats?')) return
    setError(null)
    setStatus('')
    try {
      await api.resetToolStats()
      setStatus('Tool stats reset.')
      setStats(await api.getToolStats())
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to reset tool stats.')
    }
  }

  async function onReloadTools() {
    if (!canEdit) return
    setReloading(true)
    setError(null)
    setStatus('')
    try {
      const count = await api.reloadTools()
      setStatus(`Reload completed. Registered plug-in tools: ${count}.`)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to reload tools.')
    } finally {
      setReloading(false)
    }
  }

  return (
    <div className="p-6 h-full flex flex-col gap-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold">Tools</h1>
          <p className="text-sm text-zinc-500 mt-1">Enable/disable tools, edit JSON configuration, and inspect call stats.</p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={load}
            disabled={loading || saving || reloading}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            {loading ? 'Loading…' : 'Refresh'}
          </button>
          <button
            onClick={onReloadTools}
            disabled={!canEdit || reloading}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            {reloading ? 'Reloading…' : 'Reload plug-ins'}
          </button>
          <button
            onClick={onResetStats}
            disabled={!canEdit}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            Reset stats
          </button>
        </div>
      </div>

      {!canEdit && (
        <div className="rounded-lg border border-amber-800 bg-amber-950/25 text-amber-300 px-4 py-3 text-sm">
          You are not Global Admin. Editing is disabled.
        </div>
      )}

      {error && <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">{error}</div>}
      {status && <div className="rounded-lg border border-emerald-800 bg-emerald-950/20 text-emerald-300 px-4 py-3 text-sm">{status}</div>}

      <div className="grid grid-cols-1 xl:grid-cols-[320px_1fr] gap-4 min-h-0 flex-1">
        <aside className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-auto">
          <div className="px-3 py-2 border-b border-zinc-800 text-xs uppercase tracking-wide text-zinc-400">Registered tools checklist</div>
          <ul className="divide-y divide-zinc-800">
            {tools.map(tool => {
              const active = tool.id === selectedId
              return (
                <li key={tool.id}>
                  <div className={`flex items-center gap-2 px-3 py-2 ${active ? 'bg-zinc-800/70' : 'hover:bg-zinc-800/40'}`}>
                    <input
                      type="checkbox"
                      checked={tool.enabled}
                      disabled={!canEdit || saving}
                      onClick={e => e.stopPropagation()}
                      onChange={e => {
                        e.stopPropagation()
                        void onQuickToggle(tool, e.target.checked)
                      }}
                      title={tool.enabled ? 'Disable tool' : 'Enable tool'}
                    />
                    <button
                      onClick={() => setSelectedId(tool.id)}
                      className="flex-1 text-left"
                    >
                      <div className="font-medium text-zinc-100">{tool.name}</div>
                      <div className="text-xs text-zinc-400 mt-1">{tool.category} • {tool.enabled ? 'enabled' : 'disabled'}</div>
                    </button>
                  </div>
                </li>
              )
            })}
          </ul>
        </aside>

        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 overflow-auto space-y-4">
          {!selected || !draft ? (
            <div className="text-sm text-zinc-500">No tool selected.</div>
          ) : (
            <>
              <div>
                <h2 className="text-lg font-semibold">{selected.name}</h2>
                <p className="text-sm text-zinc-400 mt-1">{selected.description || 'No description.'}</p>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-3 text-sm">
                <Info label="Source" value={selected.source} />
                <Info label="Version" value={selected.version ?? '-'} />
                <Info label="Publisher" value={selected.publisher ?? '-'} />
                <Info label="Key ID" value={selected.keyId ?? '-'} />
                <Info label="Requires tools" value={selected.requires.tools || '-'} />
                <Info label="Min context (K)" value={String(selected.requires.minContextK)} />
              </div>

              <label className="inline-flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={draft.enabled}
                  disabled={!canEdit || saving}
                  onChange={e => setDraft(prev => (prev ? { ...prev, enabled: e.target.checked } : prev))}
                />
                Enabled
              </label>

              <label className="block text-sm">
                <span className="block text-zinc-400 mb-1">Config JSON</span>
                <textarea
                  value={draft.configJson}
                  disabled={!canEdit || saving}
                  onChange={e => setDraft(prev => (prev ? { ...prev, configJson: e.target.value } : prev))}
                  rows={6}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                  placeholder="Optional JSON object"
                />
              </label>

              <div>
                <h3 className="text-sm font-semibold mb-2">Exposed functions</h3>
                <div className="rounded-lg border border-zinc-800 bg-zinc-950 divide-y divide-zinc-800">
                  {selected.tools.map(fn => (
                    <div key={fn.name} className="p-3">
                      <div className="font-medium text-zinc-100 text-sm">{fn.name}</div>
                      <div className="text-xs text-zinc-400 mt-1">{fn.description}</div>
                    </div>
                  ))}
                  {selected.tools.length === 0 && <div className="p-3 text-sm text-zinc-500">No functions exposed.</div>}
                </div>
              </div>

              <div className="flex justify-end">
                <button
                  onClick={onSave}
                  disabled={!canEdit || saving}
                  className="px-4 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500 disabled:opacity-50"
                >
                  {saving ? 'Saving…' : 'Save tool'}
                </button>
              </div>
            </>
          )}

          <div>
            <h3 className="text-sm font-semibold mb-2">Call stats</h3>
            <div className="text-xs text-zinc-500 mb-2">
              Since: {stats?.sinceUtc ? new Date(stats.sinceUtc).toLocaleString() : '-'}
            </div>
            <div className="rounded-lg border border-zinc-800 bg-zinc-950 overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-zinc-800/70 text-zinc-300">
                  <tr>
                    <th className="text-left px-3 py-2">Tool</th>
                    <th className="text-left px-3 py-2">Success</th>
                    <th className="text-left px-3 py-2">Errors</th>
                    <th className="text-left px-3 py-2">Avg ms</th>
                    <th className="text-left px-3 py-2">Max ms</th>
                  </tr>
                </thead>
                <tbody>
                  {stats?.rows.map(row => (
                    <tr key={row.toolId} className="border-t border-zinc-800">
                      <td className="px-3 py-2">{row.toolName}</td>
                      <td className="px-3 py-2">{row.successes}</td>
                      <td className="px-3 py-2">{row.errors}</td>
                      <td className="px-3 py-2">{Math.round(row.avgMs)}</td>
                      <td className="px-3 py-2">{Math.round(row.maxMs)}</td>
                    </tr>
                  ))}
                  {(!stats || stats.rows.length === 0) && (
                    <tr>
                      <td colSpan={5} className="px-3 py-5 text-center text-zinc-500">No calls recorded yet.</td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </section>
      </div>
    </div>
  )
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded border border-zinc-800 bg-zinc-950 px-3 py-2">
      <div className="text-zinc-500 text-xs uppercase tracking-wide">{label}</div>
      <div className="text-zinc-200 mt-1 break-all">{value}</div>
    </div>
  )
}
