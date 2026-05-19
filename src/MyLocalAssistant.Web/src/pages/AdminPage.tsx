import { useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'
import AdminOverviewTab from '../components/admin/AdminOverviewTab'
import AdminModelsTab from '../components/admin/AdminModelsTab'
import AdminSettingsTab from '../components/admin/AdminSettingsTab'
import AdminUsersTab from '../components/admin/AdminUsersTab'
import AdminDepartmentsTab from '../components/admin/AdminDepartmentsTab'
import AdminAgentsTab from '../components/admin/AdminAgentsTab'
import AdminToolsTab from '../components/admin/AdminToolsTab'
import AdminRagTab from '../components/admin/AdminRagTab'
import AdminAuditTab from '../components/admin/AdminAuditTab'

type AdminTab =
  | 'overview'
  | 'models'
  | 'settings'
  | 'users'
  | 'departments'
  | 'agents'
  | 'tools'
  | 'rag'
  | 'audit'

const TAB_ORDER: AdminTab[] = [
  'overview',
  'models',
  'settings',
  'users',
  'departments',
  'agents',
  'tools',
  'rag',
  'audit',
]

function normalizeTab(value: string | null): AdminTab {
  if (
    value === 'models' ||
    value === 'settings' ||
    value === 'users' ||
    value === 'departments' ||
    value === 'agents' ||
    value === 'tools' ||
    value === 'rag' ||
    value === 'audit'
  ) return value
  return 'overview'
}

export default function AdminPage() {
  const { user, signOut } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const activeTab = normalizeTab(searchParams.get('tab'))

  const subtitle = useMemo(() => {
    if (!user) return ''
    return user.isGlobalAdmin ? 'Global Admin' : 'Admin'
  }, [user])

  function setTab(tab: AdminTab) {
    setSearchParams({ tab })
  }

  return (
    <div className="h-full flex bg-zinc-950 text-zinc-100">
      <aside className="w-72 shrink-0 border-r border-zinc-800 bg-zinc-900 flex flex-col">
        <div className="px-4 py-4 border-b border-zinc-800">
          <div className="text-sm font-semibold tracking-wide">Admin Console</div>
          <div className="text-xs text-zinc-500 mt-1">{subtitle}</div>
        </div>

        <nav className="p-2 space-y-1 flex-1">
          {TAB_ORDER.map(tab => (
            <button
              key={tab}
              onClick={() => setTab(tab)}
              className={`w-full text-left px-3 py-2 rounded-lg text-sm capitalize transition-colors ${
                activeTab === tab
                  ? 'bg-zinc-700 text-zinc-100'
                  : 'text-zinc-400 hover:bg-zinc-800 hover:text-zinc-200'
              }`}
            >
              {tab}
            </button>
          ))}
        </nav>

        <div className="p-3 border-t border-zinc-800 space-y-2">
          <button
            onClick={() => { window.location.href = '/' }}
            className="w-full px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          >
            Back To Chat
          </button>
          <button
            onClick={signOut}
            className="w-full px-3 py-2 rounded-lg text-sm bg-red-700/80 hover:bg-red-700 transition-colors"
          >
            Sign Out
          </button>
        </div>
      </aside>

      <main className="flex-1 min-w-0 overflow-y-auto">
        {activeTab === 'overview' && <AdminOverviewTab />}
        {activeTab === 'models' && <AdminModelsTab />}
        {activeTab === 'settings' && <AdminSettingsTab />}
        {activeTab === 'users' && <AdminUsersTab />}
        {activeTab === 'departments' && <AdminDepartmentsTab />}
        {activeTab === 'agents' && <AdminAgentsTab />}
        {activeTab === 'tools' && <AdminToolsTab />}
        {activeTab === 'rag' && <AdminRagTab />}
        {activeTab === 'audit' && <AdminAuditTab />}
      </main>
    </div>
  )
}
