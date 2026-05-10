// Mirrors MyLocalAssistant.Shared.Contracts

export interface LoginResponse {
  accessToken: string
  refreshToken: string
  expiresAt: string
  user: UserDto
}

export interface UserDto {
  id: string
  username: string
  displayName: string
  departments: string[]
  roles: string[]
  mustChangePassword: boolean
  isAdmin: boolean
  isGlobalAdmin: boolean
  workRoot: string | null
}

export interface AgentDto {
  id: string
  name: string
  description: string
  category: string
  isGeneric: boolean
  enabled: boolean
  defaultModelId: string | null
  ragEnabled: boolean
  ragCollectionIds: string[]
  systemPrompt: string
  toolIds: string[] | null
  maxToolCalls: number | null
}

export interface ConversationSummaryDto {
  id: string
  agentId: string
  title: string
  createdAt: string
  updatedAt: string
  messageCount: number
}

export interface ConversationMessageDto {
  id: string
  role: string
  body: string | null
  createdAt: string
}

export interface ConversationDetailDto {
  id: string
  agentId: string
  title: string
  createdAt: string
  updatedAt: string
  messages: ConversationMessageDto[]
}

export interface AttachmentExtractResult {
  fileName: string
  charCount: number
  pageCount: number
  truncated: boolean
  text: string
}

// TokenStreamFrameKind enum values (serialized as integers by the server)
export const enum FrameKind {
  Token = 0,
  End = 1,
  Error = 2,
  Meta = 3,
  ToolCall = 4,
  ToolResult = 5,
  ToolUnavailable = 6,
  Queued = 7,
}

export interface TokenStreamFrame {
  kind: FrameKind
  text?: string
  errorMessage?: string
  conversationId?: string
  toolName?: string
  toolJson?: string
  toolReason?: string
  toolDisplay?: string
  queuePosition?: number
}

// Local UI message type (not from server)
export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  toolCalls?: { name: string; display?: string }[]
  createdAt: string
}
