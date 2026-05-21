import type {
  LoginResponse,
  HealthDto,
  UserDto,
  AgentDto,
  AgentUpdateRequest,
  ModelDto,
  ActiveModelStatusDto,
  ActiveEmbeddingStatusDto,
  StatsDto,
  ServerSettingsDto,
  UpdateServerSettingsRequest,
  GlobalSystemPromptDto,
  CloudKeysStatusDto,
  UpdateCloudKeysRequest,
  CloudKeyTestResultDto,
  UserAdminDto,
  CreateUserRequest,
  UpdateUserRequest,
  DepartmentDto,
  RoleDto,
  ToolDto,
  ToolUpdateRequest,
  ToolCallStatsSnapshot,
  RagCollectionDto,
  RagDocumentDto,
  CreateCollectionRequest,
  UpdateCollectionRequest,
  CollectionGrantDto,
  AddCollectionGrantRequest,
  AuditPageDto,
  ConversationSummaryDto,
  ConversationDetailDto,
  AttachmentExtractResult,
  TokenStreamFrame,
} from './types'

const AUTH_KEY = 'mla_auth'

let refreshInFlight: Promise<string | null> | null = null

interface StoredAuth {
  accessToken: string
  refreshToken: string
  expiresAt: string
}

function loadAuth(): StoredAuth | null {
  try { return JSON.parse(sessionStorage.getItem(AUTH_KEY) ?? 'null') } catch { return null }
}
function saveAuth(a: StoredAuth) { sessionStorage.setItem(AUTH_KEY, JSON.stringify(a)) }
function clearAuth() { sessionStorage.removeItem(AUTH_KEY) }

function redirectToLoginPreservingPath() {
  const currentPath = `${window.location.pathname}${window.location.search}`
  if (window.location.pathname === '/login') return
  const loginUrl = `/login?from=${encodeURIComponent(currentPath)}`
  window.location.replace(loginUrl)
}

async function getToken(): Promise<string | null> {
  const auth = loadAuth()
  if (!auth) return null

  // Refresh if token expires in < 60 s
  if (new Date(auth.expiresAt).getTime() - Date.now() < 60_000) {
    if (!refreshInFlight) {
      refreshInFlight = (async () => {
        try {
          // Re-read auth right before refresh so parallel callers share the freshest token pair.
          const current = loadAuth()
          if (!current) return null

          // Another caller may have refreshed while we were queued.
          if (new Date(current.expiresAt).getTime() - Date.now() >= 60_000)
            return current.accessToken

          const res = await fetch('/api/auth/refresh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ refreshToken: current.refreshToken }),
          })
          if (!res.ok) {
            clearAuth()
            clearUser()
            redirectToLoginPreservingPath()
            return null
          }

          const data: LoginResponse = await res.json()
          saveAuth({ accessToken: data.accessToken, refreshToken: data.refreshToken, expiresAt: data.expiresAt })
          return data.accessToken
        } catch {
          clearAuth()
          clearUser()
          redirectToLoginPreservingPath()
          return null
        }
      })()
    }

    try {
      return await refreshInFlight
    } finally {
      refreshInFlight = null
    }
  }

  return auth.accessToken
}

async function authHeaders(): Promise<HeadersInit> {
  const token = await getToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

async function json<T>(res: Response): Promise<T> {
  if (res.status === 204) return undefined as T
  if (!res.ok) {
    // 401 with a stored token means the session is invalid (server restart / key rotation).
    // Clear all local auth state and send the user back to login.
    if (res.status === 401 && loadAuth() !== null) {
      clearAuth()
      clearUser()
      redirectToLoginPreservingPath()
      throw new Error('Session expired')
    }
    let detail = res.statusText
    try { const body = await res.json(); detail = body.detail ?? body.title ?? detail } catch { /* ignore */ }
    throw new Error(detail)
  }
  return res.json() as Promise<T>
}

export async function getHealth(): Promise<HealthDto> {
  const res = await fetch('/healthz')
  if (!res.ok) throw new Error(`Health check failed (${res.status})`)
  return res.json() as Promise<HealthDto>
}

// ── Auth ────────────────────────────────────────────────────────────────────

export async function login(username: string, password: string): Promise<LoginResponse> {
  const res = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  const data = await json<LoginResponse>(res)
  saveAuth({ accessToken: data.accessToken, refreshToken: data.refreshToken, expiresAt: data.expiresAt })
  return data
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  const res = await fetch('/api/auth/change-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ currentPassword, newPassword }),
  })
  await json<unknown>(res)
}

