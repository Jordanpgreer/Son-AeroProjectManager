import { Save } from 'lucide-react'
import { useRef, useState } from 'react'
import Modal from './Modal'
import { editNote } from './lifecycleApi'
import { dateTime } from './model'
import type { QuoteActivity, QuoteStatusDetail, VendorActivity } from './types'

export default function EditNoteDialog({ detail, note, activityId, onClose, onChanged, onBusy }: {
  detail: QuoteStatusDetail; note: QuoteActivity | VendorActivity; activityId: string
  onClose: () => void; onChanged: (detail: QuoteStatusDetail) => Promise<void>; onBusy: (busy: boolean) => void
}) {
  const expectedVersion = useRef(detail.quote.version).current
  const [text, setText] = useState(note.text)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [discarding, setDiscarding] = useState(false)
  const dirty = text !== note.text
  function close() { if (saving) return; if (dirty) setDiscarding(true); else onClose() }
  return <>
    <Modal title="Edit internal note" subtitle="Correct or clarify this note. Its edit history remains in the quote record." onClose={close}>
      <form className="qs-lifecycle-form" onSubmit={async event => {
        event.preventDefault(); if (!text.trim() || !dirty) return
        setSaving(true); onBusy(true); setError(null)
        try { const updated = await editNote(detail.quote.quoteHistoryId, activityId, expectedVersion, text.trim()); await onChanged(updated); onClose() }
        catch (reason) { setError(reason instanceof Error ? reason.message : 'The note could not be saved.') }
        finally { setSaving(false); onBusy(false) }
      }}><p className="qs-note-origin">{note.displayName || note.accountName} · {dateTime(note.occurredAt)}</p><label>Internal note<textarea autoFocus value={text} rows={7} maxLength={4000} disabled={saving} onChange={event => setText(event.target.value)} /></label>{error && <p className="vq-error" role="alert">{error}</p>}<footer className="vq-modal-footer"><button type="button" className="vq-button" disabled={saving} onClick={close}>Cancel</button><button type="submit" className="vq-button vq-button-primary" disabled={saving || !text.trim() || !dirty}><Save size={16} />{saving ? 'Saving…' : 'Save note'}</button></footer></form>
    </Modal>
    {discarding && <Modal title="Discard your note edits?" subtitle="The original note is unchanged until you save." onClose={() => setDiscarding(false)}><footer className="vq-modal-footer"><button className="vq-button" onClick={onClose}>Discard changes</button><button className="vq-button vq-button-primary" onClick={() => setDiscarding(false)}>Keep editing</button></footer></Modal>}
  </>
}
