import { GitBranch, Plus } from 'lucide-react'
import { useEffect, useState } from 'react'
import Modal from './Modal'
import { createVendorRequest, updateVendorRequest } from './api'
import type { QuoteStatusSummary, VendorDetail } from './types'

export default function NewThreadDialog({ quote, statuses, onClose, onCreated, existing }: {
  quote: QuoteStatusSummary; statuses: string[]; onClose: () => void; onCreated: (detail: VendorDetail) => void
  existing?: VendorDetail
}) {
  const initial = existing?.request
  const [part, setPart] = useState(initial?.partNumber || '')
  const [vendor, setVendor] = useState(initial?.vendorName || '')
  const [email, setEmail] = useState(initial?.vendorEmail || '')
  const [title, setTitle] = useState(initial?.title || '')
  const [status, setStatus] = useState(initial?.status || statuses[0] || 'Untouched')
  const [note, setNote] = useState('')
  const [followUp, setFollowUp] = useState(initial?.followUpDate?.slice(0, 10) || '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [discarding, setDiscarding] = useState(false)
  const dirty = part !== (initial?.partNumber || '') || vendor !== (initial?.vendorName || '') || email !== (initial?.vendorEmail || '') || title !== (initial?.title || '') || Boolean(note) || followUp !== (initial?.followUpDate?.slice(0, 10) || '') || status !== (initial?.status || statuses[0] || 'Untouched')
  useEffect(() => {
    function beforeUnload(event: BeforeUnloadEvent) { if (dirty || saving) { event.preventDefault(); event.returnValue = '' } }
    window.addEventListener('beforeunload', beforeUnload)
    return () => window.removeEventListener('beforeunload', beforeUnload)
  }, [dirty, saving])
  function close() { if (saving) return; if (dirty) setDiscarding(true); else onClose() }
  return <Modal title={`${existing ? 'Edit' : 'New'} thread · Quote ${quote.quoteNumber}`} subtitle="Track a part, a vendor request, or another item independently." onClose={close}>
    <form className="vq-thread-form" onSubmit={async event => {
      event.preventDefault(); setSaving(true); setError(null)
      try {
        const fields = { quoteHistoryId: quote.quoteHistoryId, vendorName: vendor.trim(), vendorEmail: email.trim(), title: title.trim(), status, followUpDate: followUp || null, note: note.trim() || null, partNumber: part.trim() }
        const result = existing ? await updateVendorRequest(existing.request.id, { ...fields, expectedVersion: existing.request.version }) : await createVendorRequest(fields)
        onCreated(result)
      } catch (reason) { setError(reason instanceof Error ? reason.message : 'The thread could not be created.') } finally { setSaving(false) }
    }}>
      <div className="vq-quote-context"><GitBranch size={17} /><strong>Quote {quote.quoteNumber}</strong><span>{quote.customer}</span></div>
      <label>Thread name<input autoFocus required maxLength={240} placeholder="e.g. Silicone material pricing" value={title} disabled={saving} onChange={event => setTitle(event.target.value)} /></label>
      <div className="vq-form-row"><label>Part number <small>Optional</small><input maxLength={160} placeholder="e.g. SA-1042" value={part} disabled={saving} onChange={event => setPart(event.target.value)} /></label><label>Vendor or contact <small>Optional</small><input maxLength={200} placeholder="e.g. SiliconePrime" value={vendor} disabled={saving} onChange={event => setVendor(event.target.value)} /></label></div>
      <label>Contact email <small>Optional</small><input type="email" maxLength={254} placeholder="quotes@vendor.com" value={email} disabled={saving} onChange={event => setEmail(event.target.value)} /><span className="vq-field-hint">Used to match incoming quote emails. Leave blank for an internal thread.</span></label>
      <div className="vq-form-row"><label>Status<select value={status} disabled={saving} onChange={event => setStatus(event.target.value)}>{statuses.map(item => <option key={item}>{item}</option>)}</select></label><label>Follow up on<input type="date" value={followUp} disabled={saving} onChange={event => setFollowUp(event.target.value)} /></label></div>
      <label>{existing ? 'Note about this change' : 'Initial note'} <small>Optional</small><textarea rows={3} maxLength={4000} placeholder="What should the team know?" value={note} disabled={saving} onChange={event => setNote(event.target.value)} /></label>
      {error && <p className="vq-error" role="alert">{error}</p>}
      {discarding && <div className="vq-discard-inline" role="alert"><p>Discard this unsaved thread?</p><button type="button" className="vq-button" onClick={() => setDiscarding(false)}>Keep editing</button><button type="button" className="vq-button" onClick={onClose}>Discard</button></div>}
      <footer className="vq-modal-footer"><button type="button" className="vq-button" onClick={close} disabled={saving}>Cancel</button><button type="submit" className="vq-button vq-button-primary" disabled={saving || !title.trim()}><Plus size={16} />{saving ? 'Saving…' : existing ? 'Save thread' : 'Create thread'}</button></footer>
    </form>
  </Modal>
}
