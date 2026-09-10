import { CheckCircle2, RotateCcw, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import EditNoteDialog from './EditNoteDialog'
import MoveEmailDialog from './MoveEmailDialog'
import RemoveRecordDialog from './RemoveRecordDialog'
import { removeEmail, removeNote, restoreEmail, restoreNote } from './lifecycleApi'
import { editableNoteId } from './lifecycleModel'
import type { QuoteActivity, QuoteStatusDetail, VendorActivity, VendorMessage } from './types'
import './record-lifecycle.css'

type Note = QuoteActivity | VendorActivity
type Action = { kind: 'move-email' | 'remove-email'; message: VendorMessage; version: number }
  | { kind: 'edit-note' | 'remove-note'; note: Note; id: string; version: number }
type Undo = { kind: 'email'; id: number } | { kind: 'note'; id: string }

export default function useRecordLifecycle({ detail, resetToken, canRemove, externalBusy, onChanged, onMoved }: {
  detail: QuoteStatusDetail; resetToken: number; canRemove: boolean; externalBusy: boolean
  onChanged: (detail: QuoteStatusDetail) => Promise<void>; onMoved: () => void
}) {
  const [action, setAction] = useState<Action | null>(null)
  const [busy, setBusy] = useState(false)
  const [undo, setUndo] = useState<Undo | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setAction(null) }, [resetToken])
  const close = () => setAction(null)
  function openNote(kind: 'edit-note' | 'remove-note', note: Note) {
    const id = editableNoteId(note)
    if (id) setAction({ kind, note, id, version: detail.quote.version })
  }
  async function restore(item: Undo) {
    if (busy || externalBusy || !canRemove) return
    setBusy(true); setError(null)
    try {
      const updated = item.kind === 'email' ? await restoreEmail(detail.quote.quoteHistoryId, item.id, detail.quote.version) : await restoreNote(detail.quote.quoteHistoryId, item.id, detail.quote.version)
      await onChanged(updated)
      if (undo?.kind === item.kind && undo.id === item.id) setUndo(null)
      setNotice(item.kind === 'email' ? 'Email restored to the active record.' : 'Internal note restored to the activity record.')
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'This item could not be restored.') }
    finally { setBusy(false) }
  }
  const alerts = <>
    {undo && <div className="qs-undo-banner" role="status"><CheckCircle2 size={17} /><span>{undo.kind === 'email' ? 'Email' : 'Internal note'} removed from Arda. You can also restore it from Removed items.</span><button className="qs-text-button" disabled={busy || externalBusy || !canRemove} onClick={() => void restore(undo)}><RotateCcw size={14} />Undo</button><button className="vq-icon-button" aria-label="Dismiss undo message" onClick={() => setUndo(null)}><X size={15} /></button></div>}
    {notice && <div className="qs-action-notice" role="status"><CheckCircle2 size={15} /><span>{notice}</span><button className="vq-icon-button" aria-label="Dismiss update message" onClick={() => setNotice(null)}><X size={14} /></button></div>}
    {error && <p className="vq-error" role="alert">{error}</p>}
  </>
  let dialog = null
  if (action?.kind === 'move-email') dialog = <MoveEmailDialog detail={detail} message={action.message} onClose={close} onBusy={setBusy} onChanged={async updated => { await onChanged(updated); onMoved(); setNotice('Email moved. Both quote records retain the move history.') }} />
  if (action?.kind === 'edit-note') dialog = <EditNoteDialog detail={detail} note={action.note} activityId={action.id} onClose={close} onBusy={setBusy} onChanged={async updated => { await onChanged(updated); setNotice('Internal note updated. Its edit history is retained.') }} />
  if (action?.kind === 'remove-email') dialog = <RemoveRecordDialog kind="email" label={action.message.subject || '(No subject)'} onClose={close} onBusy={setBusy} onRemove={() => removeEmail(detail.quote.quoteHistoryId, action.message.id, action.version)} onChanged={async updated => { await onChanged(updated); setUndo({ kind: 'email', id: action.message.id }); setNotice(null) }} />
  if (action?.kind === 'remove-note') dialog = <RemoveRecordDialog kind="note" label={action.note.text} onClose={close} onBusy={setBusy} onRemove={() => removeNote(detail.quote.quoteHistoryId, action.id, action.version)} onChanged={async updated => { await onChanged(updated); setUndo({ kind: 'note', id: action.id }); setNotice(null) }} />
  return {
    dirty: Boolean(action) || busy, busy, alerts, dialog,
    moveEmail: (message: VendorMessage) => setAction({ kind: 'move-email', message, version: detail.quote.version }),
    removeEmail: (message: VendorMessage) => setAction({ kind: 'remove-email', message, version: detail.quote.version }),
    editNote: (note: Note) => openNote('edit-note', note), removeNote: (note: Note) => openNote('remove-note', note),
    restoreEmail: (id: number) => void restore({ kind: 'email', id }), restoreNote: (id: string) => void restore({ kind: 'note', id }),
  }
}
