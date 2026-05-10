import { useEffect, useRef } from 'react'
import type { ChatMessage } from '../api/types'
import MessageBubble from './MessageBubble'

interface Props {
  messages: ChatMessage[]
  streamingText: string
  isStreaming: boolean
  queuePos: number | null
  error: string | null
  agentName?: string
}

export default function MessageList({ messages, streamingText, isStreaming, queuePos, error, agentName }: Props) {
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, streamingText])

  if (messages.length === 0 && !isStreaming) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center text-center px-6">
        <div className="w-16 h-16 rounded-2xl bg-blue-600/20 border border-blue-500/30 flex items-center justify-center mb-4">
          <svg className="w-8 h-8 text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5}
              d="M8.625 12a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H8.25m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H12m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0h-.375M21 12c0 4.556-4.03 8.25-9 8.25a9.764 9.764 0 01-2.555-.337A5.972 5.972 0 015.41 20.97a5.969 5.969 0 01-.474-.065 4.48 4.48 0 00.978-2.025c.09-.457-.133-.901-.467-1.226C3.93 16.178 3 14.189 3 12c0-4.556 4.03-8.25 9-8.25s9 3.694 9 8.25z" />
          </svg>
        </div>
        <h2 className="text-lg font-semibold text-zinc-200 mb-1">
          {agentName ? `Chat with ${agentName}` : 'Start a conversation'}
        </h2>
        <p className="text-sm text-zinc-500 max-w-sm">
          Type a message below to begin. You can attach files using the paperclip icon.
        </p>
      </div>
    )
  }

  return (
    <div className="flex-1 overflow-y-auto px-4 py-6 space-y-6">
      {messages.map(m => (
        <MessageBubble key={m.id} message={m} />
      ))}

      {/* Streaming / queue state */}
      {isStreaming && (
        <div>
          {queuePos !== null ? (
            <div className="flex gap-3">
              <div className="shrink-0 w-8 h-8 rounded-full bg-zinc-700 flex items-center justify-center text-xs font-semibold text-zinc-300">
                AI
              </div>
              <div className="bg-zinc-800 rounded-2xl rounded-tl-sm px-4 py-3 text-sm text-zinc-400 flex items-center gap-2">
                <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"/>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z"/>
                </svg>
                Queued — position {queuePos}
              </div>
            </div>
          ) : streamingText ? (
            <MessageBubble
              message={{ id: '__streaming__', role: 'assistant', content: streamingText, createdAt: '' }}
              isStreaming
            />
          ) : (
            <div className="flex gap-3">
              <div className="shrink-0 w-8 h-8 rounded-full bg-zinc-700 flex items-center justify-center text-xs font-semibold text-zinc-300">
                AI
              </div>
              <div className="bg-zinc-800 rounded-2xl rounded-tl-sm px-4 py-3">
                <span className="flex gap-1">
                  <span className="w-2 h-2 bg-zinc-500 rounded-full animate-bounce [animation-delay:0ms]" />
                  <span className="w-2 h-2 bg-zinc-500 rounded-full animate-bounce [animation-delay:150ms]" />
                  <span className="w-2 h-2 bg-zinc-500 rounded-full animate-bounce [animation-delay:300ms]" />
                </span>
              </div>
            </div>
          )}
        </div>
      )}

      {error && (
        <div className="flex justify-center">
          <div className="bg-red-950 border border-red-800 rounded-lg px-4 py-2.5 text-sm text-red-300 max-w-lg">
            {error}
          </div>
        </div>
      )}

      <div ref={bottomRef} />
    </div>
  )
}
