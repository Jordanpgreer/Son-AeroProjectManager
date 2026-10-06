import { FileUp, FolderOpen, Pencil, Plus, Trash2 } from 'lucide-react'
import { useRef, useState } from 'react'
import { copyQuoteFileToLocation, QuoteApiError, removeQuoteFileLocation, saveQuoteFileLocation } from './api'
import Modal from './Modal'
import QuoteFolderActions from './QuoteFolderActions'
import type { QuoteStatusDetail } from './types'

const acceptedFiles = '.pdf,.msg,.eml,.xls,.xlsx,.xlsm,.doc,.docx'

export default function QuoteFileLocations({ detail, canEdit, disabled, onChanged }: {
  detail: QuoteStatusDetail; canEdit: boolean; disabled: boolean; onChanged: (detail: QuoteStatusDetail) => Promise<void>
}) {
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [draft, setDraft] = useState('S:\\')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [deletePath, setDeletePath] = useState<string | null>(null)
  const [collision, setCollision] = useState<{ path: string; file: File } | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const [uploadPath, setUploadPath] = useState<string | null>(null)
  const locations = detail.fileLocations ?? (detail.quoteFolderPath ? [{ path: detail.quoteFolderPath, source: 'Fulcrum' as const }] : [])

  async function apply(next: Promise<QuoteStatusDetail>) {
    setBusy(true); setError(null)
    try { await onChanged(await next); setEditing(null); setDeletePath(null) }
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

  function chooseFile(path: string) {
    setUploadPath(path)
    input.current?.click()
  }

  return <div className="qs-file-locations">
    <div className="qs-file-location-heading"><strong>File Location</strong>{canEdit && <button type="button" className="qs-text-button" disabled={busy || disabled} onClick={() => { setDraft('S:\\'); setEditing('new'); setError(null) }}><Plus size={12} />Add location</button>}</div>
    {locations.length === 0 && editing !== 'new' && <p className="qs-file-location-empty">No file location was found in Fulcrum internal notes.</p>}
    <div className="qs-file-location-list">
      {locations.map(location => <div className="qs-file-location" key={location.path}
        onDragOver={event => { if (canEdit && !busy && !disabled) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' } }}
        onDrop={event => { if (!canEdit || busy || disabled) return; event.preventDefault(); const file = event.dataTransfer.files.item(0); if (file) void upload(location.path, file) }}>
        {editing === location.path ? <div className="qs-file-location-editor"><input autoFocus aria-label="Edit file location" value={draft} disabled={busy} onChange={event => setDraft(event.target.value)} /><div><button className="vq-button" type="button" disabled={busy} onClick={() => setEditing(null)}>Cancel</button><button className="vq-button vq-button-primary" type="button" disabled={busy || !draft.trim()} onClick={() => void apply(saveQuoteFileLocation(detail.quote.quoteHistoryId, detail.quote.version, draft, location.path))}>Save</button></div></div> : <>
          <div className="qs-file-location-name" title={location.path}><FolderOpen size={16} /><span><strong>{location.path.split('\\').filter(Boolean).at(-1) || 'Quote folder'}</strong><small>{location.source === 'Fulcrum' ? 'Found in Fulcrum notes' : 'Added in Arda'}</small></span></div>
          <div className="qs-file-location-actions"><QuoteFolderActions path={location.path} quoteNumber={detail.quote.quoteNumber} />{canEdit && <><button type="button" className="qs-record-action" disabled={busy || disabled} onClick={() => chooseFile(location.path)}><FileUp size={14} />Copy file</button><button type="button" className="vq-icon-button" disabled={busy || disabled} aria-label={`Edit ${location.path}`} onClick={() => { setDraft(location.path); setEditing(location.path); setError(null) }}><Pencil size={14} /></button><button type="button" className="vq-icon-button is-destructive" disabled={busy || disabled} aria-label={`Delete ${location.path}`} onClick={() => setDeletePath(location.path)}><Trash2 size={14} /></button></>}</div>
          {canEdit && <small className="qs-file-drop-hint">Drop a PDF, Outlook email, Excel sheet, or Word document here to copy it into this folder.</small>}
        </>}
      </div>)}
      {editing === 'new' && <div className="qs-file-location qs-file-location-editor"><input autoFocus aria-label="New file location" value={draft} disabled={busy} onChange={event => setDraft(event.target.value)} placeholder="S:\\Estimating\\Quotes\\..." /><div><button className="vq-button" type="button" disabled={busy} onClick={() => setEditing(null)}>Cancel</button><button className="vq-button vq-button-primary" type="button" disabled={busy || !draft.trim()} onClick={() => void apply(saveQuoteFileLocation(detail.quote.quoteHistoryId, detail.quote.version, draft))}>Add</button></div></div>}
    </div>
    <input ref={input} hidden type="file" accept={acceptedFiles} onChange={event => { const file = event.currentTarget.files?.[0]; if (file && uploadPath) void upload(uploadPath, file); event.currentTarget.value = '' }} />
    {error && <p className="vq-error" role="alert">{error}</p>}
    {deletePath && <Modal title="Remove file location?" subtitle="This removes the path from Arda. It does not delete the folder or any files." onClose={() => setDeletePath(null)}><p className="qs-file-confirm-path">{deletePath}</p><footer className="vq-modal-footer"><button className="vq-button" type="button" onClick={() => setDeletePath(null)}>Cancel</button><button className="vq-button vq-button-danger" type="button" disabled={busy} onClick={() => void apply(removeQuoteFileLocation(detail.quote.quoteHistoryId, detail.quote.version, deletePath))}>Remove location</button></footer></Modal>}
    {collision && <Modal title="A file with this name already exists" subtitle={collision.file.name} onClose={() => setCollision(null)}><p className="qs-file-collision-copy">Choose whether Arda should replace the existing file or keep both files with a renamed copy.</p><footer className="vq-modal-footer"><button className="vq-button" type="button" onClick={() => setCollision(null)}>Cancel</button><button className="vq-button" type="button" disabled={busy} onClick={() => void upload(collision.path, collision.file, 'rename')}>Keep both</button><button className="vq-button vq-button-danger" type="button" disabled={busy} onClick={() => void upload(collision.path, collision.file, 'overwrite')}>Overwrite</button></footer></Modal>}
  </div>
}
