import { ArrowRight, ArrowRightLeft, LoaderCircle, Search } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import Modal from './Modal'
import { loadQuoteStatusDetail, loadQuoteStatuses } from './api'
import { moveEmail } from './lifecycleApi'
import { matchingMoveThreads } from './lifecycleModel'
import type { QuoteStatusDetail, QuoteStatusSummary, VendorMessage } from './types'

export default function MoveEmailDialog({ detail, message, onClose, onChanged, onBusy }: {
  detail: QuoteStatusDetail; message: VendorMessage; onClose: () => void
  onChanged: (detail: QuoteStatusDetail) => Promise<void>; onBusy: (busy: boolean) => void
}) {
  const sourceVersion = useRef(detail.quote.version).current
  const sourceRequestId = detail.threads.find(thread => thread.messages.some(item => item.id === message.id))?.request.id || null
  const [search, setSearch] = useState('')
  const [options, setOptions] = useState<QuoteStatusSummary[]>([])
  const [target, setTarget] = useState<QuoteStatusDetail | null>(detail)
  const [targetId, setTargetId] = useState(detail.quote.quoteHistoryId)
  const [requestId, setRequestId] = useState<number | null>(null)
  const [searching, setSearching] = useState(false)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const targets = target ? matchingMoveThreads(message, target.threads.map(thread => thread.request)) : []
  const different = targetId !== detail.quote.quoteHistoryId || requestId !== sourceRequestId
  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      setSearching(true)
      void loadQuoteStatuses(search, '', null, 1, controller.signal).then(page => { if (!controller.signal.aborted) setOptions(page.items.filter(quote => quote.canEdit)) }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Quotes could not be searched.') }).finally(() => { if (!controller.signal.aborted) setSearching(false) })
    }, 220)
    return () => { clearTimeout(timer); controller.abort() }
  }, [search])
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null); setTarget(null); setRequestId(null)
    void loadQuoteStatusDetail(targetId, controller.signal).then(result => { if (!controller.signal.aborted) setTarget(result) }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'This destination could not be loaded.') }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [targetId])
  return <Modal title="Move email" subtitle="Keep the message with the right quote and thread. Its original Outlook email stays untouched." onClose={() => { if (!saving) onClose() }}>
    <form className="qs-lifecycle-form" onSubmit={async event => {
      event.preventDefault()
      if (!target || !target.quote.canEdit || !different || loading) return
      setSaving(true); onBusy(true); setError(null)
      try {
        const updated = await moveEmail(detail.quote.quoteHistoryId, message.id, { expectedVersion: sourceVersion, targetQuoteHistoryId: target.quote.quoteHistoryId, targetExpectedVersion: target.quote.version, requestId })
        await onChanged(updated); onClose()
      } catch (reason) { setError(reason instanceof Error ? reason.message : 'The email could not be moved.') } finally { setSaving(false); onBusy(false) }
    }}>
      <div className="qs-record-context"><strong>{message.subject || '(No subject)'}</strong><span>{message.vendorEmail || message.fromAddress}</span><small>Currently on Quote {detail.quote.quoteNumber}</small></div>
      <label>Find a destination quote<span className="qs-move-search"><Search size={16} /><input value={search} disabled={saving} placeholder="Search quote number or customer…" onChange={event => setSearch(event.target.value)} /></span></label>
      <div className="qs-move-options" aria-label="Destination quotes">{searching ? <p role="status">Searching quotes…</p> : options.length ? options.map(quote => <button type="button" key={quote.quoteHistoryId} disabled={saving} aria-pressed={targetId === quote.quoteHistoryId} onClick={() => setTargetId(quote.quoteHistoryId)}><span><strong>Quote {quote.quoteNumber}</strong><small>{quote.customer}</small></span><ArrowRight size={15} /></button>) : <p>No editable quotes match this search.</p>}</div>
      <section className="qs-move-destination" aria-label="Selected destination">{loading ? <p role="status"><LoaderCircle className="vq-spinner" size={16} />Loading destination…</p> : target && <><strong>To Quote {target.quote.quoteNumber}</strong><span>{target.quote.customer}</span>{target.quote.canEdit ? <label>Place within this quote<select value={requestId || ''} disabled={saving} onChange={event => setRequestId(event.target.value ? Number(event.target.value) : null)}><option value="">Quote record · no thread</option>{targets.map(thread => <option key={thread.id} value={thread.id}>{thread.partNumber ? `${thread.partNumber} · ` : ''}{thread.vendorName || thread.title}</option>)}</select><small>Threads for the same contact and internal threads are available.</small></label> : <p className="vq-error">You no longer have permission to edit this destination quote.</p>}</>}</section>
      {error && <p className="vq-error" role="alert">{error}</p>}
      <footer className="vq-modal-footer"><button className="vq-button" type="button" disabled={saving} onClick={onClose}>Cancel</button><button className="vq-button vq-button-primary" disabled={saving || loading || !target?.quote.canEdit || !different}>{saving ? <LoaderCircle className="vq-spinner" size={16} /> : <ArrowRightLeft size={16} />}{saving ? 'Moving…' : 'Move email'}</button></footer>
    </form>
  </Modal>
}
