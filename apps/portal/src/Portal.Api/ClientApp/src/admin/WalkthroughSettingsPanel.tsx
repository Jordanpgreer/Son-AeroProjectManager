import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Eye, GraduationCap, MessageCircleQuestion, Save, ShieldCheck, Sparkles } from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type { AdminAccessPreviewTarget } from './types'
import WalkthroughPreviewLauncher from './WalkthroughPreviewLauncher'
import BennyRageOverlay from './BennyRageOverlay'
import { resolveModuleApplicationUrl } from './moduleUrls'

type WalkthroughSettings = {
  enabled: boolean
  assistantEnabled: boolean
  assistantName: string
  assistantIdleModules: string[]
  assistantIdleDelayMinutes: number
  updatedAt: string
}

type SettingsDraft = Pick<WalkthroughSettings, 'enabled' | 'assistantEnabled' | 'assistantName' | 'assistantIdleModules' | 'assistantIdleDelayMinutes'>

const BENNY_MODULES = [
  { key: 'project-tracker', name: 'Project Tracker', port: 5135 },
  { key: 'engineering-hub', name: 'Engineering Hub', port: 5150 },
  { key: 'estimating-dashboard', name: 'Estimating Dashboard', port: 5160 },
  { key: 'quality-assurance', name: 'Quality Assurance', port: 5170 },
] as const

const DEFAULT_DRAFT: SettingsDraft = {
  enabled: false,
  assistantEnabled: false,
  assistantName: 'Benny',
  assistantIdleModules: ['project-tracker'],
  assistantIdleDelayMinutes: 10,
}

