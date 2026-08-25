import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, GraduationCap, MessageCircleQuestion, Save, ShieldCheck } from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type { AdminAccessPreviewTarget } from './types'
import WalkthroughPreviewLauncher from './WalkthroughPreviewLauncher'

type WalkthroughSettings = {
  enabled: boolean
  assistantEnabled: boolean
  assistantName: string
  updatedAt: string
}

type SettingsDraft = Pick<WalkthroughSettings, 'enabled' | 'assistantEnabled' | 'assistantName'>

const DEFAULT_DRAFT: SettingsDraft = {
  enabled: false,
  assistantEnabled: false,
  assistantName: 'Benny',
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

  useEffect(() => {
    void trackerApi<WalkthroughSettings>('/api/settings/walkthrough')
      .then((value) => {
        setSettings(value)
        setDraft({
          enabled: value.enabled,
          assistantEnabled: value.assistantEnabled,
          assistantName: value.assistantName,
        })
      })
      .catch((cause) => setError(toErrorMessage(cause)))
  }, [])

  const assistantName = draft.assistantName.trim()
  const nameError = assistantName.length === 0
    ? 'Enter a name for the assistant.'
    : assistantName.length > 40
      ? 'Assistant name cannot exceed 40 characters.'
      : null
  const changed = Boolean(settings && (
    settings.enabled !== draft.enabled
    || settings.assistantEnabled !== draft.assistantEnabled
    || settings.assistantName !== assistantName
  ))

  async function save() {
    if (!settings || !changed || nameError || saving) return
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
        }),
      })
      setSettings(next)
      setDraft({
        enabled: next.enabled,
        assistantEnabled: next.assistantEnabled,
        assistantName: next.assistantName,
      })
      setMessage('Onboarding and assistant settings saved.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="walkthrough-heading">
      <header className="admin-surface-head">
        <div>
          <span className="kicker">Guided help</span>
          <h2 id="walkthrough-heading">Onboarding and assistant</h2>
          <p>Manage permission-matched training and the deterministic, non-AI helper shown inside Project Tracker.</p>
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
          </div>
          <div className="admin-inline-save">
            <p><ShieldCheck size={15} /> These settings control help features only; they do not change group permissions.</p>
            <button className="solid-button" type="button" disabled={!changed || Boolean(nameError) || saving} onClick={() => void save()}>
              <Save size={15} /> {saving ? 'Saving…' : 'Save help settings'}
            </button>
          </div>
        </>
      ) : null}

      <WalkthroughPreviewLauncher onLaunch={onPreviewWalkthrough} />
    </section>
  )
}
