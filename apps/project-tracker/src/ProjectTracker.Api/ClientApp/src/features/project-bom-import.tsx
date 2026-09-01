import { useEffect, useRef, useState } from 'react'
import type { FormEvent, MouseEvent } from 'react'
import {
  AlertTriangle,
  ArrowRight,
  CheckCircle2,
  Download,
  FileCheck2,
  FileSpreadsheet,
  ShieldCheck,
  UploadCloud,
  X,
} from 'lucide-react'
import { api } from '../lib'
import type { ImportApplyResult, ImportValidationResult, ProjectDetail } from '../types'

type BomAction = 'template' | 'validate' | 'review' | 'confirm' | null

function displayValue(value: string | null) {
  return value?.trim() || 'Blank'
}

function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1_000)
}

async function responseError(response: Response) {
  const text = await response.text()
  try {
    const payload = JSON.parse(text) as { message?: string; detail?: string; title?: string }
    return payload.message || payload.detail || payload.title || text
  } catch {
    return text || `${response.status} ${response.statusText}`
  }
}

async function downloadWorkbook(path: string, fallbackName: string) {
  const response = await fetch(path, { credentials: 'same-origin' })
  if (!response.ok) throw new Error(await responseError(response))
  const disposition = response.headers.get('content-disposition') ?? ''
  const utf8Name = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1]
  saveBlob(await response.blob(), decodeURIComponent(utf8Name ?? plainName ?? fallbackName))
}

