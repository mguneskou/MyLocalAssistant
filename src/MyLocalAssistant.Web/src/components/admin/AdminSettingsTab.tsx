import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../../api/client'
import { useAuth } from '../../contexts/AuthContext'
import type {
  CloudKeysStatusDto,
  ServerSettingsDto,
  UpdateCloudKeysRequest,
  UpdateServerSettingsRequest,
} from '../../api/types'

type EditableSettings = UpdateServerSettingsRequest

interface CloudDraft {
  openAiApiKey: string
  anthropicApiKey: string
  openAiBaseUrl: string
  groqApiKey: string
  geminiApiKey: string
  mistralApiKey: string
  cerebrasApiKey: string
}

interface CloudClearFlags {
  openAiApiKey: boolean
  anthropicApiKey: boolean
  groqApiKey: boolean
  geminiApiKey: boolean
  mistralApiKey: boolean
  cerebrasApiKey: boolean
}

const emptyCloudDraft: CloudDraft = {
  openAiApiKey: '',
  anthropicApiKey: '',
  openAiBaseUrl: '',
  groqApiKey: '',
  geminiApiKey: '',
  mistralApiKey: '',
  cerebrasApiKey: '',
}

const emptyCloudClearFlags: CloudClearFlags = {
  openAiApiKey: false,
  anthropicApiKey: false,
  groqApiKey: false,
  geminiApiKey: false,
  mistralApiKey: false,
  cerebrasApiKey: false,
}

function toCloudRequest(draft: CloudDraft, clearFlags: CloudClearFlags): UpdateCloudKeysRequest {
  const mapSecretValue = (value: string, clearRequested: boolean): string | null | undefined => {
    if (clearRequested) return ''
    const trimmed = value.trim()
    if (trimmed.length === 0) return undefined
    return trimmed
  }

  const mapUrlValue = (value: string): string | null | undefined => {
    const trimmed = value.trim()
    return trimmed
  }

  return {
    openAiApiKey: mapSecretValue(draft.openAiApiKey, clearFlags.openAiApiKey),
    anthropicApiKey: mapSecretValue(draft.anthropicApiKey, clearFlags.anthropicApiKey),
    openAiBaseUrl: mapUrlValue(draft.openAiBaseUrl),
    groqApiKey: mapSecretValue(draft.groqApiKey, clearFlags.groqApiKey),
    geminiApiKey: mapSecretValue(draft.geminiApiKey, clearFlags.geminiApiKey),
    mistralApiKey: mapSecretValue(draft.mistralApiKey, clearFlags.mistralApiKey),
    cerebrasApiKey: mapSecretValue(draft.cerebrasApiKey, clearFlags.cerebrasApiKey),
  }
}

