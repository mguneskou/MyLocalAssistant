import { useRef, useState, type KeyboardEvent, type ChangeEvent } from 'react'
import * as api from '../api/client'

interface Props {
  onSend: (text: string, attachment: { name: string; text: string } | null) => void
  onStop: () => void
  isStreaming: boolean
  disabled: boolean
}

export default function ChatInput({ onSend, onStop, isStreaming, disabled }: Props) {
  const [text, setText] = useState('')
  const [attachment, setAttachment] = useState<{ name: string; text: string } | null>(null)
  const [uploading, setUploading] = useState(false)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  function handleKey(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      submit()
    }
  }

  function submit() {
    const trimmed = text.trim()
    if (!trimmed && !attachment) return
    if (isStreaming || disabled) return
    onSend(trimmed, attachment)
    setText('')
    setAttachment(null)
    textareaRef.current?.focus()
  }

  async function handleFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setUploading(true)
    try {
      const result = await api.extractAttachment(file)
      setAttachment({ name: result.fileName, text: result.text })
    } catch (err) {
      alert(err instanceof Error ? err.message : 'File extraction failed.')
    } finally {
      setUploading(false)
    }
  }

  return (
    <div className="px-4 pb-4 pt-2 bg-zinc-950 border-t border-zinc-800 shrink-0">
      {/* Attachment badge */}
      {attachment && (
        <div className="flex items-center gap-2 mb-2 px-1">
          <div className="flex items-center gap-1.5 bg-zinc-800 border border-zinc-700 rounded-lg px-2.5 py-1.5 text-xs text-zinc-300">
            <svg className="w-3.5 h-3.5 text-zinc-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13" />
            </svg>
            {attachment.name}
            <button
              onClick={() => setAttachment(null)}
              className="ml-1 text-zinc-500 hover:text-zinc-300 transition-colors"
            >
              ×
            </button>
          </div>
        </div>
      )}

      <div className="flex items-end gap-2 bg-zinc-800 border border-zinc-700 rounded-2xl px-3 py-2
                      focus-within:ring-2 focus-within:ring-blue-500 focus-within:border-transparent">
        {/* File attach */}
        <button
          type="button"
          onClick={() => fileRef.current?.click()}
          disabled={uploading || isStreaming}
          title="Attach file"
          className="shrink-0 p-1.5 rounded-lg text-zinc-500 hover:text-zinc-300 hover:bg-zinc-700
                     disabled:opacity-40 disabled:cursor-not-allowed transition-colors mb-0.5"
        >
          {uploading ? (
            <svg className="w-5 h-5 animate-spin" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"/>
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z"/>
            </svg>
          ) : (
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.414-6.586a4 4 0 00-5.656-5.656l-6.415 6.585a6 6 0 108.486 8.486L20.5 13" />
            </svg>
          )}
        </button>

        <input ref={fileRef} type="file" className="hidden" onChange={handleFile} />

        {/* Textarea */}
        <textarea
          ref={textareaRef}
          rows={1}
          value={text}
          onChange={e => setText(e.target.value)}
          onKeyDown={handleKey}
          disabled={disabled}
          placeholder={disabled ? 'Select an agent to start chatting…' : 'Message… (Shift+Enter for new line)'}
          className="flex-1 bg-transparent text-zinc-100 placeholder-zinc-500 text-sm resize-none
                     focus:outline-none py-1.5 max-h-48 overflow-y-auto leading-relaxed"
          style={{ scrollbarWidth: 'thin' }}
        />

        {/* Send / Stop button */}
        {isStreaming ? (
          <button
            type="button"
            onClick={onStop}
            title="Stop"
            className="shrink-0 p-1.5 rounded-lg bg-zinc-600 hover:bg-zinc-500 text-white transition-colors mb-0.5"
          >
            <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 24 24">
              <rect x="6" y="6" width="12" height="12" rx="1" />
            </svg>
          </button>
        ) : (
          <button
            type="button"
            onClick={submit}
            disabled={(!text.trim() && !attachment) || disabled}
            title="Send (Enter)"
            className="shrink-0 p-1.5 rounded-lg bg-blue-600 hover:bg-blue-500
                       disabled:opacity-40 disabled:cursor-not-allowed text-white transition-colors mb-0.5"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 19V5m0 0l-7 7m7-7l7 7" />
            </svg>
          </button>
        )}
      </div>

      <p className="text-center text-xs text-zinc-600 mt-2">
        AI can make mistakes. Check important information.
      </p>
    </div>
  )
}