export default function WalkthroughSettingsPanel({
  onPreviewWalkthrough,
}: {
  onPreviewWalkthrough: (target: AdminAccessPreviewTarget) => Promise<void>
}) {
  const [settings, setSettings] = useState<WalkthroughSettings | null>(null)
  const [draft, setDraft] = useState<SettingsDraft>(DEFAULT_DRAFT)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [assistantProvoked, setAssistantProvoked] = useState(false)

  useEffect(() => {
    void trackerApi<WalkthroughSettings>('/api/settings/walkthrough')
      .then((value) => {
        setSettings(value)
        setDraft({
          enabled: value.enabled,
          assistantEnabled: value.assistantEnabled,
          assistantName: value.assistantName,
          assistantIdleModules: value.assistantIdleModules,
          assistantIdleDelayMinutes: value.assistantIdleDelayMinutes,
        })
      })
      .catch((cause) => setError(toErrorMessage(cause)))
  }, [])

  const calmAssistant = useCallback(() => setAssistantProvoked(false), [])

  const assistantName = draft.assistantName.trim()
  const nameError = assistantName.length === 0
    ? 'Enter a name for the assistant.'
    : assistantName.length > 40
      ? 'Assistant name cannot exceed 40 characters.'
      : null
  const idleDelayError = !Number.isInteger(draft.assistantIdleDelayMinutes)
    || draft.assistantIdleDelayMinutes < 5
    || draft.assistantIdleDelayMinutes > 60
    ? 'Choose a whole number from 5 to 60 minutes.'
    : null
  const changed = Boolean(settings && (
    settings.enabled !== draft.enabled
    || settings.assistantEnabled !== draft.assistantEnabled
    || settings.assistantName !== assistantName
    || settings.assistantIdleDelayMinutes !== draft.assistantIdleDelayMinutes
    || settings.assistantIdleModules.join(',') !== draft.assistantIdleModules.join(',')
  ))

  async function save() {
    if (!settings || !changed || nameError || idleDelayError || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const next = await trackerApi<WalkthroughSettings>('/api/settings/walkthrough', {
        method: 'PUT',
        body: JSON.stringify({
          enabled: draft.enabled,
          assistantEnabled: draft.assistantEnabled,
          assistantName,
          assistantIdleModules: draft.assistantIdleModules,
          assistantIdleDelayMinutes: draft.assistantIdleDelayMinutes,
        }),
      })
      setSettings(next)
      setDraft({
        enabled: next.enabled,
        assistantEnabled: next.assistantEnabled,
        assistantName: next.assistantName,
        assistantIdleModules: next.assistantIdleModules,
        assistantIdleDelayMinutes: next.assistantIdleDelayMinutes,
      })
      setMessage('Onboarding and Benny settings saved.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  function toggleIdleModule(moduleKey: string, enabled: boolean) {
    setDraft((current) => ({
      ...current,
      assistantIdleModules: enabled
        ? BENNY_MODULES.map((module) => module.key).filter((key) => key === moduleKey || current.assistantIdleModules.includes(key))
        : current.assistantIdleModules.filter((key) => key !== moduleKey),
    }))
    setMessage(null)
  }

  function previewIdleBenny(port: number) {
    const url = new URL(resolveModuleApplicationUrl(window.location, port))
    url.searchParams.set('bennyIdlePreview', '1')
    window.open(url.toString(), '_blank', 'noopener,noreferrer')
  }

  return (
    <section className="admin-surface" aria-labelledby="walkthrough-heading">
      <header className="admin-surface-head">
        <div>
          <span className="kicker">Guided help</span>
          <h2 id="walkthrough-heading">Onboarding and assistant</h2>
          <p>Manage permission-matched training, the deterministic helper, and Benny's cosmetic idle activity across modules.</p>
        </div>
        <GraduationCap size={23} aria-hidden="true" />
      </header>

      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}

      {!settings && !error ? <div className="admin-loading" role="status">Loading walkthrough setting…</div> : settings ? (
        <>
          <div className="admin-onboarding-settings">
            <section className="admin-onboarding-setting" aria-labelledby="walkthrough-setting-heading">
              <header>
                <GraduationCap size={18} aria-hidden="true" />
                <div><strong id="walkthrough-setting-heading">Permission-matched walkthrough</strong><small>Fictional training workspace</small></div>
              </header>
              <label className="admin-check-row">
                <input
                  type="checkbox"
                  checked={draft.enabled}
                  disabled={saving}
                  onChange={(event) => {
                    setDraft((current) => ({ ...current, enabled: event.target.checked }))
                    setMessage(null)
                  }}
                />
                <span>
                  <strong>{draft.enabled ? 'Walkthrough enabled' : 'Walkthrough disabled'}</strong>
                  <small>Users see only lessons that match their current Project Tracker permissions.</small>
                </span>
              </label>
            </section>

            <section className="admin-onboarding-setting" aria-labelledby="assistant-setting-heading">
              <header>
                <MessageCircleQuestion size={18} aria-hidden="true" />
                <div><strong id="assistant-setting-heading">Keyword-matching assistant</strong><small>No AI or generated answers</small></div>
              </header>
              <label className="admin-check-row">
                <input
                  type="checkbox"
                  checked={draft.assistantEnabled}
                  disabled={saving}
                  onChange={(event) => {
                    setDraft((current) => ({ ...current, assistantEnabled: event.target.checked }))
                    setMessage(null)
                    // Switching the assistant off is not something he takes well.
                    if (!event.target.checked) setAssistantProvoked(true)
                  }}
                />
                <span>
                  <strong>{draft.assistantEnabled ? `${assistantName || 'Assistant'} enabled` : 'Assistant disabled'}</strong>
                  <small>When enabled, users can ask approved questions and open matched Project Tracker destinations.</small>
                </span>
              </label>
              <label className="admin-assistant-name-field">
                <span>Assistant name</span>
                <input
                  type="text"
                  value={draft.assistantName}
                  maxLength={40}
                  disabled={saving}
                  aria-invalid={Boolean(nameError)}
                  aria-describedby="assistant-name-help"
                  onChange={(event) => {
                    setDraft((current) => ({ ...current, assistantName: event.target.value }))
                    setMessage(null)
                  }}
                />
                <small id="assistant-name-help" className={nameError ? 'field-error' : undefined}>{nameError ?? 'Shown in the assistant launcher and its approved responses.'}</small>
              </label>
            </section>

            <section className="admin-onboarding-setting admin-benny-idle-setting" aria-labelledby="benny-idle-setting-heading">
              <header>
                <Sparkles size={18} aria-hidden="true" />
                <div><strong id="benny-idle-setting-heading">Idle Benny</strong><small>Cosmetic module activity</small></div>
              </header>
              <label className="admin-benny-delay-field">
                <span>Appear after</span>
                <span className="admin-benny-delay-input"><input type="number" min="5" max="60" step="1" value={draft.assistantIdleDelayMinutes} disabled={saving} aria-invalid={Boolean(idleDelayError)} onChange={(event) => {
                  setDraft((current) => ({ ...current, assistantIdleDelayMinutes: event.target.valueAsNumber }))
                  setMessage(null)
                }} /> minutes of inactivity</span>
                <small className={idleDelayError ? 'field-error' : undefined}>{idleDelayError ?? 'Mouse, keyboard, touch, focus, or scrolling immediately dismisses him and restores the page.'}</small>
              </label>
              <div className="admin-benny-module-grid">
                {BENNY_MODULES.map((module) => {
                  const enabled = draft.assistantIdleModules.includes(module.key)
                  return (
                    <div className="admin-benny-module" key={module.key}>
                      <label>
                        <input type="checkbox" checked={enabled} disabled={saving} onChange={(event) => toggleIdleModule(module.key, event.target.checked)} />
                        <span><strong>{module.name}</strong><small>{enabled ? 'Idle activity enabled' : 'Idle activity disabled'}</small></span>
                      </label>
                      <button type="button" className="ghost-button" onClick={() => previewIdleBenny(module.port)}><Eye size={14} /> Preview</button>
                    </div>
                  )
                })}
              </div>
            </section>
          </div>
          <div className="admin-inline-save">
            <p><ShieldCheck size={15} /> These settings control help features only; they do not change group permissions.</p>
            <button className="solid-button" type="button" disabled={!changed || Boolean(nameError) || Boolean(idleDelayError) || saving} onClick={() => void save()}>
              <Save size={15} /> {saving ? 'Saving…' : 'Save help settings'}
            </button>
          </div>
        </>
      ) : null}

      <WalkthroughPreviewLauncher onLaunch={onPreviewWalkthrough} />

      {assistantProvoked && (
        <BennyRageOverlay assistantName={assistantName || 'Benny'} onFinished={calmAssistant} />
      )}
    </section>
  )
}
