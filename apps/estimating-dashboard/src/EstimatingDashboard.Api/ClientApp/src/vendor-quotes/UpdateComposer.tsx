import { Check, LockKeyhole, Plus } from 'lucide-react'
import { useId } from 'react'
import type { UpdateDraft } from './model'

export default function UpdateComposer({ label, draft, onChange, statuses, canEdit, saving, error, saved, onSave, canSave = true, compact = false, kind = 'thread' }: {
  label: string; draft: UpdateDraft; onChange: (draft: UpdateDraft) => void; statuses: string[]
  canEdit: boolean; saving: boolean; error: string | null; saved: boolean; onSave: () => void
  kind?: 'quote' | 'thread'
  canSave?: boolean
  compact?: boolean
}) {
  const noteId = useId()
  return <section className={`vq-composer qs-entry-composer${compact ? ' is-compact' : ''}`} aria-label={`Add entry to ${label}`}>
    {!compact && <header><h3>Add Entry</h3>{!canEdit && <LockKeyhole size={16} />}</header>}
    {!canEdit ? <p className="vq-read-only">You can read this record. Editing requires access to manage this quote.</p> : <>
      <label className="qs-entry-status">{kind === 'quote' ? 'Quote status' : 'RFQ status'}<select value={draft.status} disabled={saving} onChange={event => onChange({ ...draft, status: event.target.value })}>{statuses.map(status => <option key={status}>{status}</option>)}</select></label>
      <label className="vq-note-label" htmlFor={noteId}>Note <span>{compact ? 'Optional' : 'Optional when changing status'}</span></label>
      <textarea id={noteId} placeholder="Add a note, vendor response, or pricing reference…" maxLength={4000} rows={compact ? 2 : 3} value={draft.note} disabled={saving} onChange={event => onChange({ ...draft, note: event.target.value })} />
      <details className="qs-entry-options"><summary>{draft.followUpDate ? 'Follow-up date set' : 'Add a follow-up date'}</summary><label>Follow up on<input type="date" value={draft.followUpDate} disabled={saving} onChange={event => onChange({ ...draft, followUpDate: event.target.value })} /></label></details>
      {error && <p className="vq-error" role="alert">{error}</p>}
      <footer><span className="vq-composer-hint" role="status">{saved ? <><Check size={14} /> Entry saved</> : compact ? 'Internal update' : 'Shared with your estimating team'}</span><button type="button" className="vq-button vq-button-primary" disabled={saving || !canSave} onClick={onSave}><Plus size={15} />{saving ? 'Saving…' : 'Save Entry'}</button></footer>
    </>}
  </section>
}
