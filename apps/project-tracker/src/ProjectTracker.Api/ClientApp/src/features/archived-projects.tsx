import { useEffect, useMemo, useState } from 'react'
import { ArchiveRestore, RefreshCw, RotateCcw, Search } from 'lucide-react'
import { api, compactDate } from '../lib'
import type { ArchivedProject } from '../types'
import { EmptyState, SkeletonLine } from '../components'

export function ArchivedProjectsPanel() {
  const [projects, setProjects] = useState<ArchivedProject[]>([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<ArchivedProject | null>(null)
  const [restoring, setRestoring] = useState(false)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      setProjects(await api<ArchivedProject[]>('/api/admin/archived-projects'))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load archived projects.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  const filtered = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return projects
    return projects.filter((project) =>
      project.programName.toLowerCase().includes(value)
      || project.customerName?.toLowerCase().includes(value)
      || project.salesOrderNumber?.toLowerCase().includes(value))
  }, [projects, query])

  const restore = async () => {
    if (!confirming || restoring) return
    setRestoring(true)
    try {
      await api<void>(`/api/admin/archived-projects/${confirming.id}/restore`, {
        method: 'POST',
        body: JSON.stringify({ version: confirming.version }),
      })
      setConfirming(null)
      await load()
    } finally {
      setRestoring(false)
    }
  }

  return (
    <section className="settings-tab-content">
      <section className="panel table-panel workcenter-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Record Retention</span>
            <h2>Archived Projects</h2>
            <p>Archived projects stay out of active views while their schedule and activity history remain retained.</p>
          </div>
          <div className="toolbar-inline">
            <label className="search-field">
              <Search size={15} />
              <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search archived projects" />
            </label>
            <button className="icon-button" type="button" onClick={() => void load()} title="Refresh archived projects" aria-label="Refresh archived projects"><RefreshCw size={15} /></button>
          </div>
        </header>

        {loading ? (
          <div className="workcenter-list" aria-label="Loading archived projects">
            {[0, 1, 2].map((item) => <div className="workcenter-row" key={item}><SkeletonLine width="35%" /><SkeletonLine width="24%" /></div>)}
          </div>
        ) : error ? (
          <EmptyState title="Archived projects unavailable" body={error} />
        ) : projects.length === 0 ? (
          <EmptyState title="No archived projects" body="Projects archived from Project Detail will appear here and can be restored." />
        ) : filtered.length === 0 ? (
          <EmptyState title="No matching archived projects" body="Try another part number, customer, or sales order." />
        ) : (
          <div className="workcenter-list">
            {filtered.map((project) => (
              <div className="workcenter-row" key={project.id}>
                <ArchiveRestore size={16} />
                <span>
                  <strong className="technical-id">{project.programName}</strong>
                  <small>
                    {project.customerName || 'Customer not set'}
                    {project.salesOrderNumber && <> / <span className="technical-id">{project.salesOrderNumber}</span></>}
                    {' · '}Archived {compactDate(project.deletedAt.slice(0, 10))}
                    {project.deletedByDisplayName ? ` by ${project.deletedByDisplayName}` : ''}
                  </small>
                </span>
                <div className="workcenter-actions">
                  <button className="button ghost" type="button" onClick={() => setConfirming(project)}><RotateCcw size={14} /> Restore</button>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {confirming && (
        <div className="modal-backdrop" onClick={() => !restoring && setConfirming(null)}>
          <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="restore-project-title" onClick={(event) => event.stopPropagation()}>
            <div className="confirmation-icon complete"><RotateCcw size={22} /></div>
            <div className="confirmation-copy">
              <span className="kicker">Restore Project</span>
              <h2 id="restore-project-title">Restore {confirming.programName}?</h2>
              <p>The project will return to its appropriate active or completed view with its operations and activity history intact.</p>
            </div>
            <div className="modal-actions confirmation-actions">
              <button className="button ghost" type="button" onClick={() => setConfirming(null)} disabled={restoring}>Cancel</button>
              <button className="button complete-solid" type="button" onClick={() => void restore()} disabled={restoring}>{restoring ? 'Restoring...' : 'Restore Project'}</button>
            </div>
          </section>
        </div>
      )}
    </section>
  )
}
