import { useState } from 'react'
import type { ConversationSummaryDto, UserDto } from '../api/types'

interface Props {
  conversations: ConversationSummaryDto[]
  activeConvId: string | null
  onNewChat: () => void
  onSelect: (id: string) => void
  onDelete: (id: string) => void
  user: UserDto | null
  onSignOut: () => void
}

function groupByDate(convs: ConversationSummaryDto[]) {
  const now = new Date()
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const yesterday = new Date(today.getTime() - 86_400_000)
  const week = new Date(today.getTime() - 6 * 86_400_000)

  const groups: Record<string, ConversationSummaryDto[]> = {
    Today: [],
    Yesterday: [],
    'Previous 7 days': [],
    Older: [],
  }

  for (const c of convs) {
    const d = new Date(c.updatedAt)
    if (d >= today) groups['Today'].push(c)
    else if (d >= yesterday) groups['Yesterday'].push(c)
    else if (d >= week) groups['Previous 7 days'].push(c)
    else groups['Older'].push(c)
  }

  return groups
}

export default function Sidebar({ conversations, activeConvId, onNewChat, onSelect, onDelete, user, onSignOut }: Props) {
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const groups = groupByDate(conversations)

  return (
    <aside className="flex flex-col w-64 shrink-0 bg-zinc-900 border-r border-zinc-800">
      {/* App title + new chat */}
      <div className="px-3 py-3 border-b border-zinc-800">
        <div className="flex items-center gap-2 mb-3">
          <div className="flex items-center justify-center w-7 h-7 rounded-lg bg-blue-600 shrink-0">
            <svg className="w-4 h-4 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M8.625 12a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H8.25m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0H12m4.125 0a.375.375 0 11-.75 0 .375.375 0 01.75 0zm0 0h-.375M21 12c0 4.556-4.03 8.25-9 8.25a9.764 9.764 0 01-2.555-.337A5.972 5.972 0 015.41 20.97a5.969 5.969 0 01-.474-.065 4.48 4.48 0 00.978-2.025c.09-.457-.133-.901-.467-1.226C3.93 16.178 3 14.189 3 12c0-4.556 4.03-8.25 9-8.25s9 3.694 9 8.25z" />
            </svg>
          </div>
          <span className="text-sm font-semibold text-zinc-100 truncate">MyLocalAssistant</span>
        </div>
        <button
          onClick={onNewChat}
          className="w-full flex items-center gap-2 px-3 py-2 rounded-lg text-sm text-zinc-300
                     hover:bg-zinc-800 hover:text-zinc-100 transition-colors border border-zinc-700"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
          </svg>
          New chat
        </button>
      </div>

      {/* Conversation list */}
      <div className="flex-1 overflow-y-auto py-2 px-2 space-y-4">
        {Object.entries(groups).map(([label, items]) =>
          items.length === 0 ? null : (
            <div key={label}>
              <p className="px-2 py-1 text-xs font-medium text-zinc-500 uppercase tracking-wider">{label}</p>
              <div className="space-y-0.5">
                {items.map(c => (
                  <div
                    key={c.id}
                    className="relative group"
                    onMouseEnter={() => setHoveredId(c.id)}
                    onMouseLeave={() => setHoveredId(null)}
                  >
                    <button
                      onClick={() => onSelect(c.id)}
                      className={`w-full text-left px-3 py-2 rounded-lg text-sm truncate transition-colors ${
                        activeConvId === c.id
                          ? 'bg-zinc-700 text-zinc-100'
                          : 'text-zinc-400 hover:bg-zinc-800 hover:text-zinc-200'
                      }`}
                      title={c.title}
                    >
                      {c.title}
                    </button>
                    {hoveredId === c.id && (
                      <button
                        onClick={e => { e.stopPropagation(); onDelete(c.id) }}
                        className="absolute right-1.5 top-1/2 -translate-y-1/2 p-1 rounded text-zinc-500
                                   hover:text-red-400 hover:bg-zinc-700 transition-colors"
                        title="Delete conversation"
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                            d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                        </svg>
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )
        )}

        {conversations.length === 0 && (
          <p className="text-xs text-zinc-600 text-center mt-6 px-4">
            No conversations yet. Start chatting!
          </p>
        )}
      </div>

      {/* User footer */}
      <div className="px-3 py-3 border-t border-zinc-800">
        <div className="flex items-center gap-2">
          <div className="flex items-center justify-center w-7 h-7 rounded-full bg-blue-700 shrink-0 text-xs font-semibold text-white">
            {(user?.displayName ?? user?.username ?? '?')[0].toUpperCase()}
          </div>
          <span className="flex-1 text-sm text-zinc-300 truncate">{user?.displayName ?? user?.username}</span>
          <button
            onClick={onSignOut}
            title="Sign out"
            className="p-1 rounded text-zinc-500 hover:text-zinc-300 hover:bg-zinc-800 transition-colors"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
            </svg>
          </button>
        </div>
      </div>
    </aside>
  )
}
