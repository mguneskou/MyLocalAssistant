import { useRef, useState } from 'react'
import * as api from '../../api/client'
import { FrameKind, type AgentDto } from '../../api/types'

interface PromptRow {
  id: string
  role: 'user' | 'assistant' | 'error'
  text: string
}

interface Props {
  agent: AgentDto
  onClose: () => void
}

function makeId(): string {
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export default function AdminAgentPromptTestModal({ agent, onClose }: Props) {
  const [input, setInput] = useState('')
  const [rows, setRows] = useState<PromptRow[]>([])
  const [streamingText, setStreamingText] = useState('')
  const [conversationId, setConversationId] = useState<string | null>(null)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const silentAbortRef = useRef(false)

  async function runPrompt() {
    const prompt = input.trim()
    if (!prompt || running) return

    setInput('')
    setError(null)
    setRows(prev => [...prev, { id: makeId(), role: 'user', text: prompt }])

    const abort = new AbortController()
    abortRef.current = abort
    setRunning(true)
    setStreamingText('')

    let assistant = ''
    try {
      for await (const frame of api.streamChat(agent.id, prompt, conversationId, abort.signal)) {
        if (frame.kind === FrameKind.Meta && frame.conversationId) {
          setConversationId(frame.conversationId)
          continue
        }
        if (frame.kind === FrameKind.Token) {
          assistant += frame.text ?? ''
          setStreamingText(assistant)
          continue
        }
        if (frame.kind === FrameKind.Error) {
          throw new Error(frame.errorMessage ?? 'Agent returned an error.')
        }
      }

      const finalText = assistant.trim().length > 0 ? assistant : '(No response text)'
      setRows(prev => [...prev, { id: makeId(), role: 'assistant', text: finalText }])
      setStreamingText('')
    } catch (e) {
      const suppressMessage = abort.signal.aborted && silentAbortRef.current
      const message = abort.signal.aborted
        ? 'Prompt run stopped.'
        : (e instanceof Error ? e.message : 'Prompt run failed.')
      if (!suppressMessage) {
        setError(message)
        setRows(prev => [...prev, { id: makeId(), role: 'error', text: message }])
      }
      setStreamingText('')
    } finally {
      setRunning(false)
      abortRef.current = null
      silentAbortRef.current = false
    }
  }

  function stopRun(silent = false) {
    silentAbortRef.current = silent
    abortRef.current?.abort()
  }

  function clearSession() {
    stopRun(true)
    setRows([])
    setStreamingText('')
    setConversationId(null)
    setError(null)
  }

  function close() {
    stopRun(true)
    onClose()
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/70 backdrop-blur-sm flex items-center justify-center p-4">
      <div className="w-full max-w-4xl max-h-[90vh] rounded-xl border border-zinc-700 bg-zinc-900 flex flex-col overflow-hidden">
        <div className="px-4 py-3 border-b border-zinc-800 flex items-start justify-between gap-4">
          <div>
            <h2 className="text-base font-semibold">Prompt test</h2>
            <p className="text-xs text-zinc-400 mt-1">Agent: {agent.name}</p>
            <p className="text-xs text-zinc-500 mt-1">Conversation: {conversationId ?? '(new)'}</p>
          </div>
          <button
            type="button"
            onClick={close}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700"
          >
            Close
          </button>
        </div>

        <div className="flex-1 overflow-auto p-4 space-y-3 bg-zinc-950/40">
          {rows.map(row => (
            <article
              key={row.id}
              className={`rounded-lg px-3 py-2 text-sm whitespace-pre-wrap ${
                row.role === 'user'
                  ? 'bg-blue-950/35 border border-blue-900 text-blue-100'
                  : row.role === 'assistant'
                    ? 'bg-zinc-900 border border-zinc-800 text-zinc-100'
                    : 'bg-red-950/30 border border-red-900 text-red-300'
              }`}
            >
              <div className="text-xs uppercase tracking-wide opacity-70 mb-1">{row.role}</div>
              {row.text}
            </article>
          ))}

          {streamingText && (
            <article className="rounded-lg px-3 py-2 text-sm whitespace-pre-wrap bg-zinc-900 border border-zinc-800 text-zinc-100">
              <div className="text-xs uppercase tracking-wide opacity-70 mb-1">assistant (streaming)</div>
              {streamingText}
            </article>
          )}

          {rows.length === 0 && !streamingText && (
            <div className="text-sm text-zinc-500">Run test prompts to validate behavior, tool use, and system prompt output.</div>
          )}
        </div>

        <div className="border-t border-zinc-800 p-4 space-y-3">
          {error && (
            <div className="rounded-lg border border-red-800 bg-red-950/30 px-3 py-2 text-sm text-red-300">{error}</div>
          )}

          <textarea
            value={input}
            onChange={e => setInput(e.target.value)}
            onKeyDown={e => {
              if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                e.preventDefault()
                void runPrompt()
              }
            }}
            rows={4}
            disabled={running}
            placeholder="Type a prompt to test this agent"
            className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 text-sm"
          />

          <div className="flex items-center justify-between gap-2">
            <div className="text-xs text-zinc-500">Press Ctrl+Enter in this box to run quickly.</div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={clearSession}
                className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
              >
                Clear
              </button>
              {running ? (
                <button
                  type="button"
                  onClick={() => stopRun(false)}
                  className="px-3 py-2 rounded-lg text-sm bg-red-700 hover:bg-red-600"
                >
                  Stop
                </button>
              ) : (
                <button
                  type="button"
                  onClick={runPrompt}
                  disabled={!input.trim()}
                  className="px-3 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500 disabled:opacity-50"
                >
                  Run prompt
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
