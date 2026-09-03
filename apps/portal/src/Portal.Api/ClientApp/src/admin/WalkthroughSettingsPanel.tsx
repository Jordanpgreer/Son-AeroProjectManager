import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, GraduationCap, Save, ShieldCheck } from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type { AdminAccessPreviewTarget } from './types'
import WalkthroughPreviewLauncher from './WalkthroughPreviewLauncher'

/**
 * Project Tracker onboarding. Benny's own settings live on the admin console's
 * Benny page; they share this settings record, so they are read here and
 * written back untouched.
 */

type WalkthroughSettings = {
  enabled: boolean
  assistantEnabled: boolean
  assistantName: string
  assistantIdleModules: string[]
  assistantIdleDelayMinutes: number
  updatedAt: string
}

export default function WalkthroughSettingsPanel({
  onPreviewWalkthrough,
}: {
  onPreviewWalkthrough: (target: AdminAccessPreviewTarget) => Promise<void>
}) {
  const [settings, setSettings] = useState<WalkthroughSettings | null>(null)
  const [enabled, setEnabled] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    void trackerApi<WalkthroughSettings>('/api/settings/walkthrough')
      .then((value) => {
        setSettings(value)
        setEnabled(value.enabled)
      })
      .catch((cause) => setError(toErrorMessage(cause)))
  }, [])

  const changed = Boolean(settings && settings.enabled !== enabled)

  async function save() {
    if (!settings || !changed || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const next = await trackerApi<WalkthroughSettings>('/api/settings/walkthrough', {
        method: 'PUT',
        body: JSON.stringify({
          enabled,
          // Owned by the Benny admin page; echoed back unchanged.
          assistantEnabled: settings.assistantEnabled,
          assistantName: settings.assistantName,
          assistantIdleModules: settings.assistantIdleModules,
          assistantIdleDelayMinutes: settings.assistantIdleDelayMinutes,
        }),
      })
      setSettings(next)
      setEnabled(next.enabled)
      setMessage('Onboarding settings saved.')
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
          <h2 id="walkthrough-heading">Onboarding</h2>
          <p>Manage the permission-matched training workspace shown inside Project Tracker.</p>
        </div>
        <GraduationCap size={23} aria-hidden="true" />
      </header>

      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}

      {!settings && !error ? <div className="admin-loading" role="status">Loading walkthrough setting…</div> : settings ? (
        <>
          <div className="admin-onboarding-settings admin-benny-settings">
            <section className="admin-onboarding-setting" aria-labelledby="walkthrough-setting-heading">
              <header>
                <GraduationCap size={18} aria-hidden="true" />
                <div><strong id="walkthrough-setting-heading">Permission-matched walkthrough</strong><small>Fictional training workspace</small></div>
              </header>
              <label className="admin-check-row">
                <input
                  type="checkbox"
                  checked={enabled}
                  disabled={saving}
                  onChange={(event) => {
                    setEnabled(event.target.checked)
                    setMessage(null)
                  }}
                />
                <span>
                  <strong>{enabled ? 'Walkthrough enabled' : 'Walkthrough disabled'}</strong>
                  <small>Users see only lessons that match their current Project Tracker permissions.</small>
                </span>
              </label>
            </section>
          </div>
          <div className="admin-inline-save">
            <p><ShieldCheck size={15} /> These settings control help features only; they do not change group permissions.</p>
            <button className="solid-button" type="button" disabled={!changed || saving} onClick={() => void save()}>
              <Save size={15} /> {saving ? 'Saving…' : 'Save onboarding settings'}
            </button>
          </div>
        </>
      ) : null}

      <WalkthroughPreviewLauncher onLaunch={onPreviewWalkthrough} />
    </section>
  )
}