export default function AdminSettingsTab() {
  const { user } = useAuth()
  const isGlobalAdmin = user?.isGlobalAdmin === true

  const [settings, setSettings] = useState<ServerSettingsDto | null>(null)
  const [form, setForm] = useState<EditableSettings | null>(null)

  const [globalPrompt, setGlobalPrompt] = useState('')
  const [cloudStatus, setCloudStatus] = useState<CloudKeysStatusDto | null>(null)
  const [cloudDraft, setCloudDraft] = useState<CloudDraft>(emptyCloudDraft)
  const [cloudClearFlags, setCloudClearFlags] = useState<CloudClearFlags>(emptyCloudClearFlags)
  const [cloudTestResults, setCloudTestResults] = useState<Record<string, string>>({})

  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [savingPrompt, setSavingPrompt] = useState(false)
  const [savingCloud, setSavingCloud] = useState(false)
  const [testingProvider, setTestingProvider] = useState<string | null>(null)

  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<string>('')

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    setStatus('')
    try {
      const s = await api.getServerSettings()
      setSettings(s)
      setForm({
        accessTokenMinutes: s.accessTokenMinutes,
        refreshTokenDays: s.refreshTokenDays,
        messageBodyRetentionDays: s.messageBodyRetentionDays,
        auditRetentionDays: s.auditRetentionDays,
      })

      if (isGlobalAdmin) {
        const [promptResp, cloudResp] = await Promise.all([
          api.getGlobalSystemPrompt(),
          api.getCloudKeysStatus(),
        ])
        setGlobalPrompt(promptResp.systemPrompt)
        setCloudStatus(cloudResp)
        setCloudDraft({ ...emptyCloudDraft, openAiBaseUrl: cloudResp.openAiBaseUrl ?? '' })
        setCloudClearFlags(emptyCloudClearFlags)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load settings.')
    } finally {
      setLoading(false)
    }
  }, [isGlobalAdmin])

  useEffect(() => {
    load()
  }, [load])

  const canSave = useMemo(
    () => !!form && !loading && !saving,
    [form, loading, saving],
  )

  async function saveSettings() {
    if (!form) return
    setSaving(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.updateServerSettings(form)
      setSettings(updated)
      setStatus('Saved runtime settings. Token lifetimes apply on next login.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save settings.')
    } finally {
      setSaving(false)
    }
  }

  async function saveGlobalPrompt() {
    if (!isGlobalAdmin) return
    setSavingPrompt(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.setGlobalSystemPrompt(globalPrompt)
      setGlobalPrompt(updated.systemPrompt)
      setStatus('Global system prompt saved.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save global system prompt.')
    } finally {
      setSavingPrompt(false)
    }
  }

  async function saveCloudKeys() {
    if (!isGlobalAdmin) return
    setSavingCloud(true)
    setError(null)
    setStatus('')
    try {
      const updated = await api.setCloudKeys(toCloudRequest(cloudDraft, cloudClearFlags))
      setCloudStatus(updated)
      setCloudDraft({ ...emptyCloudDraft, openAiBaseUrl: updated.openAiBaseUrl ?? '' })
      setCloudClearFlags(emptyCloudClearFlags)
      setStatus('Cloud key settings saved.')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save cloud key settings.')
    } finally {
      setSavingCloud(false)
    }
  }

  async function testProvider(provider: string) {
    if (!isGlobalAdmin) return
    setTestingProvider(provider)
    setError(null)
    try {
      const result = await api.testCloudKey(provider)
      setCloudTestResults(prev => ({
        ...prev,
        [provider]: result.ok
          ? `OK${result.detail ? `: ${result.detail}` : ''}`
          : `Failed${result.detail ? `: ${result.detail}` : ''}`,
      }))
      setCloudStatus(await api.getCloudKeysStatus())
    } catch (e) {
      setCloudTestResults(prev => ({
        ...prev,
        [provider]: e instanceof Error ? e.message : 'Test failed.',
      }))
    } finally {
      setTestingProvider(null)
    }
  }

  function setNumber<K extends keyof EditableSettings>(key: K, value: string) {
    if (!form) return
    const parsed = Number.parseInt(value, 10)
    setForm({
      ...form,
      [key]: Number.isNaN(parsed) ? 0 : parsed,
    })
  }

  function setCloudSecretValue<K extends keyof CloudClearFlags>(key: K, value: string) {
    setCloudDraft(prev => ({ ...prev, [key]: value }))
    setCloudClearFlags(prev => ({ ...prev, [key]: false }))
  }

  function clearCloudSecret<K extends keyof CloudClearFlags>(key: K) {
    setCloudDraft(prev => ({ ...prev, [key]: '' }))
    setCloudClearFlags(prev => ({ ...prev, [key]: true }))
  }

  const cloudProviders = [
    { id: 'openai', label: 'OpenAI', configured: cloudStatus?.openAiConfigured ?? false },
    { id: 'anthropic', label: 'Anthropic', configured: cloudStatus?.anthropicConfigured ?? false },
    { id: 'groq', label: 'Groq', configured: cloudStatus?.groqConfigured ?? false },
    { id: 'gemini', label: 'Gemini', configured: cloudStatus?.geminiConfigured ?? false },
    { id: 'mistral', label: 'Mistral', configured: cloudStatus?.mistralConfigured ?? false },
    { id: 'cerebras', label: 'Cerebras', configured: cloudStatus?.cerebrasConfigured ?? false },
  ]

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Server Settings</h1>
          <p className="text-sm text-zinc-500 mt-1">Runtime, owner prompt, and cloud-provider configuration.</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={load}
            className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 transition-colors"
            disabled={loading || saving || savingCloud || savingPrompt}
          >
            {loading ? 'Loading…' : 'Reload'}
          </button>
          <button
            onClick={saveSettings}
            className="px-3 py-2 rounded-lg text-sm bg-blue-600 hover:bg-blue-500 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            disabled={!canSave}
          >
            {saving ? 'Saving…' : 'Save Runtime'}
          </button>
        </div>
      </div>

      {error && (
        <div className="rounded-lg border border-red-800 bg-red-950/30 text-red-300 px-4 py-3 text-sm">
          {error}
        </div>
      )}
      {status && (
        <div className="rounded-lg border border-emerald-800 bg-emerald-950/20 text-emerald-300 px-4 py-3 text-sm">
          {status}
        </div>
      )}

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 space-y-4">
        <SectionTitle title="Read-only runtime" />
        <GridReadOnly label="Listen URL" value={settings?.listenUrl ?? '-'} />
        <GridReadOnly label="JWT issuer" value={settings?.jwtIssuer ?? '-'} />
        <GridReadOnly label="JWT audience" value={settings?.jwtAudience ?? '-'} />
        <GridReadOnly label="Active LLM" value={settings?.defaultModelId ?? '(none)'} />
        <GridReadOnly label="Active embedding" value={settings?.embeddingModelId ?? '(none)'} />
      </div>

      <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 space-y-4">
        <SectionTitle title="Mutable runtime" />
        <GridInput
          label="Access token (minutes)"
          value={form?.accessTokenMinutes ?? 0}
          onChange={value => setNumber('accessTokenMinutes', value)}
          hint="1..1440"
        />
        <GridInput
          label="Refresh token (days)"
          value={form?.refreshTokenDays ?? 0}
          onChange={value => setNumber('refreshTokenDays', value)}
          hint="1..365"
        />
        <GridInput
          label="Message body retention (days)"
          value={form?.messageBodyRetentionDays ?? 0}
          onChange={value => setNumber('messageBodyRetentionDays', value)}
          hint="1..3650"
        />
        <GridInput
          label="Audit retention (days)"
          value={form?.auditRetentionDays ?? 0}
          onChange={value => setNumber('auditRetentionDays', value)}
          hint="1..3650"
        />
      </div>

      {isGlobalAdmin ? (
        <>
          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 space-y-3">
            <SectionTitle title="Global system prompt" />
            <textarea
              value={globalPrompt}
              onChange={e => setGlobalPrompt(e.target.value)}
              rows={8}
              className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700 text-sm"
              placeholder="Global owner prompt applied to all agents"
            />
            <div className="flex justify-end">
              <button
                onClick={saveGlobalPrompt}
                disabled={savingPrompt}
                className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
              >
                {savingPrompt ? 'Saving…' : 'Save Prompt'}
              </button>
            </div>
          </div>

          <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4 space-y-4">
            <SectionTitle title="Cloud providers" />

            <div className="rounded-lg border border-zinc-800 overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-zinc-800/70 text-zinc-300">
                  <tr>
                    <th className="text-left px-3 py-2">Provider</th>
                    <th className="text-left px-3 py-2">Configured</th>
                    <th className="text-left px-3 py-2">Test</th>
                    <th className="text-left px-3 py-2">Result</th>
                  </tr>
                </thead>
                <tbody>
                  {cloudProviders.map(p => (
                    <tr key={p.id} className="border-t border-zinc-800">
                      <td className="px-3 py-2">{p.label}</td>
                      <td className="px-3 py-2">{p.configured ? 'Yes' : 'No'}</td>
                      <td className="px-3 py-2">
                        <button
                          onClick={() => testProvider(p.id)}
                          disabled={testingProvider !== null}
                          className="px-2 py-1 rounded bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50 text-xs"
                        >
                          {testingProvider === p.id ? 'Testing…' : 'Test'}
                        </button>
                      </td>
                      <td className="px-3 py-2 text-zinc-400">{cloudTestResults[p.id] ?? '-'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <PasswordField
                label="OpenAI API key"
                value={cloudDraft.openAiApiKey}
                onChange={value => setCloudSecretValue('openAiApiKey', value)}
                onClear={() => clearCloudSecret('openAiApiKey')}
                clearRequested={cloudClearFlags.openAiApiKey}
              />
              <PasswordField
                label="Anthropic API key"
                value={cloudDraft.anthropicApiKey}
                onChange={value => setCloudSecretValue('anthropicApiKey', value)}
                onClear={() => clearCloudSecret('anthropicApiKey')}
                clearRequested={cloudClearFlags.anthropicApiKey}
              />
              <PasswordField
                label="Groq API key"
                value={cloudDraft.groqApiKey}
                onChange={value => setCloudSecretValue('groqApiKey', value)}
                onClear={() => clearCloudSecret('groqApiKey')}
                clearRequested={cloudClearFlags.groqApiKey}
              />
              <PasswordField
                label="Gemini API key"
                value={cloudDraft.geminiApiKey}
                onChange={value => setCloudSecretValue('geminiApiKey', value)}
                onClear={() => clearCloudSecret('geminiApiKey')}
                clearRequested={cloudClearFlags.geminiApiKey}
              />
              <PasswordField
                label="Mistral API key"
                value={cloudDraft.mistralApiKey}
                onChange={value => setCloudSecretValue('mistralApiKey', value)}
                onClear={() => clearCloudSecret('mistralApiKey')}
                clearRequested={cloudClearFlags.mistralApiKey}
              />
              <PasswordField
                label="Cerebras API key"
                value={cloudDraft.cerebrasApiKey}
                onChange={value => setCloudSecretValue('cerebrasApiKey', value)}
                onClear={() => clearCloudSecret('cerebrasApiKey')}
                clearRequested={cloudClearFlags.cerebrasApiKey}
              />
              <label className="block text-sm md:col-span-2">
                <span className="block text-zinc-400 mb-1">OpenAI base URL (optional)</span>
                <input
                  type="text"
                  value={cloudDraft.openAiBaseUrl}
                  onChange={e => setCloudDraft(prev => ({ ...prev, openAiBaseUrl: e.target.value }))}
                  className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
                  placeholder="https://api.openai.com/v1"
                />
              </label>
            </div>

            {Object.values(cloudClearFlags).some(Boolean) && (
              <div className="text-xs text-amber-400">
                One or more keys are marked to be cleared when you click Save Cloud Keys.
              </div>
            )}

            <div className="flex justify-end">
              <button
                onClick={saveCloudKeys}
                disabled={savingCloud}
                className="px-3 py-2 rounded-lg text-sm bg-zinc-800 hover:bg-zinc-700 disabled:opacity-50"
              >
                {savingCloud ? 'Saving…' : 'Save Cloud Keys'}
              </button>
            </div>
          </div>
        </>
      ) : (
        <div className="rounded-lg border border-zinc-800 bg-zinc-900 px-4 py-3 text-sm text-zinc-400">
          Global-admin-only controls include cloud key management and global system prompt editing.
        </div>
      )}
    </div>
  )
}

function SectionTitle({ title }: { title: string }) {
  return <h2 className="text-sm font-semibold text-zinc-200">{title}</h2>
}

function GridReadOnly({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-3 gap-2 text-sm">
      <div className="text-zinc-500">{label}</div>
      <div className="md:col-span-2 text-zinc-200 break-all">{value}</div>
    </div>
  )
}

function GridInput({
  label,
  value,
  onChange,
  hint,
}: {
  label: string
  value: number
  onChange: (value: string) => void
  hint: string
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-3 gap-2 text-sm items-center">
      <div className="text-zinc-500">{label}</div>
      <div className="md:col-span-2 flex items-center gap-2">
        <input
          type="number"
          value={value}
          onChange={e => onChange(e.target.value)}
          className="w-36 px-3 py-2 rounded bg-zinc-950 border border-zinc-700 text-zinc-100"
        />
        <span className="text-zinc-500 text-xs">{hint}</span>
      </div>
    </div>
  )
}

function PasswordField({
  label,
  value,
  onChange,
  onClear,
  clearRequested,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  onClear: () => void
  clearRequested: boolean
}) {
  return (
    <div className="block text-sm">
      <div className="flex items-center justify-between gap-2 mb-1">
        <span className="text-zinc-400">{label}</span>
        <button
          type="button"
          onClick={onClear}
          className="px-2 py-1 rounded bg-zinc-800 hover:bg-zinc-700 text-xs"
        >
          Clear
        </button>
      </div>
      <input
        type="password"
        value={value}
        onChange={e => onChange(e.target.value)}
        className="w-full px-3 py-2 rounded bg-zinc-950 border border-zinc-700"
        autoComplete="off"
      />
      {clearRequested && <div className="text-xs text-amber-400 mt-1">Will clear on save.</div>}
    </div>
  )
}
