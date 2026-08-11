import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
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
import { toErrorMessage, trackerApi, trackerFile } from './api'
import type { ImportApplyResult, ImportValidationResult } from './types'

type ImportAction = 'template' | 'validate' | 'review' | 'confirm' | null

function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.append(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1_000)
}

function displayValue(value: string | null) {
  return value?.trim() || 'Blank'
}

export function ImportsPanel() {
  const inputRef = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [action, setAction] = useState<ImportAction>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [review, setReview] = useState<ImportValidationResult | null>(null)
  const [confirming, setConfirming] = useState(false)

  const busy = action !== null

  async function download(path: string, kind: 'template' | 'review') {
    if (busy) return
    setAction(kind)
    setError(null)
    try {
      const result = await trackerFile(path)
      downloadBlob(result.blob, result.fileName)
      setMessage(kind === 'template'
        ? 'Template downloaded with the current Project Tracker data.'
        : 'Highlighted review workbook downloaded. You can correct it and upload it again.')
    } catch (cause) {
      setError(toErrorMessage(cause))
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
      const result = await trackerApi<ImportValidationResult>('/api/import/validate', {
        method: 'POST',
        body: form,
      })
      setReview(result)
      setMessage(result.errors.length > 0
        ? `Validation finished with ${result.errors.length} error${result.errors.length === 1 ? '' : 's'}. Nothing was imported.`
        : result.changeCount > 0
          ? `Validation passed. Review ${result.changeCount} proposed field change${result.changeCount === 1 ? '' : 's'} before confirming.`
          : 'Validation passed, but the workbook matches the current system and has no changes to apply.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setAction(null)
    }
  }

  async function confirmUpload() {
    if (!review?.canConfirm || busy) return
    setAction('confirm')
    setError(null)
    try {
      const result = await trackerApi<ImportApplyResult>(`/api/import/reviews/${review.reviewId}/confirm`, {
        method: 'POST',
      })
      setMessage(
        `Import applied: ${result.projectsAdded} projects added, ${result.projectsUpdated} projects updated, `
        + `${result.operationsAdded} operations added, and ${result.operationsUpdated} operations updated.`,
      )
      setReview(null)
      setFile(null)
      setConfirming(false)
      if (inputRef.current) inputRef.current.value = ''
    } catch (cause) {
      setError(toErrorMessage(cause))
      setConfirming(false)
    } finally {
      setAction(null)
    }
  }

  return (
    <section className="admin-surface admin-import" aria-labelledby="imports-heading">
      <header className="admin-surface-head">
        <div>
          <span className="kicker">Administrator-controlled data intake</span>
          <h2 id="imports-heading">Reviewable workbook import</h2>
          <p>Use the controlled template for full project edits, or upload a supported legacy schedule to create new projects. Every proposed change is reviewed before anything is saved.</p>
        </div>
        <ShieldCheck size={23} />
      </header>

      <ol className="admin-import-steps" aria-label="Import workflow">
        <li><span>1</span><strong>Download</strong><small>Current projects and operations</small></li>
        <li><span>2</span><strong>Edit</strong><small>Keep IDs and sheet structure intact</small></li>
        <li><span>3</span><strong>Validate</strong><small>Compare without saving</small></li>
        <li><span>4</span><strong>Confirm</strong><small>Apply the reviewed changes</small></li>
      </ol>

      <div className="admin-import-template">
        <div>
          <FileSpreadsheet size={22} aria-hidden="true" />
          <span><strong>Controlled Project Tracker template</strong><small>Includes a Projects tab, an Operations tab, required-field guidance, and all current data for mass edits.</small></span>
        </div>
        <button className="ghost-button" type="button" disabled={busy} onClick={() => void download('/api/import/template', 'template')}>
          <Download size={16} /> {action === 'template' ? 'Preparing…' : 'Download template'}
        </button>
      </div>

      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className={`admin-notice ${review?.errors.length ? 'warning' : 'success'}`} role="status"><CheckCircle2 size={16} /> {message}</p>}

      <form className="admin-import-upload" onSubmit={validate}>
        <label className="admin-file-picker">
          <UploadCloud size={25} aria-hidden="true" />
          <span><strong>{file?.name || 'Choose a project workbook'}</strong><small>.xlsx or .xlsm · maximum 15 MB · selecting a file does not change system data</small></span>
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
        <button className="solid-button" type="submit" disabled={!file || busy}>
          <FileCheck2 size={16} /> {action === 'validate' ? 'Comparing…' : 'Validate and compare'}
        </button>
      </form>

      <p className="admin-import-safety"><ShieldCheck size={15} /> Rows omitted from the workbook are not deleted. Calculated read-only columns are checked by Project Tracker and are not imported.</p>

      {review && (
        <section className="admin-import-review" aria-labelledby="import-review-heading">
          <header>
            <div><span className="kicker">Staged review</span><h3 id="import-review-heading">No changes have been saved yet</h3></div>
            <small>Review expires {new Date(review.expiresAt).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}</small>
          </header>

          <p className="admin-import-safety">
            <FileSpreadsheet size={15} /> Detected format: <strong>{review.workbookFormat}</strong>
            {review.projectsRequiringCompletion > 0
              ? ` · ${review.projectsRequiringCompletion} new project${review.projectsRequiringCompletion === 1 ? '' : 's'} will request missing details when opened.`
              : ''}
          </p>

          <div className="admin-import-stats">
            <span><strong>{review.projectRows}</strong><small>Project rows checked</small></span>
            <span><strong>{review.operationRows}</strong><small>Operation rows checked</small></span>
            <span><strong>{review.changeCount}</strong><small>Field changes found</small></span>
            <span className={review.errors.length ? 'has-errors' : 'is-valid'}><strong>{review.errors.length}</strong><small>Validation errors</small></span>
          </div>

          <div className="admin-import-impact">
            <span><strong>{review.projectsAdded}</strong> projects added</span>
            <span><strong>{review.projectsUpdated}</strong> projects updated</span>
            <span><strong>{review.operationsAdded}</strong> operations added</span>
            <span><strong>{review.operationsUpdated}</strong> operations updated</span>
          </div>

          {review.errors.length > 0 && (
            <div className="admin-import-errors">
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
            <div className="admin-import-change-table">
              <table>
                <thead><tr><th>Location</th><th>Record</th><th>Field</th><th>Current</th><th>Uploaded</th></tr></thead>
                <tbody>
                  {review.changes.map((change, index) => (
                    <tr key={`${change.sheet}-${change.row}-${change.field}-${index}`} className={change.changeType === 'Added' ? 'is-added' : 'is-modified'}>
                      <td><strong>{change.sheet}</strong><small>Row {change.row}</small></td>
                      <td><span className="admin-change-kind">{change.changeType}</span><small>{change.recordKey}</small></td>
                      <td>{change.field}</td>
                      <td>{displayValue(change.currentValue)}</td>
                      <td>{displayValue(change.uploadedValue)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {review.changeCount > review.changes.length && <p>Showing the first {review.changes.length} changes. The review workbook contains the complete highlighted comparison.</p>}
            </div>
          )}

          <footer className="admin-import-review-actions">
            <button className="ghost-button" type="button" disabled={busy} onClick={() => void download(review.reviewWorkbookUrl, 'review')}>
              <Download size={16} /> {action === 'review' ? 'Preparing…' : 'Download highlighted review'}
            </button>
            {review.canConfirm && !confirming && (
              <button className="solid-button" type="button" disabled={busy} onClick={() => setConfirming(true)}>
                Confirm upload <ArrowRight size={16} />
              </button>
            )}
          </footer>

          {confirming && (
            <div className="admin-import-confirm" role="alert">
              <div><AlertTriangle size={19} /><span><strong>Apply {review.changeCount} reviewed changes?</strong><small>This is the point when Project Tracker data will be updated.</small></span></div>
              <div>
                <button className="ghost-button" type="button" disabled={busy} onClick={() => setConfirming(false)}><X size={15} /> Cancel</button>
                <button className="solid-button" type="button" disabled={busy} onClick={() => void confirmUpload()}><CheckCircle2 size={16} /> {action === 'confirm' ? 'Applying…' : 'Apply reviewed changes'}</button>
              </div>
            </div>
          )}
        </section>
      )}
    </section>
  )
}
