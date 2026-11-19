import { FolderOpen, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { copyQuoteFileToLocation, QuoteApiError, removeQuoteFileLocation, saveQuoteFileLocation } from './api'
import Modal from './Modal'
import QuoteFolderActions from './QuoteFolderActions'
import type { QuoteStatusDetail } from './types'

export default function QuoteFileLocations({ detail, canEdit, disabled, onChanged }: {
  detail: QuoteStatusDetail; canEdit: boolean; disabled: boolean; onChanged: (detail: QuoteStatusDetail) => Promise<void>
}) {
  const [addingLocation, setAddingLocation] = useState(false)
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [deletePath, setDeletePath] = useState<string | null>(null)
  const [collision, setCollision] = useState<{ path: string; file: File } | null>(null)
  const locations = detail.fileLocations ?? (detail.quoteFolderPath ? [{ path: detail.quoteFolderPath, source: 'Fulcrum' as const }] : [])

  async function apply(next: Promise<QuoteStatusDetail>) {
    setBusy(true); setError(null)
    try { await onChanged(await next); setAddingLocation(false); setDeletePath(null) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'The file location could not be updated.') }
    finally { setBusy(false) }
  }

  async function upload(path: string, file: File, resolution: 'reject' | 'overwrite' | 'rename' = 'reject') {
    setBusy(true); setError(null)
    try {
      await onChanged(await copyQuoteFileToLocation(detail.quote.quoteHistoryId, detail.quote.version, path, file, resolution))
      setCollision(null)
    } catch (reason) {
      if (reason instanceof QuoteApiError && reason.code === 'FileAlreadyExists') setCollision({ path, file })
      else setError(reason instanceof Error ? reason.message : 'The file could not be copied.')
    } finally { setBusy(false) }
  }

  return <div className="qs-file-locations">
    <div className="qs-file-location-heading">
      <strong>File Location</strong>
      {canEdit && !addingLocation && <button type="button" className="qs-text-button" disabled={busy || disabled} onClick={() => { setDraft(''); setAddingLocation(true); setError(null) }}><Plus size={12} />Add location</button>}
    </div>
    {locations.length === 0 && !addingLocation && <p className="qs-file-location-empty">No quote folder yet.</p>}
    <div className="qs-file-location-list">
      {locations.map(location => <div className="qs-file-location" key={location.path}
        onDragOver={event => { if (canEdit && !busy && !disabled) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' } }}
        onDrop={event => { if (!canEdit || busy || disabled) return; event.preventDefault(); const file = event.dataTransfer.files.item(0); if (file) void upload(location.path, file) }}>
        <div className="qs-file-location-name" title={location.path}><FolderOpen size={16} aria-hidden="true" /><strong>Quote Folder</strong></div>
        <div className="qs-file-location-actions">
          <QuoteFolderActions path={location.path} quoteNumber={detail.quote.quoteNumber} />
          {canEdit && <button type="button" className="vq-icon-button is-destructive" disabled={busy || disabled} aria-label={`Remove ${location.path}`} onClick={() => setDeletePath(location.path)}><Trash2 size={14} /></button>}
        </div>
        {canEdit && <small className="qs-file-drop-hint">Drop a PDF, Outlook email, Excel sheet, or Word document to copy it here.</small>}
      </div>)}
      {addingLocation && <form className="qs-file-location qs-file-location-editor" onSubmit={event => {
        event.preventDefault()
        if (!busy && !disabled && draft.trim()) void apply(saveQuoteFileLocation(detail.quote.quoteHistoryId, detail.quote.version, draft))
      }}>
        <input autoFocus aria-label="New file location" value={draft} disabled={busy || disabled} onChange={event => setDraft(event.target.value)} placeholder="Paste the quote folder path" />
        <div>
          <button className="vq-button" type="button" disabled={busy} onClick={() => { setAddingLocation(false); setError(null) }}>Cancel</button>
          <button className="vq-button vq-button-primary" type="submit" disabled={busy || disabled || !draft.trim()}>Add</button>
        </div>
      </form>}
    </div>
    {error && <p className="vq-error" role="alert">{error}</p>}
    {deletePath && <Modal title="Remove file location?" subtitle="This removes the path from Arda. It does not delete the folder or any files." onClose={() => setDeletePath(null)}><p className="qs-file-confirm-path">{deletePath}</p><footer className="vq-modal-footer"><button className="vq-button" type="button" onClick={() => setDeletePath(null)}>Cancel</button><button className="vq-button vq-button-danger" type="button" disabled={busy} onClick={() => void apply(removeQuoteFileLocation(detail.quote.quoteHistoryId, detail.quote.version, deletePath))}>Remove location</button></footer></Modal>}
    {collision && <Modal title="A file with this name already exists" subtitle={collision.file.name} onClose={() => setCollision(null)}><p className="qs-file-collision-copy">Choose whether Arda should replace the existing file or keep both files with a renamed copy.</p><footer className="vq-modal-footer"><button className="vq-button" type="button" onClick={() => setCollision(null)}>Cancel</button><button className="vq-button" type="button" disabled={busy} onClick={() => void upload(collision.path, collision.file, 'rename')}>Keep both</button><button className="vq-button vq-button-danger" type="button" disabled={busy} onClick={() => void upload(collision.path, collision.file, 'overwrite')}>Overwrite</button></footer></Modal>}
  </div>
}
