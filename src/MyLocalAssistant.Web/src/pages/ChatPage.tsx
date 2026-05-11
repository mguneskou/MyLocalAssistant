import { useCallback, useEffect, useRef, useState } from 'react'
import type { AgentDto, ChatMessage, ConversationSummaryDto } from '../api/types'
import { FrameKind } from '../api/types'
import * as api from '../api/client'
import Sidebar from '../components/Sidebar'
import MessageList from '../components/MessageList'
import ChatInput from '../components/ChatInput'
import { useAuth } from '../contexts/AuthContext'

export default function ChatPage() {
  const { user, signOut } = useAuth()

  const [agents, setAgents] = useState<AgentDto[]>([])
  const [selectedAgent, setSelectedAgent] = useState<AgentDto | null>(null)

  const [conversations, setConversations] = useState<ConversationSummaryDto[]>([])
  const [activeConvId, setActiveConvId] = useState<string | null>(null)

  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [streamingText, setStreamingText] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const [queuePos, setQueuePos] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const abortRef = useRef<AbortController | null>(null)

  // Load agents on mount
  useEffect(() => {
    console.log('[MLA] ChatPage mounted — loading agents...')
    api.listAgents().then(a => {
      console.log('[MLA] Agents loaded:', a.length, a.map(x => x.name))
      setAgents(a)
      if (a.length > 0) setSelectedAgent(a[0])
    }).catch(err => { console.error('[MLA] Failed to load agents:', err) })
  }, [])

  // Load conversations when agent changes
  const loadConversations = useCallback(async (agentId?: string) => {
    try {
      const list = await api.listConversations(agentId)
      setConversations(list)
    } catch { /* ignore */ }
  }, [])

  useEffect(() => {
    loadConversations(selectedAgent?.id)
  }, [selectedAgent, loadConversations])

  // Load conversation messages when active changes
  useEffect(() => {
    if (!activeConvId) { setMessages([]); return }
    api.getConversation(activeConvId).then(d => {
      setMessages(d.messages
        .filter(m => m.body)
        .map(m => ({
          id: m.id,
          role: m.role.toLowerCase() as 'user' | 'assistant',
          content: m.body!,
          createdAt: m.createdAt,
        })))
    }).catch(() => {})
  }, [activeConvId])

  function newChat() {
    abortRef.current?.abort()
    setActiveConvId(null)
    setMessages([])
    setStreamingText('')
    setError(null)
  }

  function selectConversation(id: string) {
    if (isStreaming) return
    setActiveConvId(id)
    setError(null)
  }

  async function deleteConversation(id: string) {
    await api.deleteConversation(id)
    if (activeConvId === id) newChat()
    setConversations(prev => prev.filter(c => c.id !== id))
  }

  async function sendMessage(text: string, attachment: { name: string; text: string } | null) {
    if (!selectedAgent || isStreaming) return
    setError(null)

    // Build final message text
    const fullText = attachment
      ? `${text}\n\n---\n**Attached: ${attachment.name}**\n\`\`\`\n${attachment.text}\n\`\`\``
      : text

    const userMsg: ChatMessage = {
      id: crypto.randomUUID(),
      role: 'user',
      content: fullText,
      createdAt: new Date().toISOString(),
    }
    setMessages(prev => [...prev, userMsg])
    setIsStreaming(true)
    setStreamingText('')
    setQueuePos(null)

    const ac = new AbortController()
    abortRef.current = ac

    let assistantText = ''
    const toolCalls: { name: string; display?: string }[] = []

    try {
      for await (const frame of api.streamChat(selectedAgent.id, fullText, activeConvId, ac.signal)) {
        switch (frame.kind) {
          case FrameKind.Meta:
            if (frame.conversationId) {
              setActiveConvId(frame.conversationId)
            }
            break
          case FrameKind.Queued:
            setQueuePos(frame.queuePosition ?? null)
            break
          case FrameKind.Token:
            setQueuePos(null)
            assistantText += frame.text ?? ''
            setStreamingText(assistantText)
            break
          case FrameKind.ToolCall:
            toolCalls.push({ name: frame.toolName ?? '', display: frame.toolDisplay ?? undefined })
            break
          case FrameKind.End: {
            const assistantMsg: ChatMessage = {
              id: crypto.randomUUID(),
              role: 'assistant',
              content: assistantText,
              toolCalls: toolCalls.length > 0 ? [...toolCalls] : undefined,
              createdAt: new Date().toISOString(),
            }
            setMessages(prev => [...prev, assistantMsg])
            setStreamingText('')
            loadConversations(selectedAgent.id)
            break
          }
          case FrameKind.Error:
            setError(frame.errorMessage ?? 'An error occurred.')
            break
        }
      }
    } catch (err) {
      if ((err as Error).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : 'Stream failed.')
      }
    } finally {
      setIsStreaming(false)
      setQueuePos(null)
    }
  }

  function stopStreaming() {
    abortRef.current?.abort()
  }

  return (
    <div className="flex h-full bg-zinc-950">
      {/* Sidebar */}
      <Sidebar
        conversations={conversations}
        activeConvId={activeConvId}
        onNewChat={newChat}
        onSelect={selectConversation}
        onDelete={deleteConversation}
        user={user}
        onSignOut={signOut}
      />

      {/* Main area */}
      <div className="flex flex-col flex-1 min-w-0">
        {/* Top bar */}
        <header className="flex items-center gap-3 px-4 py-3 border-b border-zinc-800 bg-zinc-900 shrink-0">
          <div className="flex-1 min-w-0">
            {agents.length > 0 && (
              <select
                value={selectedAgent?.id ?? ''}
                onChange={e => {
                  const a = agents.find(x => x.id === e.target.value) ?? null
                  setSelectedAgent(a)
                  newChat()
                }}
                className="bg-zinc-800 border border-zinc-700 text-zinc-100 text-sm rounded-lg
                           px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-blue-500 max-w-xs"
              >
                {agents.map(a => (
                  <option key={a.id} value={a.id}>{a.name}</option>
                ))}
              </select>
            )}
          </div>
          {selectedAgent && (
            <span className="text-xs text-zinc-500 hidden sm:block truncate max-w-xs" title={selectedAgent.description}>
              {selectedAgent.description}
            </span>
          )}
        </header>

        {/* Messages */}
        <MessageList
          messages={messages}
          streamingText={streamingText}
          isStreaming={isStreaming}
          queuePos={queuePos}
          error={error}
          agentName={selectedAgent?.name}
        />

        {/* Input */}
        <ChatInput
          onSend={sendMessage}
          onStop={stopStreaming}
          isStreaming={isStreaming}
          disabled={!selectedAgent}
        />
      </div>
    </div>
  )
}
