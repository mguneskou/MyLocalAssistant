import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import type {
  ActiveEmbeddingStatusDto,
  CollectionGrantDto,
  DepartmentDto,
  RagCollectionDto,
  RagDocumentDto,
  RoleDto,
  UserAdminDto,
} from '../../api/types'

type PrincipalKind = 'User' | 'Department' | 'Role'

const ACCESS_MODES = ['Restricted', 'Public'] as const

export default function AdminRagTab() {
  const [embeddingStatus, setEmbeddingStatus] = useState<ActiveEmbeddingStatusDto | null>(null)
  const [collections, setCollections] = useState<RagCollectionDto[]>([])
  const [selectedCollectionId, setSelectedCollectionId] = useState<string | null>(null)
  const [documents, setDocuments] = useState<RagDocumentDto[]>([])
  const [grants, setGrants] = useState<CollectionGrantDto[]>([])

  const [users, setUsers] = useState<UserAdminDto[]>([])
  const [departments, setDepartments] = useState<DepartmentDto[]>([])
  const [roles, setRoles] = useState<RoleDto[]>([])

  const [newCollectionName, setNewCollectionName] = useState('')
  const [newCollectionDescription, setNewCollectionDescription] = useState('')
  const [newCollectionAccessMode, setNewCollectionAccessMode] = useState<(typeof ACCESS_MODES)[number]>('Restricted')

  const [editDescription, setEditDescription] = useState('')
  const [editAccessMode, setEditAccessMode] = useState<(typeof ACCESS_MODES)[number]>('Restricted')

  const [grantKind, setGrantKind] = useState<PrincipalKind>('User')
  const [grantPrincipalId, setGrantPrincipalId] = useState('')

  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState('')

  const selectedCollection = useMemo(
    () => collections.find(c => c.id === selectedCollectionId) ?? null,
    [collections, selectedCollectionId],
  )

  const principalOptions = useMemo(() => {
    if (grantKind === 'User') {
      return users.map(u => ({ id: u.id, label: `${u.displayName} (${u.username})` }))
    }
    if (grantKind === 'Department') {
      return departments.map(d => ({ id: d.id, label: d.name }))
    }
    return roles.map(r => ({ id: r.id, label: r.name }))
  }, [grantKind, users, departments, roles])

  const loadBase = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [collectionsResp, usersResp, depsResp, rolesResp, embeddingResp] = await Promise.all([
        api.listCollections(),
        api.listUsers(),
        api.listDepartments(),
        api.listRoles(),
        api.getEmbeddingStatus(),
      ])
      setCollections(collectionsResp)
      setUsers(usersResp)
      setDepartments(depsResp)
      setRoles(rolesResp)
      setEmbeddingStatus(embeddingResp)
      setSelectedCollectionId(prev => {
        if (prev && collectionsResp.some(c => c.id === prev)) return prev
        return collectionsResp[0]?.id ?? null
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load RAG administration data.')
    } finally {
      setLoading(false)
    }
  }, [])

  const loadCollectionDetails = useCallback(async (collectionId: string) => {
    try {
      const [docsResp, grantsResp] = await Promise.all([
        api.listCollectionDocuments(collectionId),
        api.listCollectionGrants(collectionId),
      ])
      setDocuments(docsResp)
      setGrants(grantsResp)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load collection details.')
    }
  }, [])

  useEffect(() => {
    loadBase()
  }, [loadBase])

  useEffect(() => {
    if (!selectedCollection) {
      setDocuments([])
      setGrants([])
      return
    }
    setEditDescription(selectedCollection.description ?? '')
    setEditAccessMode(selectedCollection.accessMode === 'Public' ? 'Public' : 'Restricted')
    loadCollectionDetails(selectedCollection.id)
  }, [selectedCollection, loadCollectionDetails])

  useEffect(() => {
    setGrantPrincipalId(principalOptions[0]?.id ?? '')
  }, [principalOptions])

  async function onCreateCollection() {
    if (!newCollectionName.trim()) return
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      const created = await api.createCollection({
        name: newCollectionName.trim(),
        description: newCollectionDescription.trim() || null,
        accessMode: newCollectionAccessMode,
      })
      setCollections(prev => [...prev, created])
      setSelectedCollectionId(created.id)
      setNewCollectionName('')
      setNewCollectionDescription('')
      setNewCollectionAccessMode('Restricted')
      setStatus(`Collection '${created.name}' created.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create collection.')
    } finally {
      setBusy(false)
    }
  }

  async function onSaveCollection() {
    if (!selectedCollection) return
    setBusy(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateCollection(selectedCollection.id, {
        description: editDescription.trim() || null,
        accessMode: editAccessMode,
      })
      setCollections(prev => prev.map(c => (c.id === updated.id ? updated : c)))
      setStatus(`Collection '${updated.name}' saved.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save collection.')
    } finally {
      setBusy(false)
    }
  }

  async function onDeleteCollection() {
    if (!selectedCollection) return
    if (!window.confirm(`Delete collection '${selectedCollection.name}' and all documents?`)) return

    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.deleteCollection(selectedCollection.id)
      setCollections(prev => prev.filter(c => c.id !== selectedCollection.id))
      setSelectedCollectionId(prev => (prev === selectedCollection.id ? null : prev))
      setStatus(`Collection '${selectedCollection.name}' deleted.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete collection.')
    } finally {
      setBusy(false)
    }
  }

  async function onUploadFiles(fileList: FileList | null) {
    if (!selectedCollection || !fileList || fileList.length === 0) return

    setBusy(true)
    setError(null)
    setStatus('')
    try {
      for (const file of Array.from(fileList)) {
        await api.uploadCollectionDocument(selectedCollection.id, file)
      }
      await loadCollectionDetails(selectedCollection.id)
      await loadBase()
      setStatus(`Uploaded ${fileList.length} file(s).`)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to upload document(s).')
    } finally {
      setBusy(false)
    }
  }

  async function onDeleteDocument(documentId: string) {
    if (!selectedCollection) return
    if (!window.confirm('Delete this document?')) return

    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.deleteCollectionDocument(selectedCollection.id, documentId)
      setDocuments(prev => prev.filter(d => d.id !== documentId))
      await loadBase()
      setStatus('Document deleted.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete document.')
    } finally {
      setBusy(false)
    }
  }

  async function onAddGrant() {
    if (!selectedCollection || !grantPrincipalId) return

    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.addCollectionGrant(selectedCollection.id, {
        principalKind: grantKind,
        principalId: grantPrincipalId,
      })
      setGrants(await api.listCollectionGrants(selectedCollection.id))
      await loadBase()
      setStatus('Grant added.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to add grant.')
    } finally {
      setBusy(false)
    }
  }

  async function onRemoveGrant(grantId: number) {
    if (!selectedCollection) return
    if (!window.confirm('Remove this grant?')) return

    setBusy(true)
    setError(null)
    setStatus('')
    try {
      await api.removeCollectionGrant(selectedCollection.id, grantId)
      setGrants(prev => prev.filter(g => g.id !== grantId))
      await loadBase()
      setStatus('Grant removed.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove grant.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="p-6 h-full flex flex-col gap-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold">RAG</h1>
          <p className="text-sm text-zinc-500 mt-1">Manage collections, documents, and principal grants.</p>
        </div>
        <button
          onClick={loadBase}
          disabled={loading || busy}
          className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
        >
          {loading ? 'Loading…' : 'Refresh'}
        </button>
      </div>

      {error && <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">{error}</div>}
      {status && <div className="rounded-lg border border-emerald-800 bg-emerald-950/20 text-emerald-300 px-4 py-3 text-sm">{status}</div>}

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-3 text-sm">
        <span className="text-zinc-300">Embedding runtime:</span>{' '}
        <span className="font-medium text-zinc-100">{embeddingStatus?.activeModelId ?? '(none)'}</span>{' '}
        <span className="text-zinc-500">- {embeddingStatus?.status ?? 'unknown'}</span>
        {(embeddingStatus?.status ?? '').toLowerCase() === 'failed' && embeddingStatus?.lastError && (
          <div className="text-xs text-red-300 mt-1">{embeddingStatus.lastError}</div>
        )}
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-[320px_1fr] gap-4 min-h-0 flex-1">
        <aside className="rounded-xl border border-zinc-800 bg-zinc-900 overflow-auto">
          <div className="px-3 py-2 border-b border-zinc-800 text-xs uppercase tracking-wide text-zinc-400">Collections</div>
          <ul className="divide-y divide-zinc-800">
            {collections.map(c => {
              const active = c.id === selectedCollectionId
              return (
                <li key={c.id}>
                  <button
                    onClick={() => setSelectedCollectionId(c.id)}
                    className={`w-full text-left px-3 py-3 ${active ? 'bg-zinc-800/70' : 'hover:bg-zinc-800/40'}`}
                  >
                    <div className="font-medium text-zinc-100">{c.name}</div>
                    <div className="text-xs text-zinc-400 mt-1">
                      {c.accessMode} • {c.documentCount} docs • {c.grantCount} grants
                    </div>
                  </button>
                </li>
              )
            })}
          </ul>
        </aside>

        <section className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 overflow-auto space-y-5">
          <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 space-y-3">
            <h2 className="text-sm font-semibold">Create collection</h2>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
              <input
                type="text"
                value={newCollectionName}
                onChange={e => setNewCollectionName(e.target.value)}
                placeholder="Name"
                className="px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
              />
              <select
                value={newCollectionAccessMode}
                onChange={e => setNewCollectionAccessMode(e.target.value as (typeof ACCESS_MODES)[number])}
                className="px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
              >
                {ACCESS_MODES.map(mode => (
                  <option key={mode} value={mode}>{mode}</option>
                ))}
              </select>
              <button
                onClick={onCreateCollection}
                disabled={busy || !newCollectionName.trim()}
                className="px-3 py-2 rounded bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-sm"
              >
                Create
              </button>
            </div>
            <textarea
              value={newCollectionDescription}
              onChange={e => setNewCollectionDescription(e.target.value)}
              rows={2}
              placeholder="Description (optional)"
              className="w-full px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
            />
          </div>

          {!selectedCollection ? (
            <div className="text-sm text-zinc-500">Select a collection to manage documents and permissions.</div>
          ) : (
            <>
              <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 space-y-3">
                <h2 className="text-sm font-semibold">Collection settings: {selectedCollection.name}</h2>
                <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
                  <select
                    value={editAccessMode}
                    onChange={e => setEditAccessMode(e.target.value as (typeof ACCESS_MODES)[number])}
                    className="px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
                  >
                    {ACCESS_MODES.map(mode => (
                      <option key={mode} value={mode}>{mode}</option>
                    ))}
                  </select>
                  <button
                    onClick={onSaveCollection}
                    disabled={busy}
                    className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50 text-sm"
                  >
                    Save collection
                  </button>
                  <button
                    onClick={onDeleteCollection}
                    disabled={busy}
                    className="px-3 py-2 rounded bg-red-700/80 hover:bg-red-700 disabled:opacity-50 text-sm"
                  >
                    Delete collection
                  </button>
                </div>
                <textarea
                  value={editDescription}
                  onChange={e => setEditDescription(e.target.value)}
                  rows={2}
                  className="w-full px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
                  placeholder="Description"
                />
              </div>

              <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 space-y-3">
                <h2 className="text-sm font-semibold">Documents</h2>
                <input
                  type="file"
                  multiple
                  disabled={busy}
                  onChange={e => {
                    void onUploadFiles(e.target.files)
                    e.currentTarget.value = ''
                  }}
                  className="block text-sm text-zinc-300"
                />
                <div className="rounded border border-zinc-800 overflow-hidden">
                  <table className="w-full text-sm">
                    <thead className="bg-zinc-800/70 text-zinc-300">
                      <tr>
                        <th className="text-left px-3 py-2">File</th>
                        <th className="text-left px-3 py-2">Chunks</th>
                        <th className="text-left px-3 py-2">Size</th>
                        <th className="text-left px-3 py-2">Ingested</th>
                        <th className="text-left px-3 py-2">Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {documents.map(doc => (
                        <tr key={doc.id} className="border-t border-zinc-800">
                          <td className="px-3 py-2">{doc.fileName}</td>
                          <td className="px-3 py-2">{doc.chunkCount}</td>
                          <td className="px-3 py-2">{formatBytes(doc.sizeBytes)}</td>
                          <td className="px-3 py-2">{new Date(doc.ingestedAt).toLocaleString()}</td>
                          <td className="px-3 py-2">
                            <button
                              onClick={() => onDeleteDocument(doc.id)}
                              disabled={busy}
                              className="px-2 py-1 rounded bg-zinc-800 hover:bg-zinc-700 text-xs disabled:opacity-50"
                            >
                              Delete
                            </button>
                          </td>
                        </tr>
                      ))}
                      {documents.length === 0 && (
                        <tr>
                          <td colSpan={5} className="px-3 py-5 text-center text-zinc-500">No documents in this collection.</td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>

              <div className="rounded-lg border border-zinc-800 bg-zinc-950 p-3 space-y-3">
                <h2 className="text-sm font-semibold">Access grants</h2>
                <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
                  <select
                    value={grantKind}
                    onChange={e => setGrantKind(e.target.value as PrincipalKind)}
                    className="px-3 py-2 rounded bg-zinc-900 border border-zinc-700"
                  >
                    <option value="User">User</option>
                    <option value="Department">Department</option>
                    <option value="Role">Role</option>
                  </select>
                  <select
                    value={grantPrincipalId}
                    onChange={e => setGrantPrincipalId(e.target.value)}
                    className="px-3 py-2 rounded bg-zinc-900 border border-zinc-700 md:col-span-2"
                  >
                    {principalOptions.map(opt => (
                      <option key={opt.id} value={opt.id}>{opt.label}</option>
                    ))}
                  </select>
                  <button
                    onClick={onAddGrant}
                    disabled={busy || !grantPrincipalId}
                    className="px-3 py-2 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50 text-sm"
                  >
                    Add grant
                  </button>
                </div>

                <div className="rounded border border-zinc-800 overflow-hidden">
                  <table className="w-full text-sm">
                    <thead className="bg-zinc-800/70 text-zinc-300">
                      <tr>
                        <th className="text-left px-3 py-2">Kind</th>
                        <th className="text-left px-3 py-2">Principal</th>
                        <th className="text-left px-3 py-2">Created</th>
                        <th className="text-left px-3 py-2">Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {grants.map(grant => (
                        <tr key={grant.id} className="border-t border-zinc-800">
                          <td className="px-3 py-2">{grant.principalKind}</td>
                          <td className="px-3 py-2">{grant.principalDisplayName || grant.principalId}</td>
                          <td className="px-3 py-2">{new Date(grant.createdAt).toLocaleString()}</td>
                          <td className="px-3 py-2">
                            <button
                              onClick={() => onRemoveGrant(grant.id)}
                              disabled={busy}
                              className="px-2 py-1 rounded bg-zinc-800 hover:bg-zinc-700 text-xs disabled:opacity-50"
                            >
                              Remove
                            </button>
                          </td>
                        </tr>
                      ))}
                      {grants.length === 0 && (
                        <tr>
                          <td colSpan={4} className="px-3 py-5 text-center text-zinc-500">No grants configured.</td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </>
          )}
        </section>
      </div>
    </div>
  )
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  if (value < 1024 * 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(1)} MB`
  return `${(value / (1024 * 1024 * 1024)).toFixed(1)} GB`
}
