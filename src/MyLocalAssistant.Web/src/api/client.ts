import type {
  LoginResponse,
  AgentDto,
  ConversationSummaryDto,
  ConversationDetailDto,
  AttachmentExtractResult,
  TokenStreamFrame,
} from './types'

const AUTH_KEY = 'mla_auth'

interface StoredAuth {
  accessToken: string
  refreshToken: string
  expiresAt: string
}

function loadAuth(): StoredAuth | null {
  try { return JSON.parse(localStorage.getItem(AUTH_KEY) ?? 'null') } catch { return null }
}
function saveAuth(a: StoredAuth) { localStorage.setItem(AUTH_KEY, JSON.stringify(a)) }
function clearAuth() { localStorage.removeItem(AUTH_KEY) }

async function getToken(): Promise<string | null> {
  const auth = loadAuth()
  if (!auth) return null

  // Refresh if token expires in < 60 s
  if (new Date(auth.expiresAt).getTime() - Date.now() < 60_000) {
    try {
      const res = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: auth.refreshToken }),
      })
      if (!res.ok) { clearAuth(); return null }
      const data: LoginResponse = await res.json()
      saveAuth({ accessToken: data.accessToken, refreshToken: data.refreshToken, expiresAt: data.expiresAt })
      return data.accessToken
    } catch { clearAuth(); return null }
  }
  return auth.accessToken
}

async function authHeaders(): Promise<HeadersInit> {
  const token = await getToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let detail = res.statusText
    try { const body = await res.json(); detail = body.detail ?? body.title ?? detail } catch { /* ignore */ }
    throw new Error(detail)
  }
  return res.json() as Promise<T>
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

export function logout() { clearAuth() }

export function getStoredUser(): LoginResponse['user'] | null {
  // User is stored as part of the login response in AuthContext; this is the fallback
  try { return JSON.parse(localStorage.getItem('mla_user') ?? 'null') } catch { return null }
}
export function saveUser(u: LoginResponse['user']) { localStorage.setItem('mla_user', JSON.stringify(u)) }
export function clearUser() { localStorage.removeItem('mla_user') }

// ── Agents ──────────────────────────────────────────────────────────────────

export async function listAgents(): Promise<AgentDto[]> {
  const res = await fetch('/api/agents', { headers: await authHeaders() })
  return json<AgentDto[]>(res)
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
