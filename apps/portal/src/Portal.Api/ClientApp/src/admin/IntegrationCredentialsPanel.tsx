import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { Activity, CheckCircle2, Download, KeyRound, Network, RefreshCcw, ShieldCheck, Trash2 } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import { estimatingAdminApi } from './estimatingApi'
import type { EnterpriseIntegrationProvider, EnterpriseQuoteSyncResult, IntegrationCredential, IntegrationCredentialOverview } from './types'

const FULCRUM_NAME = 'Fulcrum Public API'
const FULCRUM_KEY = 'fulcrum-public-api'
const ACUMATICA_NAME = 'Acumatica API'
const ACUMATICA_KEY = 'acumatica-api'

function keyFromName(value: string) {
  return value
    .trim()
    .toLocaleLowerCase('en-US')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
}

function formatDate(value: string | null) {
  if (!value) return 'Not yet'
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}

export default function IntegrationCredentialsPanel() {
  const [credentials, setCredentials] = useState<IntegrationCredential[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState<string | null>(null)
  const [testing, setTesting] = useState<string | null>(null)
  const [syncing, setSyncing] = useState<string | null>(null)
  const [savingProvider, setSavingProvider] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [activeProvider, setActiveProvider] = useState('Fulcrum')
  const [selectedProvider, setSelectedProvider] = useState('Fulcrum')
  const [supportedProviders, setSupportedProviders] = useState<string[]>(['Fulcrum', 'Acumatica'])
  const [editingKey, setEditingKey] = useState<string | null>(FULCRUM_KEY)
  const [displayName, setDisplayName] = useState(FULCRUM_NAME)
  const [secret, setSecret] = useState('')

  const sortedCredentials = useMemo(
    () => [...credentials].sort((left, right) => left.displayName.localeCompare(right.displayName)),
    [credentials],
  )

  useEffect(() => {
    let active = true
    void portalApi<IntegrationCredentialOverview>('/api/admin/integration-credentials')
      .then((overview) => {
        if (!active) return
        setCredentials(overview.credentials)
        setActiveProvider(overview.activeProvider)
        setSelectedProvider(overview.activeProvider)
        setSupportedProviders(overview.supportedProviders)
        const fulcrum = overview.credentials.find((credential) => credential.credentialKey === FULCRUM_KEY)
        if (fulcrum) {
          setEditingKey(fulcrum.credentialKey)
          setDisplayName(fulcrum.displayName)
        }
      })
      .catch((cause) => {
        if (active) setError(toErrorMessage(cause))
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => { active = false }
  }, [])

  function resetForm() {
    setEditingKey(null)
    setDisplayName('')
    setSecret('')
  }

  function editCredential(credential: IntegrationCredential) {
    setEditingKey(credential.credentialKey)
    setDisplayName(credential.displayName)
    setSecret('')
    setError(null)
    setMessage(null)
  }

  function configureProvider(credentialKey: string, credentialName: string) {
    const existing = credentials.find((credential) => credential.credentialKey === credentialKey)
    setEditingKey(credentialKey)
    setDisplayName(existing?.displayName ?? credentialName)
    setSecret('')
    setError(null)
    setMessage(null)
  }

  async function saveActiveProvider() {
    if (selectedProvider === activeProvider) return
    setSavingProvider(true)
    setError(null)
    setMessage(null)
    try {
      const updated = await portalApi<EnterpriseIntegrationProvider>('/api/admin/integration-provider', {
        method: 'PUT',
        body: JSON.stringify({ provider: selectedProvider }),
      })
      setActiveProvider(updated.activeProvider)
      setSelectedProvider(updated.activeProvider)
      setMessage(`${updated.activeProvider} is now the source for enterprise data pulls and pushes across connected modules.`)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSavingProvider(false)
    }
  }

  async function saveCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const credentialKey = editingKey ?? keyFromName(displayName)
    if (!credentialKey) {
      setError('Enter a recognizable name for this API key.')
      return
    }
    if (!secret.trim()) {
      setError('Enter the new API key or token. Saved values cannot be retrieved or reused by this form.')
      return
    }

    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const updated = await portalApi<IntegrationCredential>(
        `/api/admin/integration-credentials/${encodeURIComponent(credentialKey)}`,
        {
          method: 'PUT',
          body: JSON.stringify({ displayName, secret }),
        },
      )
      setCredentials((current) => [
        ...current.filter((credential) => credential.credentialKey !== updated.credentialKey),
        updated,
      ])
      setSecret('')
      setEditingKey(updated.credentialKey)
      setDisplayName(updated.displayName)
      const expiryNote = updated.expiresAt
        ? ` It expires ${formatDate(updated.expiresAt)}.`
        : ''
      setMessage(`${updated.displayName} was encrypted and saved. The original value is no longer available to this page.${expiryNote}`)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  async function removeCredential(credential: IntegrationCredential) {
    if (!window.confirm(`Delete ${credential.displayName}? Connected jobs will stop using this API key immediately and will fail until a replacement is saved.`)) return
    setDeleting(credential.credentialKey)
    setError(null)
    setMessage(null)
    try {
      await portalApi<void>(`/api/admin/integration-credentials/${encodeURIComponent(credential.credentialKey)}`, {
        method: 'DELETE',
      })
      setCredentials((current) => current.filter((item) => item.credentialKey !== credential.credentialKey))
      if (editingKey === credential.credentialKey) resetForm()
      setMessage(`${credential.displayName} was deleted. Connected jobs can no longer use that API key.`)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setDeleting(null)
    }
  }

  async function testCredential(credential: IntegrationCredential) {
    setTesting(credential.credentialKey)
    setError(null)
    setMessage(null)
    try {
      const updated = await portalApi<IntegrationCredential>(
        `/api/admin/integration-credentials/${encodeURIComponent(credential.credentialKey)}/test`,
        { method: 'POST' },
      )
      setCredentials((current) => current.map((item) =>
        item.credentialKey === updated.credentialKey ? updated : item,
      ))
      if (updated.lastTestSucceeded) {
        setMessage(updated.lastTestMessage ?? `${updated.displayName} passed the connection test.`)
      } else {
        setError(updated.lastTestMessage ?? `${updated.displayName} failed the connection test.`)
      }
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setTesting(null)
    }
  }

  async function runManualPull(credential: IntegrationCredential) {
    setSyncing(credential.credentialKey)
    setError(null)
    setMessage(null)
    try {
      const result = await estimatingAdminApi<EnterpriseQuoteSyncResult>(
        '/api/quote-history/sync',
        { method: 'POST' },
      )
      setCredentials((current) => current.map((item) =>
        item.credentialKey === credential.credentialKey
          ? { ...item, lastUsedAt: result.completedAt }
          : item,
      ))
      setMessage(
        `${result.providerName} pull completed: ${result.recordsReceived.toLocaleString()} received, `
        + `${result.newRecords.toLocaleString()} new, ${result.updatedRecords.toLocaleString()} updated, `
        + `${result.unchangedRecords.toLocaleString()} unchanged.`,
      )
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSyncing(null)
    }
  }

  return (
    <div className="integration-credentials-stack">
      <section className="admin-surface integration-credential-intro" aria-labelledby="integration-credentials-heading">
        <header className="admin-surface-head">
          <div>
            <span className="kicker">Server credentials</span>
            <h2 id="integration-credentials-heading">Enterprise system connection</h2>
            <p>Choose the active business system once, then manage its protected credentials below. Connected modules use the same selection automatically.</p>
          </div>
          <span className="admin-permission-badge"><ShieldCheck size={14} aria-hidden="true" /> Administrators only</span>
        </header>

        <div className="enterprise-provider-switch">
          <span className="integration-credential-icon"><Network size={19} aria-hidden="true" /></span>
          <div>
            <strong>Active data provider</strong>
            <p>Project quantities, estimating quotes, and future Engineering and Quality connections are routed through this provider.</p>
          </div>
          <label>
            <span className="sr-only">Enterprise data provider</span>
            <select value={selectedProvider} onChange={(event) => setSelectedProvider(event.target.value)}>
              {supportedProviders.map((provider) => <option key={provider} value={provider}>{provider}</option>)}
            </select>
          </label>
          <button type="button" className="solid-button" disabled={savingProvider || selectedProvider === activeProvider} onClick={() => void saveActiveProvider()}>
            {savingProvider ? 'Switching...' : selectedProvider === activeProvider ? `${activeProvider} active` : `Switch to ${selectedProvider}`}
          </button>
        </div>

        {selectedProvider === 'Acumatica' && (
          <p className="enterprise-provider-note">Acumatica should be active only after its endpoint, credentials, and field mappings have been configured for production.</p>
        )}

        <div className="integration-credential-callout">
          <span><KeyRound size={19} aria-hidden="true" /></span>
          <div>
            <strong>Provider credentials</strong>
            <p>Tokens are encrypted for this server. Fulcrum currently supports a live connection test; Acumatica testing becomes available when its tenant authentication is configured.</p>
          </div>
          <div className="integration-provider-actions">
            <button type="button" className="ghost-button" onClick={() => configureProvider(FULCRUM_KEY, FULCRUM_NAME)}>Configure Fulcrum</button>
            <button type="button" className="ghost-button" onClick={() => configureProvider(ACUMATICA_KEY, ACUMATICA_NAME)}>Configure Acumatica</button>
          </div>
        </div>

        {error && <p className="admin-message error" role="alert">{error}</p>}
        {message && <p className="admin-message success" role="status"><CheckCircle2 size={15} aria-hidden="true" /> {message}</p>}

        <form className="integration-credential-form" onSubmit={(event) => void saveCredential(event)}>
          <label>
            <span>Credential name</span>
            <input
              value={displayName}
              maxLength={160}
              placeholder="Example: Fulcrum Public API"
              onChange={(event) => setDisplayName(event.target.value)}
              required
            />
            <small>Use a clear system and purpose name. Existing entries retain their internal identifier when renamed.</small>
          </label>
          <label>
            <span>{editingKey && credentials.some((credential) => credential.credentialKey === editingKey) ? 'Replacement API key or token' : 'API key or token'}</span>
            <input
              type="password"
              value={secret}
              maxLength={16000}
              autoComplete="off"
              placeholder="Paste the new value"
              onChange={(event) => setSecret(event.target.value)}
              required
            />
            <small>The field is cleared after saving. You can update or delete this API key at any time from the saved credentials list.</small>
          </label>
          <div className="integration-credential-actions">
            <button type="submit" className="solid-button" disabled={saving}>{saving ? 'Encrypting and saving...' : editingKey && credentials.some((credential) => credential.credentialKey === editingKey) ? 'Update API key' : 'Save API key'}</button>
            <button type="button" className="ghost-button" onClick={resetForm}>Add another</button>
          </div>
        </form>
      </section>

      <section className="admin-surface" aria-labelledby="saved-credentials-heading">
        <header className="admin-surface-head">
          <div><span className="kicker">Protected inventory</span><h2 id="saved-credentials-heading">Saved credentials</h2><p>Only status and audit information are visible. Every entry can be updated or deleted; secret values cannot be revealed.</p></div>
        </header>
        {loading && <div className="admin-loading" role="status">Loading saved credentials...</div>}
        {!loading && sortedCredentials.length === 0 && <div className="estimator-settings-empty"><strong>No API keys saved</strong><p>Configure Fulcrum or Acumatica above, or add another named key.</p></div>}
        {!loading && sortedCredentials.length > 0 && (
          <div className="integration-credential-list">
            {sortedCredentials.map((credential) => {
              const expired = credential.expiresAt !== null && new Date(credential.expiresAt).getTime() <= Date.now()
              const expiringSoon = !expired && credential.expiresAt !== null && new Date(credential.expiresAt).getTime() <= Date.now() + 30 * 24 * 60 * 60 * 1000
              return (
                <article className={expired ? 'is-expired' : expiringSoon ? 'is-expiring' : ''} key={credential.credentialKey}>
                  <span className="integration-credential-icon"><KeyRound size={18} aria-hidden="true" /></span>
                  <div className="integration-credential-summary">
                    <strong>{credential.displayName}</strong>
                    <code aria-label="Saved API key is hidden">••••••••••••••••</code>
                    <small>{expired ? 'Expired' : expiringSoon ? 'Expires soon' : 'Encrypted and configured'} · Updated {formatDate(credential.updatedAt)} by {credential.updatedBy}</small>
                    {credential.expiresAt && <small>Expires {formatDate(credential.expiresAt)}</small>}
                    <small>Last used {formatDate(credential.lastUsedAt)}</small>
                    {credential.lastTestedAt
                      ? <>
                          <span className={`integration-test-status ${credential.lastTestSucceeded ? 'success' : 'failure'}`}>
                            {credential.lastTestSucceeded ? 'Connection test passed' : 'Connection test failed'}
                            {credential.lastTestHttpStatusCode ? ` · HTTP ${credential.lastTestHttpStatusCode}` : ''}
                          </span>
                          <small>{credential.lastTestMessage} Tested {formatDate(credential.lastTestedAt)}{credential.lastTestedBy ? ` by ${credential.lastTestedBy}` : ''}.</small>
                        </>
                      : credential.credentialKey === FULCRUM_KEY && <span className="integration-test-status untested">Connection not tested yet</span>}
                  </div>
                  <div className="integration-credential-row-actions">
                    {credential.credentialKey === FULCRUM_KEY && <button type="button" className="solid-button" disabled={testing === credential.credentialKey || syncing === credential.credentialKey} onClick={() => void testCredential(credential)}><Activity size={14} aria-hidden="true" /> {testing === credential.credentialKey ? 'Testing...' : 'Test API connection'}</button>}
                    {credential.credentialKey === FULCRUM_KEY && <button type="button" className="ghost-button" disabled={syncing === credential.credentialKey || testing === credential.credentialKey || activeProvider !== 'Fulcrum'} title={activeProvider === 'Fulcrum' ? 'Pull the latest Fulcrum quotes into Estimating Logs' : 'Set Fulcrum as the active provider before starting a pull'} onClick={() => void runManualPull(credential)}><Download size={14} aria-hidden="true" /> {syncing === credential.credentialKey ? 'Pulling quotes...' : 'Pull estimating quotes'}</button>}
                    <button type="button" className="ghost-button" onClick={() => editCredential(credential)}><RefreshCcw size={14} aria-hidden="true" /> Update API key</button>
                    <button type="button" className="ghost-button danger" disabled={deleting === credential.credentialKey} onClick={() => void removeCredential(credential)}><Trash2 size={14} aria-hidden="true" /> {deleting === credential.credentialKey ? 'Deleting...' : 'Delete API key'}</button>
                  </div>
                </article>
              )
            })}
          </div>
        )}
      </section>
    </div>
  )
}