export async function getProfile(): Promise<UserDto> {
  const res = await fetch('/api/auth/me', { headers: await authHeaders() })
  return json<UserDto>(res)
}

export async function updateWorkRoot(workRoot: string | null): Promise<UserDto> {
  const res = await fetch('/api/auth/me', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ workRoot }),
  })
  return json<UserDto>(res)
}

export function logout() { clearAuth() }

export function getStoredUser(): LoginResponse['user'] | null {
  try { return JSON.parse(sessionStorage.getItem('mla_user') ?? 'null') } catch { return null }
}
export function saveUser(u: LoginResponse['user']) { sessionStorage.setItem('mla_user', JSON.stringify(u)) }
export function clearUser() { sessionStorage.removeItem('mla_user') }

// ── Agents ──────────────────────────────────────────────────────────────────

export async function listAgents(): Promise<AgentDto[]> {
  const res = await fetch('/api/agents', { headers: await authHeaders() })
  return json<AgentDto[]>(res)
}

// ── Admin: Models ───────────────────────────────────────────────────────────

export async function listModels(): Promise<ModelDto[]> {
  const res = await fetch('/api/admin/models/', { headers: await authHeaders() })
  return json<ModelDto[]>(res)
}

export async function getModelStatus(): Promise<ActiveModelStatusDto> {
  const res = await fetch('/api/admin/models/status', { headers: await authHeaders() })
  return json<ActiveModelStatusDto>(res)
}

export async function getEmbeddingStatus(): Promise<ActiveEmbeddingStatusDto> {
  const res = await fetch('/api/admin/models/embedding/status', { headers: await authHeaders() })
  return json<ActiveEmbeddingStatusDto>(res)
}

export async function startDownload(modelId: string): Promise<void> {
  const res = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}/download`, {
    method: 'POST',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function cancelDownload(modelId: string): Promise<void> {
  const res = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}/download`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function activateModel(modelId: string): Promise<void> {
  const res = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}/activate`, {
    method: 'POST',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function deactivateModel(): Promise<void> {
  const res = await fetch('/api/admin/models/deactivate', {
    method: 'POST',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function activateEmbedding(modelId: string): Promise<void> {
  const res = await fetch(`/api/admin/models/embedding/${encodeURIComponent(modelId)}/activate`, {
    method: 'POST',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function deleteModel(modelId: string): Promise<void> {
  const res = await fetch(`/api/admin/models/${encodeURIComponent(modelId)}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

// ── Admin: Stats ────────────────────────────────────────────────────────────

export async function getStats(days = 30): Promise<StatsDto> {
  const res = await fetch(`/api/admin/stats?days=${days}`, { headers: await authHeaders() })
  return json<StatsDto>(res)
}

// ── Admin: Settings ─────────────────────────────────────────────────────────

export async function getServerSettings(): Promise<ServerSettingsDto> {
  const res = await fetch('/api/admin/settings/', { headers: await authHeaders() })
  return json<ServerSettingsDto>(res)
}

export async function updateServerSettings(req: UpdateServerSettingsRequest): Promise<ServerSettingsDto> {
  const res = await fetch('/api/admin/settings/', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<ServerSettingsDto>(res)
}

export async function getGlobalSystemPrompt(): Promise<GlobalSystemPromptDto> {
  const res = await fetch('/api/admin/settings/global-prompt', { headers: await authHeaders() })
  return json<GlobalSystemPromptDto>(res)
}

export async function setGlobalSystemPrompt(systemPrompt: string): Promise<GlobalSystemPromptDto> {
  const res = await fetch('/api/admin/settings/global-prompt', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ systemPrompt }),
  })
  return json<GlobalSystemPromptDto>(res)
}

export async function getCloudKeysStatus(): Promise<CloudKeysStatusDto> {
  const res = await fetch('/api/admin/settings/cloud-keys', { headers: await authHeaders() })
  return json<CloudKeysStatusDto>(res)
}

