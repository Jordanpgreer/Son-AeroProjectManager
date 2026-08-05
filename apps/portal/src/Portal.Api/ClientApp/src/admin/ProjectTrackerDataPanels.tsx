import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  FileSpreadsheet,
  UploadCloud,
} from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type { ImportResult } from './types'

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
