import { ArrowDownLeft, ArrowUpRight, CheckCircle2, FileText, LoaderCircle, Paperclip, Upload } from 'lucide-react'
import { useEffect, useState } from 'react'
import Modal from './Modal'
import { dateTime } from './model'
import { importManualEmail, previewManualEmail } from './manualEmailApi'
import { emailThreadMatches, preferredEmailDirection, type EmailDirection } from './manualEmailModel'
import type { ManualEmailImportResult, ManualEmailPreview, QuoteStatusDetail, VendorDetail } from './types'

export default function ManualEmailReview({ file, detail, selectedThread, onClose, onImported, onCommitted }: {
  file: File; detail: QuoteStatusDetail; selectedThread: VendorDetail | null; onClose: () => void
  onImported: (detail: QuoteStatusDetail) => Promise<void>
  onCommitted: () => void
}) {
  const [preview, setPreview] = useState<ManualEmailPreview | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [direction, setDirection] = useState<EmailDirection>('')
  const [recipient, setRecipient] = useState('')
  const [requestId, setRequestId] = useState<number | null>(selectedThread?.request.id || null)
  const [note, setNote] = useState('')
  const [saving, setSaving] = useState(false)
  const [result, setResult] = useState<ManualEmailImportResult | null>(null)
  const [discarding, setDiscarding] = useState(false)
  const [refreshFailed, setRefreshFailed] = useState(false)
  const chosen = detail.threads.find(item => item.request.id === requestId)?.request || null
  const matches = preview ? emailThreadMatches(chosen, preview, direction, recipient) : true
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null)
    void previewManualEmail(detail.quote.quoteHistoryId, file, controller.signal).then(parsed => {
      if (controller.signal.aborted) return
      setPreview(parsed)
      setDirection(preferredEmailDirection(parsed, selectedThread?.request))
      const preferred = parsed.toAddresses.find(address => address.toLowerCase() === selectedThread?.request.vendorEmail.toLowerCase())
      setRecipient(preferred || (parsed.toAddresses.length === 1 ? parsed.toAddresses[0] : ''))
    }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'This email could not be read.') }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [detail.quote.quoteHistoryId, file, selectedThread?.request.id, selectedThread?.request.vendorEmail, attempt])
  function close() { if (saving) return; if (note.trim() && !result) setDiscarding(true); else onClose() }
  return <>
    <Modal title={result ? result.outcome === 'duplicate' ? 'Email already attached' : 'Email attached' : 'Review email'} subtitle={result ? `Quote ${detail.quote.quoteNumber} · ${detail.quote.customer}` : 'Review the message and choose where it belongs before adding it to the record.'} onClose={close}>
      {loading ? <div className="qs-upload-loading" role="status"><LoaderCircle className="vq-spinner" size={28} /><strong>Reading your email…</strong><span>{file.name}</span></div> : result ? <div className="qs-upload-result"><CheckCircle2 size={38} /><h3>{result.outcome === 'duplicate' ? 'No duplicate was added' : 'Added to the quote record'}</h3><p>{result.message}</p><div><FileText size={16} /><span>{preview?.subject || file.name}</span></div>{result.outcome === 'duplicate' && note.trim() && <label className="qs-upload-skipped-note">Your note was not added<textarea readOnly rows={3} value={note} /><small>You can copy it into an internal update if needed.</small></label>}{refreshFailed && <p className="vq-error" role="alert">The email is saved, but the record could not be refreshed.</p>}<footer className="vq-modal-footer">{refreshFailed && <button className="vq-button" disabled={saving} onClick={async () => { setSaving(true); try { await onImported(result.detail); setRefreshFailed(false) } catch { setRefreshFailed(true) } finally { setSaving(false) } }}>Refresh record</button>}<button className="vq-button vq-button-primary" onClick={onClose}>Done</button></footer></div> : <form className="qs-email-review" onSubmit={async event => {
        event.preventDefault()
        if (!preview || !direction || (direction === 'outgoing' && !recipient) || !matches) return
        setSaving(true); setError(null)
        try {
          const imported = await importManualEmail(detail.quote.quoteHistoryId, file, { expectedVersion: detail.quote.version, requestId, direction, vendorEmail: direction === 'outgoing' ? recipient : preview.fromAddress, note })
          setResult(imported)
          onCommitted()
          try { await onImported(imported.detail) } catch { setRefreshFailed(true) }
        } catch (reason) { setError(reason instanceof Error ? reason.message : 'The email could not be attached.') } finally { setSaving(false) }
      }}>
        {error && <p className="vq-error" role="alert">{error}</p>}
        {!preview && <div className="qs-upload-fallback"><p>Save the message from Outlook as an .msg or .eml file and try again.</p><button type="button" className="vq-button" onClick={() => setAttempt(value => value + 1)}>Try reading again</button></div>}
        {preview && <>
          <section className="qs-email-preview"><span className="vq-eyebrow">ORIGINAL EMAIL</span><h3>{preview.subject || '(No subject)'}</h3><dl><dt>From</dt><dd>{preview.fromName ? `${preview.fromName} <${preview.fromAddress}>` : preview.fromAddress}</dd><dt>To</dt><dd>{preview.toAddresses.join(', ') || 'No recipient recorded'}</dd><dt>Sent</dt><dd>{dateTime(preview.sentAt)}</dd>{preview.receivedAt && <><dt>Received</dt><dd>{dateTime(preview.receivedAt)}</dd></>}</dl><div><Paperclip size={14} />{preview.attachmentCount} attachment{preview.attachmentCount === 1 ? '' : 's'}<span>{file.name}</span></div></section>
          <fieldset className="qs-email-direction" disabled={saving}><legend>This email was</legend><label className={direction === 'incoming' ? 'is-selected' : ''}><input type="radio" name="manual-email-direction" value="incoming" checked={direction === 'incoming'} onChange={() => setDirection('incoming')} /><ArrowDownLeft size={17} /><span>Received</span></label><label className={direction === 'outgoing' ? 'is-selected' : ''}><input type="radio" name="manual-email-direction" value="outgoing" checked={direction === 'outgoing'} onChange={() => setDirection('outgoing')} /><ArrowUpRight size={17} /><span>Sent</span></label></fieldset>
          {direction === 'outgoing' && <label>Contact for this sent email<select value={recipient} disabled={saving} onChange={event => setRecipient(event.target.value)}><option value="">Choose a recipient…</option>{preview.toAddresses.map(address => <option key={address}>{address}</option>)}</select><small>Choose the contact whose conversation this email belongs to.</small></label>}
          <label>Attach to<select value={requestId || ''} disabled={saving} onChange={event => setRequestId(event.target.value ? Number(event.target.value) : null)}><option value="">Quote {detail.quote.quoteNumber} · choose a thread later</option>{detail.threads.map(item => <option key={item.request.id} value={item.request.id}>{item.request.partNumber ? `${item.request.partNumber} · ` : ''}{item.request.vendorName || item.request.title}</option>)}</select><small>Quote {detail.quote.quoteNumber} · {detail.quote.customer}. The original subject and message dates are kept.</small></label>
          {direction && !matches && <p className="vq-error">This thread has a different contact. Choose the matching contact or attach the email to the quote.</p>}
          <label>Internal note <small>Optional</small><textarea rows={3} maxLength={4000} value={note} disabled={saving} placeholder="Add context or a next step for your team…" onChange={event => setNote(event.target.value)} /><small>Added to the activity record. This note is never emailed.</small></label>
        </>}
        <footer className="vq-modal-footer"><button className="vq-button" type="button" disabled={saving} onClick={close}>Cancel</button>{preview && <button className="vq-button vq-button-primary" type="submit" disabled={saving || !direction || (direction === 'outgoing' && !recipient) || !matches}>{saving ? <LoaderCircle className="vq-spinner" size={16} /> : <Upload size={16} />}{saving ? 'Attaching…' : 'Attach email'}</button>}</footer>
      </form>}
    </Modal>
    {discarding && <Modal title="Discard your email note?" subtitle="This email and its internal note have not been added to Arda." onClose={() => setDiscarding(false)}><footer className="vq-modal-footer"><button className="vq-button" onClick={onClose}>Discard</button><button className="vq-button vq-button-primary" onClick={() => setDiscarding(false)}>Keep reviewing</button></footer></Modal>}
  </>
}
