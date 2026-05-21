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

export interface HealthDto {
  status: string
  version: string
  time: string
}

export interface DownloadStatusDto {
  stage: string
  bytes: number
  totalBytes: number
  bytesPerSecond: number
  etaSeconds: number
  error?: string | null
}

export interface ModelDto {
  id: string
  displayName: string
  tier: string
  quantization: string
  totalBytes: number
  recommendedContextSize: number
  minRamGb: number
  description: string
  license: string
  licenseUrl: string
  isInstalled: boolean
  sizeOnDisk?: number | null
  isActive: boolean
  isActiveEmbedding: boolean
  download?: DownloadStatusDto | null
  source: string
  isCloud: boolean
  isCloudConfigured: boolean
  isActiveFailed: boolean
}

export interface ActiveModelStatusDto {
  activeModelId?: string | null
  status: string
  lastError?: string | null
  backend: string
}

export interface ActiveEmbeddingStatusDto {
  activeModelId?: string | null
  status: string
  lastError?: string | null
  embeddingDimension: number
}

export interface AgentStatDto {
  agentId: string
  count: number
  errors: number
}

export interface DayStat {
  day: string
  count: number
}

export interface StatsDto {
  totalChats: number
  activeUsers: number
  errorRate: number
  byAgent: AgentStatDto[]
  dailyChats: DayStat[]
}

export interface ServerSettingsDto {
  listenUrl: string
  jwtIssuer: string
  jwtAudience: string
  accessTokenMinutes: number
  refreshTokenDays: number
  messageBodyRetentionDays: number
  auditRetentionDays: number
  defaultModelId?: string | null
  embeddingModelId?: string | null
}

export interface UpdateServerSettingsRequest {
  accessTokenMinutes: number
  refreshTokenDays: number
  messageBodyRetentionDays: number
  auditRetentionDays: number
}

export interface GlobalSystemPromptDto {
  systemPrompt: string
}

export interface CloudKeysStatusDto {
  openAiConfigured: boolean
  anthropicConfigured: boolean
  openAiBaseUrl?: string | null
  groqConfigured: boolean
  geminiConfigured: boolean
  mistralConfigured: boolean
  cerebrasConfigured: boolean
}

export interface UpdateCloudKeysRequest {
  openAiApiKey?: string | null
  anthropicApiKey?: string | null
  openAiBaseUrl?: string | null
  groqApiKey?: string | null
  geminiApiKey?: string | null
  mistralApiKey?: string | null
  cerebrasApiKey?: string | null
}

export interface CloudKeyTestResultDto {
  ok: boolean
  detail?: string | null
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
  scenarioNotes?: string | null
}

export interface AgentUpdateRequest {
  enabled: boolean
  defaultModelId?: string | null
  ragEnabled: boolean
  ragCollectionIds?: string[]
  systemPrompt?: string | null
  description?: string | null
  toolIds?: string[]
  maxToolCalls?: number | null
  scenarioNotes?: string | null
}

export interface UserAdminDto {
  id: string
  username: string
  displayName: string
  departments: string[]
  isAdmin: boolean
  isDisabled: boolean
  mustChangePassword: boolean
  createdAt: string
  lastLoginAt?: string | null
  workRoot?: string | null
}

export interface CreateUserRequest {
  username: string
  displayName: string
  password: string
  departments?: string[]
  isAdmin: boolean
  workRoot?: string | null
}

export interface UpdateUserRequest {
  displayName?: string | null
  departments?: string[]
  isAdmin?: boolean
  isDisabled?: boolean
  workRoot?: string | null
  mustChangePassword?: boolean
}

export interface ResetPasswordRequest {
  newPassword: string
}

export interface DepartmentDto {
  id: string
  name: string
  userCount: number
}

export interface RoleDto {
  id: string
  name: string
}

export interface ToolFunctionDto {
  name: string
  description: string
  argumentsSchemaJson: string
}

export interface ToolRequirementsDto {
  tools: string
  minContextK: number
}

export interface ToolDto {
  id: string
  name: string
  description: string
  category: string
  source: string
  version?: string | null
  publisher?: string | null
  keyId?: string | null
  enabled: boolean
  configJson?: string | null
  requires: ToolRequirementsDto
  tools: ToolFunctionDto[]
}

export interface ToolUpdateRequest {
  enabled: boolean
  configJson?: string | null
}

export interface ToolCallStatRow {
  toolId: string
  toolName: string
  successes: number
  errors: number
  avgMs: number
  maxMs: number
}

export interface ToolCallStatsSnapshot {
  sinceUtc: string
  rows: ToolCallStatRow[]
}

export interface RagCollectionDto {
  id: string
  name: string
  description?: string | null
  documentCount: number
  createdAt: string
  accessMode: string
  grantCount: number
}

export interface RagDocumentDto {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  chunkCount: number
  ingestedAt: string
  sha256?: string | null
}

export interface CreateCollectionRequest {
  name: string
  description?: string | null
  accessMode?: string | null
}

export interface UpdateCollectionRequest {
  description?: string | null
  accessMode: string
}

export interface CollectionGrantDto {
  id: number
  principalKind: string
  principalId: string
  principalDisplayName?: string | null
  createdAt: string
}

export interface AddCollectionGrantRequest {
  principalKind: string
  principalId: string
}

export interface AuditEntryDto {
  id: number
  timestamp: string
  userId?: string | null
  username?: string | null
  action: string
  agentId?: string | null
  detail?: string | null
  ipAddress?: string | null
  success: boolean
  isAdminAction: boolean
}

export interface AuditPageDto {
  items: AuditEntryDto[]
  total: number
  skip: number
  take: number
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