export async function setCloudKeys(req: UpdateCloudKeysRequest): Promise<CloudKeysStatusDto> {
  const res = await fetch('/api/admin/settings/cloud-keys', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<CloudKeysStatusDto>(res)
}

export async function testCloudKey(provider: string): Promise<CloudKeyTestResultDto> {
  const res = await fetch(`/api/admin/settings/cloud-keys/test/${encodeURIComponent(provider)}`, {
    method: 'POST',
    headers: await authHeaders(),
  })
  return json<CloudKeyTestResultDto>(res)
}

// ── Admin: Users / Departments / Roles ─────────────────────────────────────

export async function listUsers(): Promise<UserAdminDto[]> {
  const res = await fetch('/api/admin/users/', { headers: await authHeaders() })
  return json<UserAdminDto[]>(res)
}

export async function createUser(req: CreateUserRequest): Promise<UserAdminDto> {
  const res = await fetch('/api/admin/users/', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<UserAdminDto>(res)
}

export async function updateUser(id: string, req: UpdateUserRequest): Promise<UserAdminDto> {
  const res = await fetch(`/api/admin/users/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<UserAdminDto>(res)
}

export async function resetUserPassword(id: string, newPassword: string): Promise<void> {
  const res = await fetch(`/api/admin/users/${encodeURIComponent(id)}/reset-password`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify({ newPassword }),
  })
  await json<unknown>(res)
}

export async function deleteUser(id: string): Promise<void> {
  const res = await fetch(`/api/admin/users/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function listDepartments(): Promise<DepartmentDto[]> {
  const res = await fetch('/api/admin/departments/', { headers: await authHeaders() })
  return json<DepartmentDto[]>(res)
}

export async function listRoles(): Promise<RoleDto[]> {
  const res = await fetch('/api/admin/roles/', { headers: await authHeaders() })
  return json<RoleDto[]>(res)
}

// ── Admin: Agents ───────────────────────────────────────────────────────────

export async function listAdminAgents(): Promise<AgentDto[]> {
  const res = await fetch('/api/admin/agents/', { headers: await authHeaders() })
  return json<AgentDto[]>(res)
}

export async function updateAgent(id: string, req: AgentUpdateRequest): Promise<AgentDto> {
  const res = await fetch(`/api/admin/agents/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<AgentDto>(res)
}

// ── Admin: Tools ────────────────────────────────────────────────────────────

export async function listTools(): Promise<ToolDto[]> {
  const res = await fetch('/api/admin/tools/', { headers: await authHeaders() })
  return json<ToolDto[]>(res)
}

export async function updateTool(id: string, req: ToolUpdateRequest): Promise<ToolDto> {
  const res = await fetch(`/api/admin/tools/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<ToolDto>(res)
}

export async function getToolStats(): Promise<ToolCallStatsSnapshot> {
  const res = await fetch('/api/admin/tools/stats', { headers: await authHeaders() })
  return json<ToolCallStatsSnapshot>(res)
}

export async function resetToolStats(): Promise<void> {
  const res = await fetch('/api/admin/tools/stats/reset', {
    method: 'POST',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function reloadTools(): Promise<number> {
  const res = await fetch('/api/admin/tools/reload', {
    method: 'POST',
    headers: await authHeaders(),
  })
  const body = await json<{ count?: number }>(res)
  return typeof body?.count === 'number' ? body.count : 0
}

// ── Admin: RAG ──────────────────────────────────────────────────────────────

export async function listCollections(): Promise<RagCollectionDto[]> {
  const res = await fetch('/api/admin/rag/collections', { headers: await authHeaders() })
  return json<RagCollectionDto[]>(res)
}

export async function createCollection(req: CreateCollectionRequest): Promise<RagCollectionDto> {
  const res = await fetch('/api/admin/rag/collections', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<RagCollectionDto>(res)
}

export async function updateCollection(id: string, req: UpdateCollectionRequest): Promise<RagCollectionDto> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<RagCollectionDto>(res)
}

export async function deleteCollection(id: string): Promise<void> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function listCollectionDocuments(collectionId: string): Promise<RagDocumentDto[]> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/documents`, {
    headers: await authHeaders(),
  })
  return json<RagDocumentDto[]>(res)
}

export async function uploadCollectionDocument(collectionId: string, file: File): Promise<RagDocumentDto> {
  const form = new FormData()
  form.append('file', file)
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/documents`, {
    method: 'POST',
    headers: await authHeaders(),
    body: form,
  })
  return json<RagDocumentDto>(res)
}

export async function deleteCollectionDocument(collectionId: string, documentId: string): Promise<void> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/documents/${encodeURIComponent(documentId)}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

export async function listCollectionGrants(collectionId: string): Promise<CollectionGrantDto[]> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/grants`, {
    headers: await authHeaders(),
  })
  return json<CollectionGrantDto[]>(res)
}

export async function addCollectionGrant(collectionId: string, req: AddCollectionGrantRequest): Promise<CollectionGrantDto> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/grants`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: JSON.stringify(req),
  })
  return json<CollectionGrantDto>(res)
}

export async function removeCollectionGrant(collectionId: string, grantId: number): Promise<void> {
  const res = await fetch(`/api/admin/rag/collections/${encodeURIComponent(collectionId)}/grants/${grantId}`, {
    method: 'DELETE',
    headers: await authHeaders(),
  })
  await json<unknown>(res)
}

// ── Admin: Audit ────────────────────────────────────────────────────────────

function buildAuditQuery(params: {
  from?: string
  to?: string
  action?: string
  user?: string
  success?: boolean
  isAdminAction?: boolean
  skip?: number
  take?: number
}): string {
  const q = new URLSearchParams()
  if (params.from) q.set('from', params.from)
  if (params.to) q.set('to', params.to)
  if (params.action) q.set('action', params.action)
  if (params.user) q.set('user', params.user)
  if (typeof params.success === 'boolean') q.set('success', params.success ? 'true' : 'false')
  if (typeof params.isAdminAction === 'boolean') q.set('isAdminAction', params.isAdminAction ? 'true' : 'false')
  if (typeof params.skip === 'number') q.set('skip', String(params.skip))
  if (typeof params.take === 'number') q.set('take', String(params.take))
  const text = q.toString()
  return text.length > 0 ? `?${text}` : ''
}

export async function listAudit(params: {
  from?: string
  to?: string
  action?: string
  user?: string
  success?: boolean
  isAdminAction?: boolean
  skip?: number
  take?: number
}): Promise<AuditPageDto> {
  const res = await fetch(`/api/admin/audit/${buildAuditQuery(params)}`, { headers: await authHeaders() })
  return json<AuditPageDto>(res)
}

export async function listAuditActions(): Promise<string[]> {
  const res = await fetch('/api/admin/audit/actions', { headers: await authHeaders() })
  return json<string[]>(res)
}

export async function downloadAuditCsv(params: {
  from?: string
  to?: string
  action?: string
  user?: string
  success?: boolean
  isAdminAction?: boolean
}): Promise<Blob> {
  const res = await fetch(`/api/admin/audit/export.csv${buildAuditQuery(params)}`, { headers: await authHeaders() })
  if (!res.ok) {
    await json<unknown>(res)
    throw new Error('CSV export failed')
  }
  return res.blob()
}

// ── Conversations ────────────────────────────────────────────────────────────

export async function listConversations(agentId?: string): Promise<ConversationSummaryDto[]> {
  const q = agentId ? `?agentId=${encodeURIComponent(agentId)}` : ''
  const res = await fetch(`/api/chat/conversations/${q}`, { headers: await authHeaders() })
  return json<ConversationSummaryDto[]>(res)
}

export async function searchConversations(query: string, semantic = false): Promise<ConversationSummaryDto[]> {
  const res = await fetch(
    `/api/chat/conversations/search?q=${encodeURIComponent(query)}&semantic=${semantic}`,
    { headers: await authHeaders() },
  )
  return json<ConversationSummaryDto[]>(res)
}

export async function getConversation(id: string): Promise<ConversationDetailDto> {
  const res = await fetch(`/api/chat/conversations/${id}`, { headers: await authHeaders() })
  return json<ConversationDetailDto>(res)
}

export async function deleteConversation(id: string): Promise<void> {
  await fetch(`/api/chat/conversations/${id}`, { method: 'DELETE', headers: await authHeaders() })
}

// ── Attachments ──────────────────────────────────────────────────────────────

export async function extractAttachment(file: File): Promise<AttachmentExtractResult> {
  const form = new FormData()
  form.append('file', file)
  const res = await fetch('/api/chat/attachments/extract', {
    method: 'POST',
    headers: await authHeaders(),
    body: form,
  })
  return json<AttachmentExtractResult>(res)
}

// ── Chat streaming ───────────────────────────────────────────────────────────

export async function* streamChat(
  agentId: string,
  message: string,
  conversationId: string | null,
  signal: AbortSignal,
): AsyncGenerator<TokenStreamFrame> {
  const token = await getToken()
  const res = await fetch('/api/chat/stream', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify({ agentId, message, conversationId }),
    signal,
  })

  if (!res.ok) {
    let detail = res.statusText
    try { const body = await res.json(); detail = body.detail ?? detail } catch { /* ignore */ }
    throw new Error(detail)
  }

  const reader = res.body!.getReader()
  const decoder = new TextDecoder()
  let buf = ''

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buf += decoder.decode(value, { stream: true })
      const lines = buf.split('\n')
      buf = lines.pop() ?? ''
      for (const line of lines) {
        if (!line.startsWith('data: ')) continue
        const payload = line.slice(6).trim()
        if (!payload) continue
        yield JSON.parse(payload) as TokenStreamFrame
      }
    }
  } finally {
    reader.releaseLock()
  }
}
