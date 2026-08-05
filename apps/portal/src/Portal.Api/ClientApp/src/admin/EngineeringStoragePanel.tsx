import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  FolderPlus,
  FolderTree,
  HardDrive,
  RefreshCw,
  Save,
  Server,
} from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import type { EngineeringStorageOverview } from './types'

const updatedLabel = (value: string | null) => value
  ? new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
  : 'Using deployment configuration'

export default function EngineeringStoragePanel() {
  const [overview, setOverview] = useState<EngineeringStorageOverview | null>(null)
  const [rootPath, setRootPath] = useState('')
  const [authorityName, setAuthorityName] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function load(announce = false) {
    setLoading(true)
    setError(null)
    try {
      const next = await portalApi<EngineeringStorageOverview>('/api/admin/engineering-storage')
      setOverview(next)
      setRootPath(next.rootPath)
      if (announce) setMessage('Storage folders refreshed from the configured server path.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  async function saveRoot(event: FormEvent) {
    event.preventDefault()
    if (!overview?.canManageStorage || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const next = await portalApi<EngineeringStorageOverview>('/api/admin/engineering-storage', {
        method: 'PUT',
        body: JSON.stringify({ rootPath: rootPath.trim() }),
      })
      setOverview(next)
      setRootPath(next.rootPath)
      setMessage('Engineering drawing storage path saved and indexed successfully.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  async function createAuthority(event: FormEvent) {
    event.preventDefault()
    if (!overview?.canManageStorage || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const next = await portalApi<EngineeringStorageOverview>('/api/admin/engineering-storage/design-authorities', {
        method: 'POST',
        body: JSON.stringify({ name: authorityName.trim() }),
      })
      setOverview(next)
      setRootPath(next.rootPath)
      setAuthorityName('')
      setMessage('Design authority created. Its server folder is ready for controlled drawings.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  return <section className="admin-surface engineering-storage" aria-labelledby="engineering-storage-heading" aria-busy={loading}>
    <header className="admin-surface-head engineering-storage-head">
      <div>
        <span className="kicker">Controlled drawing storage</span>
        <h2 id="engineering-storage-heading">File storage and design authorities</h2>
        <p>Set the server folder used for new drawing packages. Immediate child folders are the approved Design Authority list used throughout Engineering Hub.</p>
      </div>
      <button className="ghost-button" type="button" disabled={loading || saving} onClick={() => void load(true)}>
        <RefreshCw size={15}/> Refresh folders
      </button>
    </header>

    {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16}/> {error}</p>}
    {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16}/> {message}</p>}

    {loading || !overview ? <div className="admin-loading" role="status">Checking Engineering drawing storage...</div> : <>
      <div className={`engineering-storage-status ${overview.available && overview.writable ? 'healthy' : 'unavailable'}`}>
        <span className="engineering-storage-status-icon">{overview.isNetworkPath ? <Server size={22}/> : <HardDrive size={22}/>}</span>
        <div><strong>{overview.available && overview.writable ? 'Storage online' : 'Storage needs attention'}</strong><p>{overview.message}</p></div>
        <dl>
          <div><dt>Folder type</dt><dd>{overview.isNetworkPath ? 'UNC network share' : 'Local / mapped path'}</dd></div>
          <div><dt>Indexed authorities</dt><dd>{overview.designAuthorities.length}</dd></div>
          <div><dt>Prior roots retained</dt><dd>{overview.previousRootCount}</dd></div>
        </dl>
      </div>

      <form className="engineering-storage-path-form" onSubmit={saveRoot}>
        <label>
          <span>Drawing storage root</span>
          <input
            value={rootPath}
            onChange={event => setRootPath(event.target.value)}
            placeholder="\\server\share\Engineering\Drawings"
            spellCheck={false}
            required
            disabled={!overview.canManageStorage || saving}
          />
          <small>For a company server, use the Q drive’s UNC path rather than <code>Q:\</code>. The application service account must have read, create-folder, write, and delete access.</small>
        </label>
        <button className="solid-button" type="submit" disabled={!overview.canManageStorage || saving || rootPath.trim() === overview.rootPath}>
          <Save size={15}/> {saving ? 'Checking...' : 'Save and index path'}
        </button>
      </form>

      <p className="admin-readonly-note engineering-storage-history-note">
        Changing the active root does not move existing files. Up to eight prior roots remain available for existing drawing packages; all new uploads use the active root.
      </p>

      <section className="engineering-authorities" aria-labelledby="design-authorities-heading">
        <div className="admin-section-title">
          <div><h3 id="design-authorities-heading">Approved Design Authorities</h3><p>Indexed directly from folders under the active drawing root.</p></div>
          <FolderTree size={20} aria-hidden="true"/>
        </div>
        {overview.canManageStorage ? <form className="engineering-authority-create" onSubmit={createAuthority}>
          <label><span>New Design Authority</span><input value={authorityName} onChange={event => setAuthorityName(event.target.value)} required maxLength={200} placeholder="Authority folder name" disabled={saving || !overview.available}/></label>
          <button className="solid-button" type="submit" disabled={saving || !overview.available || !authorityName.trim()}><FolderPlus size={15}/> Create authority folder</button>
        </form> : <p className="admin-readonly-note">Creating authorities and changing the root requires Manage Engineering File Storage.</p>}

        {overview.designAuthorities.length ? <div className="engineering-authority-grid">
          {overview.designAuthorities.map(authority => <article key={authority}><FolderTree size={18}/><span><strong>{authority}</strong><small>Approved and selectable</small></span></article>)}
        </div> : <div className="engineering-authority-empty"><FolderTree size={24}/><strong>No approved authorities found</strong><p>Create the first Design Authority folder before users create drawings.</p></div>}
      </section>

      <footer className="engineering-storage-meta">Last settings update: <strong>{updatedLabel(overview.updatedAt)}</strong>{overview.updatedBy ? <> by <strong>{overview.updatedBy}</strong></> : null}</footer>
    </>}
  </section>
}
