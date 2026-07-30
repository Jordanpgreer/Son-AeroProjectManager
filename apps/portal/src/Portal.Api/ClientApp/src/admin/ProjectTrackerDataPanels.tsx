import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  ArchiveRestore,
  CheckCircle2,
  FileSpreadsheet,
  RefreshCw,
  UploadCloud,
} from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type { ArchivedProject, ImportResult } from './types'

function formatDate(value: string) {
  const date = new Date(value)
  return Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat('en-US', {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
      }).format(date)
    : value
}

export function ArchivedProjectsPanel() {
  const [projects, setProjects] = useState<ArchivedProject[] | null>(null)
  const [restoringId, setRestoringId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function load() {
    setError(null)
    try {
      setProjects(await trackerApi<ArchivedProject[]>('/api/admin/archived-projects'))
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  useEffect(() => { void load() }, [])

  async function restore(project: ArchivedProject) {
    if (!window.confirm(`Restore “${project.programName}” to Project Tracker?`)) return
    setRestoringId(project.id)
    setError(null)
    setMessage(null)
    try {
      await trackerApi<void>(`/api/admin/archived-projects/${project.id}/restore`, {
        method: 'POST',
        body: JSON.stringify({ version: project.version }),
      })
      setMessage(`${project.programName} restored.`)
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setRestoringId(null)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="archived-heading">
      <header className="admin-surface-head">
        <div><span className="kicker">Recovery</span><h2 id="archived-heading">Archived projects</h2><p>Restore soft-deleted projects to their previous completed or active state.</p></div>
        <button className="ghost-button" type="button" onClick={() => void load()}><RefreshCw size={15} /> Refresh</button>
      </header>
      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}
      {projects === null ? (
        <div className="admin-loading">Loading archived projects…</div>
      ) : projects.length === 0 ? (
        <p className="admin-empty">No archived projects.</p>
      ) : (
        <div className="admin-archive-list">
          {projects.map((project) => (
            <article key={project.id}>
              <span className="admin-archive-icon"><ArchiveRestore size={18} /></span>
              <div>
                <strong>{project.programName}</strong>
                <span>{project.customerName || 'No customer'}{project.salesOrderNumber ? ` · SO ${project.salesOrderNumber}` : ''}</span>
                <small>Archived {formatDate(project.deletedAt)}{project.deletedByDisplayName ? ` by ${project.deletedByDisplayName}` : ''}</small>
              </div>
              <button className="ghost-button" type="button" disabled={restoringId !== null} onClick={() => void restore(project)}>
                <ArchiveRestore size={15} /> {restoringId === project.id ? 'Restoring…' : 'Restore'}
              </button>
            </article>
          ))}
        </div>
      )}
    </section>
  )
}

export function ImportsPanel() {
  const inputRef = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function upload(event: FormEvent) {
    event.preventDefault()
    if (!file || busy) return
    setBusy(true)
    setError(null)
    setMessage(null)
    try {
      const form = new FormData()
      form.append('file', file)
      const result = await trackerApi<ImportResult>('/api/import/upload', {
        method: 'POST',
        body: form,
      })
      setMessage(
        `Imported ${result.projectCount} project${result.projectCount === 1 ? '' : 's'}, `
        + `${result.taskCount} operation${result.taskCount === 1 ? '' : 's'}, and `
        + `${result.holidayCount} holiday${result.holidayCount === 1 ? '' : 's'}.`,
      )
      setFile(null)
      if (inputRef.current) inputRef.current.value = ''
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="admin-surface admin-import" aria-labelledby="imports-heading">
      <header className="admin-surface-head">
        <div><span className="kicker">Controlled data intake</span><h2 id="imports-heading">Workbook import</h2><p>Upload a Project Tracker workbook. Existing projects are preserved; matching records follow the server import rules.</p></div>
        <UploadCloud size={23} />
      </header>
      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}
      <form onSubmit={upload}>
        <label className="admin-file-picker">
          <FileSpreadsheet size={25} aria-hidden="true" />
          <span><strong>{file?.name || 'Choose an Excel workbook'}</strong><small>.xlsx or .xlsm · server validation applies</small></span>
          <input
            ref={inputRef}
            type="file"
            accept=".xlsx,.xlsm"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
          />
        </label>
        <button className="solid-button" type="submit" disabled={!file || busy}>
          <UploadCloud size={16} /> {busy ? 'Importing…' : 'Upload and import'}
        </button>
      </form>
    </section>
  )
}
