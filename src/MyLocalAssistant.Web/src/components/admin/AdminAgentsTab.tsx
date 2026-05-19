import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import { useAuth } from '../../contexts/AuthContext'
import type { AgentDto, AgentUpdateRequest, ModelDto, RagCollectionDto, ToolDto } from '../../api/types'
import AdminAgentPromptTestModal from './AdminAgentPromptTestModal'

interface AgentDraft {
  enabled: boolean
  defaultModelId: string
  ragEnabled: boolean
  ragCollectionIds: string[]
  systemPrompt: string
  toolIds: string[]
  maxToolCalls: string
  scenarioNotes: string
}

function toDraft(agent: AgentDto): AgentDraft {
  return {
    enabled: agent.enabled,
    defaultModelId: agent.defaultModelId ?? '',
    ragEnabled: agent.ragEnabled,
    ragCollectionIds: agent.ragCollectionIds,
    systemPrompt: agent.systemPrompt,
    toolIds: agent.toolIds ?? [],
    maxToolCalls: agent.maxToolCalls == null ? '' : String(agent.maxToolCalls),
    scenarioNotes: agent.scenarioNotes ?? '',
  }
}

export default function AdminAgentsTab() {
  const { user } = useAuth()
  const canEdit = !!user?.isGlobalAdmin

  const [agents, setAgents] = useState<AgentDto[]>([])
  const [models, setModels] = useState<ModelDto[]>([])
  const [collections, setCollections] = useState<RagCollectionDto[]>([])
  const [tools, setTools] = useState<ToolDto[]>([])

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [draft, setDraft] = useState<AgentDraft | null>(null)

  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState('')
  const [showPromptTest, setShowPromptTest] = useState(false)

  const selected = useMemo(
    () => agents.find(a => a.id === selectedId) ?? null,
    [agents, selectedId],
  )

  const modelOptions = useMemo(
    () => models.filter(m => m.isInstalled || m.isCloudConfigured),
    [models],
  )

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [agentsResp, modelsResp, collectionsResp, toolsResp] = await Promise.all([
        api.listAdminAgents(),
        api.listModels(),
        api.listCollections(),
        api.listTools(),
      ])
      setAgents(agentsResp)
      setModels(modelsResp)
      setCollections(collectionsResp)
      setTools(toolsResp)
      setSelectedId(prev => {
        if (prev && agentsResp.some(a => a.id === prev)) return prev
        return agentsResp[0]?.id ?? null
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load agent administration data.')
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

  function updateDraft<K extends keyof AgentDraft>(key: K, value: AgentDraft[K]) {
    setDraft(prev => (prev ? { ...prev, [key]: value } : prev))
  }

  function toggleDraftValue(key: 'ragCollectionIds' | 'toolIds', value: string) {
    setDraft(prev => {
      if (!prev) return prev
      const current = prev[key]
      const next = current.includes(value) ? current.filter(v => v !== value) : [...current, value]
      return { ...prev, [key]: next }
    })
  }

  async function onSave() {
    if (!selected || !draft || !canEdit) return

    const normalizedMaxToolCalls = draft.maxToolCalls.trim() === ''
      ? null
      : Math.max(1, Math.min(100, Number.parseInt(draft.maxToolCalls, 10) || 1))

    const req: AgentUpdateRequest = {
      enabled: draft.enabled,
      defaultModelId: draft.defaultModelId.trim() ? draft.defaultModelId : null,
      ragEnabled: draft.ragEnabled,
      ragCollectionIds: draft.ragCollectionIds,
      systemPrompt: draft.systemPrompt,
      toolIds: draft.toolIds,
      maxToolCalls: normalizedMaxToolCalls,
      scenarioNotes: draft.scenarioNotes.trim() || null,
    }

    setSaving(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateAgent(selected.id, req)
      setAgents(prev => prev.map(a => (a.id === updated.id ? updated : a)))
      setDraft(toDraft(updated))
      setStatus(`Saved '${updated.name}'.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save agent.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="p-6 h-full flex flex-col gap-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold">Agents</h1>
          <p className="text-sm text-zinc-500 mt-1">Agent prompt, model, tools, and RAG routing configuration.</p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setShowPromptTest(true)}
            disabled={!selected || loading}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            Prompt test…
          </button>
          <button
            onClick={load}
            disabled={loading || saving}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
          >
            {loading ? 'Loading…' : 'Refresh'}
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

      <div className="flex-1 min-h-0 grid grid-cols-1 xl:grid-cols-[320px_1fr] gap-4">
        <aside className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-auto">
          <div className="px-3 py-2 border-b border-zinc-800 text-xs uppercase tracking-wide text-zinc-400">
            Agents
          </div>
          <ul className="divide-y divide-zinc-800">
            {agents.map(agent => {
              const active = agent.id === selectedId
              return (
                <li key={agent.id}>
                  <button
                    onClick={() => setSelectedId(agent.id)}
                    className={`w-full text-left px-3 py-3 ${active ? 'bg-zinc-800/70' : 'hover:bg-zinc-800/40'}`}
                  >
                    <div className="font-medium text-zinc-100">{agent.name}</div>
                    <div className="text-xs text-zinc-400 mt-1 flex items-center gap-2">
                      <span>{agent.category}</span>
                      <span>•</span>
                      <span>{agent.enabled ? 'enabled' : 'disabled'}</span>
                    </div>
                  </button>
                </li>
              )
            })}
          </ul>
        </aside>

        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 overflow-auto">
          {!selected || !draft ? (
            <div className="text-zinc-500 text-sm">No agent selected.</div>
          ) : (
            <div className="space-y-5">
              <div>
                <h2 className="text-lg font-semibold">{selected.name}</h2>
                <p className="text-sm text-zinc-400 mt-1">{selected.description || 'No description.'}</p>
              </div>

              <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
                <label className="text-sm">
                  <span className="block text-zinc-400 mb-1">Default model</span>
                  <select
                    value={draft.defaultModelId}
                    disabled={!canEdit || saving}
                    onChange={e => updateDraft('defaultModelId', e.target.value)}
                    className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                  >
                    <option value="">(use active model)</option>
                    {modelOptions.map(model => (
                      <option key={model.id} value={model.id}>{model.displayName} ({model.id})</option>
                    ))}
                  </select>
                </label>

                <label className="text-sm">
                  <span className="block text-zinc-400 mb-1">Max tool calls</span>
                  <input
                    type="number"
                    min={1}
                    max={100}
                    value={draft.maxToolCalls}
                    disabled={!canEdit || saving}
                    onChange={e => updateDraft('maxToolCalls', e.target.value)}
                    className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                    placeholder="Default"
                  />
                </label>
              </div>

              <div className="flex flex-wrap gap-4 text-sm">
                <label className="inline-flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={draft.enabled}
                    disabled={!canEdit || saving}
                    onChange={e => updateDraft('enabled', e.target.checked)}
                  />
                  Enabled
                </label>
                <label className="inline-flex items-center gap-2">
                  <input
                    type="checkbox"
                    checked={draft.ragEnabled}
                    disabled={!canEdit || saving}
                    onChange={e => updateDraft('ragEnabled', e.target.checked)}
                  />
                  RAG enabled
                </label>
              </div>

              <div>
                <h3 className="text-sm font-semibold mb-2">RAG collections</h3>
                <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 grid grid-cols-1 md:grid-cols-2 gap-2 max-h-48 overflow-auto">
                  {collections.map(c => (
                    <label key={c.id} className="inline-flex items-center gap-2 text-sm text-zinc-300">
                      <input
                        type="checkbox"
                        checked={draft.ragCollectionIds.includes(c.id)}
                        disabled={!canEdit || saving || !draft.ragEnabled}
                        onChange={() => toggleDraftValue('ragCollectionIds', c.id)}
                      />
                      <span>{c.name}</span>
                      <span className="text-zinc-500">({c.documentCount} docs)</span>
                    </label>
                  ))}
                </div>
              </div>

              <div>
                <h3 className="text-sm font-semibold mb-2">Tools</h3>
                <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 grid grid-cols-1 md:grid-cols-2 gap-2 max-h-56 overflow-auto">
                  {tools.map(t => (
                    <label key={t.id} className="inline-flex items-center gap-2 text-sm text-zinc-300">
                      <input
                        type="checkbox"
                        checked={draft.toolIds.includes(t.id)}
                        disabled={!canEdit || saving}
                        onChange={() => toggleDraftValue('toolIds', t.id)}
                      />
                      <span>{t.name}</span>
                      <span className="text-zinc-500">({t.enabled ? 'enabled' : 'disabled'})</span>
                    </label>
                  ))}
                </div>
              </div>

              <label className="block text-sm">
                <span className="block text-zinc-400 mb-1">System prompt</span>
                <textarea
                  value={draft.systemPrompt}
                  disabled={!canEdit || saving}
                  onChange={e => updateDraft('systemPrompt', e.target.value)}
                  rows={8}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                />
              </label>

              <label className="block text-sm">
                <span className="block text-zinc-400 mb-1">Scenario notes</span>
                <textarea
                  value={draft.scenarioNotes}
                  disabled={!canEdit || saving}
                  onChange={e => updateDraft('scenarioNotes', e.target.value)}
                  rows={4}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 disabled:opacity-60"
                  placeholder="Optional scenario guidance"
                />
              </label>

              <div className="flex justify-end">
                <button
                  onClick={onSave}
                  disabled={!canEdit || saving}
                  className="px-4 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500 disabled:opacity-50"
                >
                  {saving ? 'Saving…' : 'Save agent'}
                </button>
              </div>
            </div>
          )}
        </section>
      </div>

      {showPromptTest && selected && (
        <AdminAgentPromptTestModal
          agent={selected}
          onClose={() => setShowPromptTest(false)}
        />
      )}
    </div>
  )
}
