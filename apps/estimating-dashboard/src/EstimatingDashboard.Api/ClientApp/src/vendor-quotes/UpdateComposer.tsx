import { Check, LockKeyhole, MessageSquare, Plus, Save } from 'lucide-react'
import { useEffect, useState } from 'react'
import type { UpdateDraft } from './model'

export default function UpdateComposer({ label, draft, onChange, statuses, canEdit, saving, error, saved, onSave, kind = 'thread' }: {
  label: string; draft: UpdateDraft; onChange: (draft: UpdateDraft) => void; statuses: string[]
  canEdit: boolean; saving: boolean; error: string | null; saved: boolean; onSave: () => void
  kind?: 'quote' | 'thread'
}) {
  const [expanded, setExpanded] = useState(false)
  useEffect(() => { if (saved && kind === 'quote') setExpanded(false) }, [saved, kind])
  if (kind === 'quote' && canEdit && !expanded && !draft.note) return <button className="qs-add-update" type="button" onClick={() => setExpanded(true)}><span><MessageSquare size={18} /></span><div><strong>{saved ? 'Update saved. Add another…' : 'Add an internal update…'}</strong><small>Keep decisions, follow-ups, and the latest news in the record.</small></div><Plus size={18} /></button>
  return <section className="vq-composer" aria-label={`Update ${label}`}>
    <header><div><span className="vq-eyebrow">INTERNAL UPDATE</span><h3>{label}</h3></div><LockKeyhole size={16} /></header>
    {!canEdit ? <p className="vq-read-only">You can read this record. Editing requires access to manage this quote.</p> : <>
      <div className={`vq-composer-fields${kind === 'quote' ? ' qs-follow-up-only' : ''}`}>{kind === 'thread' && <label>Status<select value={draft.status} disabled={saving} onChange={event => onChange({ ...draft, status: event.target.value })}>{statuses.map(status => <option key={status}>{status}</option>)}</select></label>}<label>Follow up on<input type="date" value={draft.followUpDate} disabled={saving} onChange={event => onChange({ ...draft, followUpDate: event.target.value })} /></label></div>
      <label className="vq-note-label" htmlFor="quote-status-note">Note <span>Optional</span></label>
      <textarea id="quote-status-note" placeholder="What changed? Capture a vendor update, decision, or next step…" maxLength={4000} rows={expanded || draft.note ? 4 : 2} value={draft.note} disabled={saving} onFocus={() => setExpanded(true)} onChange={event => onChange({ ...draft, note: event.target.value })} />
      {error && <p className="vq-error" role="alert">{error}</p>}
      <footer><span className="vq-composer-hint">{saved ? <><Check size={14} /> Update saved</> : 'Shared in Arda · never emailed'}</span><button type="button" className="vq-button vq-button-primary" disabled={saving} onClick={onSave}><Save size={15} />{saving ? 'Saving…' : 'Save update'}</button></footer>
    </>}
  </section>
}
