import { ArrowUpRight, Link2, Plus, X } from 'lucide-react'
import { useEffect, useRef } from 'react'
import type { DashboardQuote } from './quoteDashboardApi'
import type { QuoteRecord } from './quoteStore'
import { currency, formatOptionalDate } from './quoteDashboardFormatting'
import { quoteStatusUrl } from './estimatingNavigation'

export default function QuoteDashboardDetail({ quote, linkedQuotes, canGenerate, storageError, onClose, onGenerate }: {
  quote: DashboardQuote; linkedQuotes: QuoteRecord[]; canGenerate: boolean; storageError: boolean
  onClose: () => void; onGenerate: () => void
}) {
  const closeButton = useRef<HTMLButtonElement>(null)
  useEffect(() => { closeButton.current?.focus() }, [quote.id])
  return <aside className="qd-detail" aria-labelledby="quote-detail-title" onKeyDown={event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose() }
  }}>
    <header><div><span>Quote details</span><h2 id="quote-detail-title">#{quote.quoteNumber}</h2></div><button ref={closeButton} type="button" className="qd-close" aria-label="Close quote details" onClick={onClose}><X size={19} /></button></header>
    <p className="qd-detail-customer">{quote.customer || 'Customer not recorded'}</p>
    <dl>
      <div><dt>Part number</dt><dd>{quote.partNumbers.length ? quote.partNumbers.join(', ') : 'Not available'}</dd></div>
      <div><dt>Estimator</dt><dd>{quote.estimatingRep || 'Unassigned'}</dd></div>
      <div><dt>Arda status</dt><dd><span className="qd-status" data-status={quote.ardaStatus ?? 'Untouched'}><i aria-hidden="true" />{quote.ardaStatus ?? 'Untouched'}</span></dd></div>
      <div><dt>RFQ due</dt><dd>{formatOptionalDate(quote.rfqDueDate?.slice(0, 10) ?? null)}</dd></div>
      {quote.estimatingDueDate && quote.estimatingDueDate.slice(0, 10) !== quote.rfqDueDate?.slice(0, 10)
        && <div><dt>Estimating due</dt><dd>{formatOptionalDate(quote.estimatingDueDate.slice(0, 10))}</dd></div>}
      {quote.estimatingCompletionDate && <div><dt>Completed</dt><dd>{formatOptionalDate(quote.estimatingCompletionDate.slice(0, 10))}</dd></div>}
      <div><dt>Value</dt><dd>{currency(quote.totalValue)}</dd></div>
    </dl>
    {quote.ardaStatusNotes && <p className="qd-detail-note">{quote.ardaStatusNotes}</p>}
    {quote.canOpenQuoteStatus && <a className="qd-primary-button" href={quoteStatusUrl(quote.quoteNumber)}>Open quote status<ArrowUpRight size={16} /></a>}
    {linkedQuotes.length > 0 && <section className="qd-linked-estimates" aria-label="Linked estimates"><h3>Linked estimates</h3>{linkedQuotes.map((local, index) => {
      const part = (local.draft ?? local.revisions.at(-1))?.estimate.metadata.partNumber
      return <a key={local.id} href={`#/calculator?quote=${encodeURIComponent(local.id)}`}><Link2 size={14} />{part || `Estimate ${index + 1}`}</a>
    })}</section>}
    {linkedQuotes.length === 0 && canGenerate && quote.canGenerateEstimate && <button className="qd-secondary-button" type="button" disabled={storageError} onClick={onGenerate}><Plus size={15} />Create estimate</button>}
  </aside>
}
