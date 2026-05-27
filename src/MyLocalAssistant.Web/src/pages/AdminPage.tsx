import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'
import * as api from '../api/client'
import AdminOverviewTab from '../components/admin/AdminOverviewTab'
import AdminUsageTab from '../components/admin/AdminUsageTab'
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
  | 'usage'
  | 'models'
  | 'settings'
  | 'users'
  | 'departments'
  | 'agents'
  | 'tools'
  | 'rag'
  | 'audit'

const BASE_TABS: AdminTab[] = ['overview', 'usage', 'models', 'settings', 'users', 'departments', 'rag', 'audit']
const GLOBAL_TABS: AdminTab[] = ['agents', 'tools']

const TAB_TITLES: Record<AdminTab, string> = {
  overview: 'Overview',
  usage: 'Usage',
  models: 'Models',
  settings: 'Settings',
  users: 'Users',
  departments: 'Departments',
  agents: 'Agents',
  tools: 'Tools',
  rag: 'RAG',
  audit: 'Audit',
}

function normalizeTab(value: string | null): AdminTab {
  if (
    value === 'usage' ||
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
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = normalizeTab(searchParams.get('tab'))
  const [appVersion, setAppVersion] = useState<string>('…')

  const tabOrder = useMemo(
    () => (user?.isGlobalAdmin ? [...BASE_TABS.slice(0, 6), ...GLOBAL_TABS, ...BASE_TABS.slice(6)] : BASE_TABS),
    [user?.isGlobalAdmin],
  )

  const activeTab = tabOrder.includes(requestedTab) ? requestedTab : tabOrder[0]

  useEffect(() => {
    if (requestedTab !== activeTab) {
      setSearchParams({ tab: activeTab }, { replace: true })
    }
  }, [activeTab, requestedTab, setSearchParams])

  useEffect(() => {
    let cancelled = false
    void api.getHealth()
      .then(h => {
        if (!cancelled) setAppVersion(h.version || '?')
      })
      .catch(() => {
        if (!cancelled) setAppVersion('?')
      })
    return () => { cancelled = true }
  }, [])

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
          <div className="text-sm font-semibold tracking-wide">Admin</div>
          <div className="text-xs text-zinc-500 mt-1">{subtitle}</div>
          <div className="text-[11px] text-zinc-500 mt-1">Version {appVersion}</div>
        </div>

        <nav className="p-2 space-y-1 flex-1">
          {tabOrder.map(tab => (
            <button
              key={tab}
              onClick={() => setTab(tab)}
              className={`w-full text-left px-3 py-2 rounded-lg text-sm transition-colors ${
                activeTab === tab
                  ? 'bg-zinc-700 text-zinc-100'
                  : 'text-zinc-400 hover:bg-zinc-800 hover:text-zinc-200'
              }`}
            >
              {TAB_TITLES[tab]}
            </button>
          ))}
        </nav>

        <div className="p-3 border-t border-zinc-800 space-y-2">
          <button
            onClick={() => { navigate('/change-password') }}
            className="w-full px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
          >
            Change Password
          </button>
          <button
            onClick={() => { navigate('/') }}
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
        {activeTab === 'usage' && <AdminUsageTab />}
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
