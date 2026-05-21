import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type { ActiveEmbeddingStatusDto, ActiveModelStatusDto, ModelDto } from '../../api/types'

const LIVE_DOWNLOAD_STAGES = new Set(['Queued', 'Downloading', 'Verifying'])

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
  const hasLiveDownloads = useMemo(
    () => models.some(isDownloadRunning),
    [models],
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
      if (selectedId && !modelsResp.some(m => m.id === selectedId)) {
        setSelectedId(modelsResp[0]?.id ?? null)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load models.')
    } finally {
      setLoading(false)
    }
  }, [selectedId])

  useEffect(() => {
    load()
  }, [load])

  useEffect(() => {
    if (!hasLiveDownloads) return
    const id = window.setInterval(() => {
      void load()
    }, 1500)
    return () => window.clearInterval(id)
  }, [hasLiveDownloads, load])

  const canCancel = !!selected?.download && LIVE_DOWNLOAD_STAGES.has(selected.download.stage)
  const canDownload = selected?.isCloud === false && selected?.isInstalled === false && !canCancel
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
          {loading ? 'Refreshing...' : 'Refresh'}
        </button>
      </div>

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-sm grid grid-cols-1 lg:grid-cols-2 gap-2">
        <div className="text-zinc-300">
          Chat: <span className="font-medium">{chatStatus?.activeModelId ?? '(none)'}</span>
          <span className="text-zinc-500"> - {chatStatus?.status ?? 'unknown'}</span>
        </div>
        <div className="text-zinc-300">
          Embedding: <span className="font-medium">{embeddingStatus?.activeModelId ?? '(none)'}</span>
          <span className="text-zinc-500"> - {embeddingStatus?.status ?? 'unknown'}</span>
        </div>
      </div>

      {hasLiveDownloads && (
        <div className="rounded-lg border border-blue-900/60 bg-blue-950/30 text-blue-200 px-4 py-3 text-sm">
          Download status is auto-refreshing every 1.5 seconds.
        </div>
      )}

      <div className="text-xs text-zinc-500">
        Selected model: <span className="text-zinc-300">{selected?.displayName ?? '(none)'}</span>
      </div>

      <div className="flex flex-wrap gap-2">
        <ActionButton disabled={!canDownload || busy} onClick={() => runAction(() => api.startDownload(selected!.id))}>Download</ActionButton>
        <ActionButton disabled={!canCancel || busy} onClick={() => runAction(() => api.cancelDownload(selected!.id))}>Cancel download</ActionButton>
        <ActionButton
          disabled={!canDeactivate || busy}
          onClick={() => {
            if (!selected) return
            if (!window.confirm(`Deactivate '${selected.displayName}'?`)) return
            void runAction(() => api.deactivateModel())
          }}
        >
          Deactivate chat
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
              <th className="text-left px-3 py-2">Role</th>
              <th className="text-left px-3 py-2">Source</th>
              <th className="text-left px-3 py-2">Size</th>
              <th className="text-left px-3 py-2">Min RAM</th>
              <th className="text-left px-3 py-2">License</th>
              <th className="text-left px-3 py-2">Status</th>
              <th className="text-left px-3 py-2">Checklist Select</th>
            </tr>
          </thead>
          <tbody>
            {models.map(m => {
              const isEmbedding = isEmbeddingModel(m)
              const activeForRole = isEmbedding ? m.isActiveEmbedding : m.isActive
              const readyForRole = isEmbedding ? m.isInstalled : (m.isCloud ? m.isCloudConfigured : m.isInstalled)
              const blockedByDownload = isDownloadRunning(m)
              const canSelect = !busy && readyForRole && !blockedByDownload
              const selectLabel = activeForRole
                ? 'Selected'
                : !readyForRole
                  ? (m.isCloud ? 'Configure cloud key' : 'Install to activate')
                  : blockedByDownload
                    ? 'Wait for download'
                    : 'Select'

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
                  <td className="px-3 py-2 text-zinc-300">
                    <div className="inline-flex items-center px-2 py-0.5 rounded-full text-xs border border-zinc-700 bg-zinc-800/70">
                      {isEmbedding ? 'Embedding' : 'Chat'}
                    </div>
                    <div className="text-zinc-500 text-xs mt-1">Tier: {m.tier}</div>
                  </td>
                  <td className="px-3 py-2 text-zinc-300">{m.isCloud ? `Cloud (${m.source})` : 'Local'}</td>
                  <td className="px-3 py-2 text-zinc-300">{m.isCloud ? '-' : formatBytes(m.totalBytes)}</td>
                  <td className="px-3 py-2 text-zinc-300">{formatMinRam(m)}</td>
                  <td className="px-3 py-2 text-zinc-300">{renderLicense(m)}</td>
                  <td className="px-3 py-2 text-zinc-300">{renderStatusCell(m)}</td>
                  <td className="px-3 py-2">
                    <label className={`inline-flex items-center gap-2 text-xs ${activeForRole ? 'text-emerald-200' : 'text-zinc-300'}`}>
                      <input
                        type="radio"
                        name={isEmbedding ? 'embedding-active-model' : 'chat-active-model'}
                        checked={activeForRole}
                        disabled={!activeForRole && !canSelect}
                        onClick={e => e.stopPropagation()}
                        onChange={e => {
                          e.stopPropagation()
                          if (activeForRole || !canSelect) return
                          void runAction(() => isEmbedding ? api.activateEmbedding(m.id) : api.activateModel(m.id))
                        }}
                      />
                      <span>{isEmbedding ? `Embedding ${selectLabel}` : `Chat ${selectLabel}`}</span>
                    </label>
                  </td>
                </tr>
              )
            })}
            {models.length === 0 && (
              <tr>
                <td colSpan={8} className="px-3 py-6 text-center text-zinc-500">No models found.</td>
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

function formatEta(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return 'calculating'
  const total = Math.round(seconds)
  const m = Math.floor(total / 60)
  const s = total % 60
  if (m <= 0) return `${s}s`
  if (m < 60) return `${m}m ${s}s`
  const h = Math.floor(m / 60)
  const mm = m % 60
  return `${h}h ${mm}m`
}

function isEmbeddingModel(model: ModelDto): boolean {
  return model.tier.toLowerCase() === 'embedding'
}

function isDownloadRunning(model: ModelDto): boolean {
  const stage = model.download?.stage
  return !!stage && LIVE_DOWNLOAD_STAGES.has(stage)
}

function formatMinRam(model: ModelDto): string {
  if (model.isCloud) return '-'
  if (!Number.isFinite(model.minRamGb) || model.minRamGb <= 0) return 'n/a'
  return `${model.minRamGb} GB`
}

function renderLicense(model: ModelDto) {
  const license = model.license?.trim()
  const url = model.licenseUrl?.trim()
  if (!license && !url) return <span className="text-zinc-500">n/a</span>
  if (!url) return <span>{license ?? 'n/a'}</span>
  return (
    <a
      href={url}
      target="_blank"
      rel="noreferrer"
      className="text-blue-300 hover:text-blue-200 underline"
      onClick={e => e.stopPropagation()}
    >
      {license || 'View'}
    </a>
  )
}

function renderStatusCell(model: ModelDto) {
  const d = model.download
  if (d) {
    const pct = d.totalBytes > 0 ? Math.min(100, Math.max(0, Math.round((d.bytes * 100) / d.totalBytes))) : null
    if (d.stage === 'Queued') {
      return <span className="text-zinc-300">Queued...</span>
    }
    if (d.stage === 'Downloading' && pct !== null) {
      return (
        <div className="space-y-1.5">
          <div className="text-zinc-100">Downloading {pct}%</div>
          <div className="h-1.5 rounded bg-zinc-800 overflow-hidden">
            <div className="h-full bg-blue-500" style={{ width: `${pct}%` }} />
          </div>
          <div className="text-xs text-zinc-500">
            {formatBytes(d.bytes)} / {formatBytes(d.totalBytes)}
            {d.bytesPerSecond > 0 ? ` · ${formatBytes(d.bytesPerSecond)}/s` : ''}
            {d.etaSeconds > 0 ? ` · ETA ${formatEta(d.etaSeconds)}` : ''}
          </div>
        </div>
      )
    }
    if (d.stage === 'Downloading') {
      return (
        <div className="space-y-1.5">
          <div className="text-zinc-100">Downloading...</div>
          <div className="text-xs text-zinc-500">{formatBytes(d.bytes)} downloaded</div>
        </div>
      )
    }
    if (d.stage === 'Verifying') return <span className="text-zinc-300">Verifying...</span>
    if (d.stage === 'Failed') return <span className="text-red-300">Failed: {d.error ?? 'Unknown error'}</span>
    if (d.stage === 'Cancelled') return <span className="text-zinc-400">Cancelled</span>
    if (d.stage === 'Completed') return <span className="text-emerald-300">Installed</span>
    return <span className="text-zinc-300">{d.stage}</span>
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {model.isActive && (
        <span className="px-2 py-0.5 rounded-full text-xs border border-emerald-700 bg-emerald-950/40 text-emerald-200">
          Active Chat
        </span>
      )}
      {model.isActiveEmbedding && (
        <span className="px-2 py-0.5 rounded-full text-xs border border-cyan-700 bg-cyan-950/40 text-cyan-200">
          Active Embedding
        </span>
      )}
      {model.isActiveFailed && (
        <span className="px-2 py-0.5 rounded-full text-xs border border-red-700 bg-red-950/40 text-red-200">
          Load Failed
        </span>
      )}
      {!model.isActive && !model.isActiveEmbedding && model.isInstalled && (
        <span className="px-2 py-0.5 rounded-full text-xs border border-zinc-700 bg-zinc-800/70 text-zinc-300">
          Installed
        </span>
      )}
      {model.isCloud && (
        <span
          className={`px-2 py-0.5 rounded-full text-xs border ${
            model.isCloudConfigured
              ? 'border-blue-700 bg-blue-950/40 text-blue-200'
              : 'border-amber-700 bg-amber-950/40 text-amber-200'
          }`}
        >
          {model.isCloudConfigured ? 'Cloud Ready' : 'Cloud Key Missing'}
        </span>
      )}
      {!model.isCloud && !model.isInstalled && (
        <span className="px-2 py-0.5 rounded-full text-xs border border-zinc-700 bg-zinc-800/70 text-zinc-400">
          Not Installed
        </span>
      )}
    </div>
  )
}