export function ProjectBomImport({
  project,
  onApplied,
}: {
  project: ProjectDetail
  onApplied?: () => Promise<void>
}) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [open, setOpen] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [action, setAction] = useState<BomAction>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [review, setReview] = useState<ImportValidationResult | null>(null)
  const [confirming, setConfirming] = useState(false)
  const busy = action !== null

  useEffect(() => {
    setOpen(false)
    setFile(null)
    setAction(null)
    setError(null)
    setMessage(null)
    setReview(null)
    setConfirming(false)
    if (inputRef.current) inputRef.current.value = ''
  }, [project.id])

  useEffect(() => {
    if (!open) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) setOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [busy, open])

  const close = () => {
    if (!busy) setOpen(false)
  }

  const closeFromBackdrop = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) close()
  }

  async function download(kind: 'template' | 'review', path: string) {
    if (busy) return
    setAction(kind)
    setError(null)
    try {
      await downloadWorkbook(
        path,
        kind === 'template' ? `Project-${project.id}-BOM.xlsx` : `Project-${project.id}-BOM-Review.xlsx`,
      )
      setMessage(kind === 'template'
        ? 'Project BOM downloaded with the current project and operation data.'
        : 'Highlighted review downloaded. Correct any marked rows and upload it again.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The workbook could not be downloaded.')
    } finally {
      setAction(null)
    }
  }

  async function validate(event: FormEvent) {
    event.preventDefault()
    if (!file || busy) return
    setAction('validate')
    setError(null)
    setMessage(null)
    setReview(null)
    setConfirming(false)
    try {
      const form = new FormData()
      form.append('file', file)
      const result = await api<ImportValidationResult>(`/api/projects/${project.id}/bom/validate`, {
        method: 'POST',
        body: form,
      })
      setReview(result)
      setMessage(result.errors.length > 0
        ? `Validation found ${result.errors.length} error${result.errors.length === 1 ? '' : 's'}. Nothing was saved.`
        : result.changeCount > 0
          ? `Validation passed. Review ${result.changeCount} proposed field change${result.changeCount === 1 ? '' : 's'} before confirming.`
          : 'Validation passed. This workbook matches the current project and has no changes to apply.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The BOM could not be validated.')
    } finally {
      setAction(null)
    }
  }

  async function confirmUpload() {
    if (!review?.canConfirm || busy) return
    setAction('confirm')
    setError(null)
    try {
      const result = await api<ImportApplyResult>(
        `/api/projects/${project.id}/bom/reviews/${review.reviewId}/confirm`,
        { method: 'POST' },
      )
      setReview(null)
      setFile(null)
      setConfirming(false)
      if (inputRef.current) inputRef.current.value = ''
      setMessage(
        `Project BOM applied: ${result.operationsAdded} operation${result.operationsAdded === 1 ? '' : 's'} added, `
        + `${result.operationsUpdated} operation${result.operationsUpdated === 1 ? '' : 's'} updated, and `
        + `${result.projectsUpdated} project record${result.projectsUpdated === 1 ? '' : 's'} updated.`,
      )
      if (onApplied) {
        try {
          await onApplied()
        } catch {
          setError('The BOM changes were applied, but the latest project view could not be refreshed. Reload Project Tracker to see the saved data.')
        }
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The reviewed BOM changes could not be applied.')
      setConfirming(false)
    } finally {
      setAction(null)
    }
  }

  return (
    <>
      <button className="button ghost" type="button" onClick={() => setOpen(true)}>
        <FileSpreadsheet size={15} /> Project BOM
      </button>
      {open && (
        <div className="modal-backdrop project-bom-backdrop" onMouseDown={closeFromBackdrop}>
          <section className="modal project-bom-modal" role="dialog" aria-modal="true" aria-labelledby="project-bom-title">
            <header className="project-bom-head">
              <div>
                <span className="kicker">Project-level controlled import</span>
                <h2 id="project-bom-title">Project BOM · <span className="technical-id">{project.programName}</span></h2>
                <p>Download this project’s operations, edit the approved columns, then validate the workbook before saving anything.</p>
              </div>
              <button className="icon-button" type="button" aria-label="Close project BOM" disabled={busy} onClick={close}><X size={17} /></button>
            </header>

            <div className="project-bom-body">
              <ol className="project-bom-steps" aria-label="Project BOM workflow">
                <li><span>1</span><strong>Download</strong><small>Current project operations</small></li>
                <li><span>2</span><strong>Edit</strong><small>Dates, routing, and operation fields</small></li>
                <li><span>3</span><strong>Validate</strong><small>Compare without saving</small></li>
                <li><span>4</span><strong>Confirm</strong><small>Apply reviewed changes</small></li>
              </ol>

              <div className="project-bom-template">
                <div>
                  <FileSpreadsheet size={22} aria-hidden="true" />
                  <span><strong>Project BOM template</strong><small>Includes this project only. Existing system IDs are protected; new operation IDs are assigned automatically.</small></span>
                </div>
                <button className="button ghost" type="button" disabled={busy} onClick={() => void download('template', `/api/projects/${project.id}/bom/template`)}>
                  <Download size={15} /> {action === 'template' ? 'Preparing…' : 'Download BOM'}
                </button>
              </div>

              {error && <p className="project-bom-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
              {message && <p className={`project-bom-notice ${review?.errors.length ? 'warning' : 'success'}`} role="status"><CheckCircle2 size={16} /> {message}</p>}

              <form className="project-bom-upload" onSubmit={validate}>
                <label className="project-bom-file">
                  <UploadCloud size={23} aria-hidden="true" />
                  <span><strong>{file?.name || 'Choose this project’s BOM workbook'}</strong><small>.xlsx or .xlsm · maximum 15 MB · selecting a file does not change project data</small></span>
                  <input
                    ref={inputRef}
                    type="file"
                    accept=".xlsx,.xlsm,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/vnd.ms-excel.sheet.macroEnabled.12"
                    onChange={(event) => {
                      setFile(event.target.files?.[0] ?? null)
                      setReview(null)
                      setConfirming(false)
                      setError(null)
                      setMessage(null)
                    }}
                  />
                </label>
                <button className="button primary" type="submit" disabled={!file || busy}>
                  <FileCheck2 size={15} /> {action === 'validate' ? 'Comparing…' : 'Validate and compare'}
                </button>
              </form>

              <p className="project-bom-safety"><ShieldCheck size={15} /> This upload is locked to Project ID {project.id}. Omitted operations are not deleted, and no other project can be changed.</p>

              {review && (
                <section className="project-bom-review" aria-labelledby="project-bom-review-title">
                  <header>
                    <div><span className="kicker">Staged review</span><h3 id="project-bom-review-title">No changes have been saved yet</h3></div>
                    <small>Review expires {new Date(review.expiresAt).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}</small>
                  </header>

                  <div className="project-bom-stats">
                    <span><strong>{review.operationRows}</strong><small>Operations checked</small></span>
                    <span><strong>{review.operationsAdded}</strong><small>New operations</small></span>
                    <span><strong>{review.operationsUpdated}</strong><small>Updated operations</small></span>
                    <span><strong>{review.changeCount}</strong><small>Field changes</small></span>
                    <span className={review.errors.length ? 'has-errors' : 'is-valid'}><strong>{review.errors.length}</strong><small>Errors</small></span>
                  </div>

                  {review.errors.length > 0 && (
                    <div className="project-bom-errors">
                      <h4><AlertTriangle size={16} /> Fix these errors before confirming</h4>
                      <ul>
                        {review.errors.map((issue, index) => (
                          <li key={`${issue.sheet}-${issue.row}-${issue.column}-${index}`}>
                            <strong>{issue.sheet} · row {issue.row}{issue.column ? ` · ${issue.column}` : ''}</strong>
                            <span>{issue.message}</span>
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}

                  {review.changes.length > 0 && (
                    <div className="project-bom-changes">
                      <table>
                        <thead><tr><th>Location</th><th>Record</th><th>Field</th><th>Current</th><th>Uploaded</th></tr></thead>
                        <tbody>
                          {review.changes.map((change, index) => (
                            <tr key={`${change.sheet}-${change.row}-${change.field}-${index}`} className={change.changeType === 'Added' ? 'is-added' : ''}>
                              <td><strong>{change.sheet}</strong><small>Row {change.row}</small></td>
                              <td><span>{change.changeType}</span><small>{change.recordKey}</small></td>
                              <td>{change.field}</td>
                              <td>{displayValue(change.currentValue)}</td>
                              <td>{displayValue(change.uploadedValue)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                      {review.changeCount > review.changes.length && <p>Showing the first {review.changes.length} changes. The highlighted workbook contains the full comparison.</p>}
                    </div>
                  )}

                  <footer className="project-bom-review-actions">
                    <button className="button ghost" type="button" disabled={busy} onClick={() => void download('review', review.reviewWorkbookUrl)}>
                      <Download size={15} /> {action === 'review' ? 'Preparing…' : 'Download highlighted review'}
                    </button>
                    {review.canConfirm && !confirming && (
                      <button className="button primary" type="button" disabled={busy} onClick={() => setConfirming(true)}>
                        Confirm upload <ArrowRight size={15} />
                      </button>
                    )}
                  </footer>

                  {confirming && (
                    <div className="project-bom-confirm" role="alert">
                      <div><AlertTriangle size={18} /><span><strong>Apply {review.changeCount} reviewed changes?</strong><small>Project Tracker data is updated only after this confirmation.</small></span></div>
                      <div>
                        <button className="button ghost" type="button" disabled={busy} onClick={() => setConfirming(false)}><X size={15} /> Cancel</button>
                        <button className="button primary" type="button" disabled={busy} onClick={() => void confirmUpload()}><CheckCircle2 size={15} /> {action === 'confirm' ? 'Applying…' : 'Apply changes'}</button>
                      </div>
                    </div>
                  )}
                </section>
              )}
            </div>
          </section>
        </div>
      )}
    </>
  )
}
