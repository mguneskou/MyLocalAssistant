import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type { ActiveEmbeddingStatusDto, ActiveModelStatusDto, ModelDto } from '../../api/types'

export default function AdminModelsTab() {
  const [models, setModels] = useState<ModelDto[]>([])
  const [chatStatus, setChatStatus] = useState<ActiveModelStatusDto | null>(null)
  const [embeddingStatus, setEmbeddingStatus] = useState<ActiveEmbeddingStatusDto | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const selected = useMemo(
    () => models.find(m => m.id === selectedId) ?? null,
    [models, selectedId],
  )

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [modelsResp, chatResp, embedResp] = await Promise.all([
        api.listModels(),
        api.getModelStatus(),
        api.getEmbeddingStatus(),
      ])
      setModels(modelsResp)
      setChatStatus(chatResp)
      setEmbeddingStatus(embedResp)
      if (!selectedId && modelsResp.length > 0) setSelectedId(modelsResp[0].id)
      if (selectedId && !modelsResp.some(m => m.id === selectedId))
        setSelectedId(modelsResp[0]?.id ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load models.')
    } finally {
      setLoading(false)
    }
  }, [selectedId])

  useEffect(() => {
    load()
  }, [load])

  const canDownload = selected?.isCloud === false && selected?.isInstalled === false && selected?.download?.stage !== 'Downloading'
  const canCancel = selected?.download && ['Queued', 'Downloading', 'Verifying'].includes(selected.download.stage)
  const canActivate =
    !!selected &&
    selected.isActiveEmbedding === false &&
    (!selected.isActive || selected.isActiveFailed) &&
    (selected.isCloud ? selected.isCloudConfigured : selected.isInstalled)
  const canDeactivate = selected?.isActive === true
  const canDelete =
    selected?.isCloud === false &&
    selected?.isInstalled === true &&
    selected?.isActive === false &&
    selected?.isActiveEmbedding === false &&
    !canCancel

  async function runAction(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      await action()
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Action failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Models</h1>
          <p className="text-sm text-zinc-500 mt-1">Manage chat and embedding models from the web admin.</p>
        </div>
        <button
          onClick={load}
          className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          disabled={loading || busy}
        >
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-sm grid grid-cols-1 lg:grid-cols-2 gap-2">
        <div className="text-zinc-300">
          Chat: <span className="font-medium">{chatStatus?.activeModelId ?? '(none)'}</span>
          <span className="text-zinc-500"> — {chatStatus?.status ?? 'unknown'}</span>
        </div>
        <div className="text-zinc-300">
          Embedding: <span className="font-medium">{embeddingStatus?.activeModelId ?? '(none)'}</span>
          <span className="text-zinc-500"> — {embeddingStatus?.status ?? 'unknown'}</span>
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        <ActionButton disabled={!canDownload || busy} onClick={() => runAction(() => api.startDownload(selected!.id))}>Download</ActionButton>
        <ActionButton disabled={!canCancel || busy} onClick={() => runAction(() => api.cancelDownload(selected!.id))}>Cancel download</ActionButton>
        <ActionButton
          disabled={!canActivate || busy}
          onClick={() => runAction(async () => {
            if (!selected) return
            if (selected.tier.toLowerCase() === 'embedding') {
              await api.activateEmbedding(selected.id)
            } else {
              await api.activateModel(selected.id)
            }
          })}
        >
          Activate
        </ActionButton>
        <ActionButton
          disabled={!canDeactivate || busy}
          onClick={() => {
            if (!selected) return
            if (!window.confirm(`Deactivate '${selected.displayName}'?`)) return
            void runAction(() => api.deactivateModel())
          }}
        >
          Deactivate
        </ActionButton>
        <ActionButton
          disabled={!canDelete || busy}
          onClick={() => {
            if (!selected) return
            if (!window.confirm(`Delete local files for '${selected.displayName}'?`)) return
            void runAction(() => api.deleteModel(selected.id))
          }}
        >
          Delete files
        </ActionButton>
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
              <th className="text-left px-3 py-2">Model</th>
              <th className="text-left px-3 py-2">Tier</th>
              <th className="text-left px-3 py-2">Source</th>
              <th className="text-left px-3 py-2">Size</th>
              <th className="text-left px-3 py-2">Status</th>
            </tr>
          </thead>
          <tbody>
            {models.map(m => {
              const isSelected = m.id === selectedId
              return (
                <tr
                  key={m.id}
                  onClick={() => setSelectedId(m.id)}
                  className={`cursor-pointer border-t border-zinc-800 ${isSelected ? 'bg-zinc-800/70' : 'hover:bg-zinc-800/40'}`}
                >
                  <td className="px-3 py-2">
                    <div className="font-medium text-zinc-100">{m.displayName}</div>
                    <div className="text-zinc-500 text-xs">{m.id}</div>
                  </td>
                  <td className="px-3 py-2 text-zinc-300">{m.tier}</td>
                  <td className="px-3 py-2 text-zinc-300">{m.isCloud ? `Cloud (${m.source})` : 'Local'}</td>
                  <td className="px-3 py-2 text-zinc-300">{m.isCloud ? '—' : formatBytes(m.totalBytes)}</td>
                  <td className="px-3 py-2 text-zinc-300">{renderStatus(m)}</td>
                </tr>
              )
            })}
            {models.length === 0 && (
              <tr>
                <td colSpan={5} className="px-3 py-6 text-center text-zinc-500">No models found.</td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function ActionButton({
  children,
  disabled,
  onClick,
}: {
  children: string
  disabled?: boolean
  onClick: () => void
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
    >
      {children}
    </button>
  )
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes
  let i = -1
  do {
    value /= 1024
    i++
  } while (value >= 1024 && i < units.length - 1)
  return `${value.toFixed(1)} ${units[i]}`
}

function renderStatus(model: ModelDto): string {
  const d = model.download
  if (d) {
    if (d.stage === 'Downloading' && d.totalBytes > 0) {
      const pct = Math.round((d.bytes * 100) / d.totalBytes)
      return `Downloading ${pct}%`
    }
    if (d.stage === 'Verifying') return 'Verifying…'
    if (d.stage === 'Failed') return `Failed: ${d.error ?? ''}`
    if (d.stage === 'Cancelled') return 'Cancelled'
    if (d.stage === 'Completed') return 'Installed'
    return d.stage
  }
  if (model.isActive && model.isActiveFailed) return 'Active (load failed)'
  if (model.isActive) return 'Installed (active chat)'
  if (model.isActiveEmbedding) return 'Installed (active embedding)'
  if (model.isCloud) return model.isCloudConfigured ? 'Cloud (key configured)' : 'Cloud (no key configured)'
  if (model.isInstalled) return 'Installed'
  return 'Available'
}
