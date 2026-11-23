import { AlertTriangle, ChevronLeft, ChevronRight, Link2, LoaderCircle, Plus } from 'lucide-react'
import { quoteStatusUrl } from './estimatingNavigation'
import { dashboardLastPage, type DashboardQuote, type QuoteDashboardResponse } from './quoteDashboardApi'
import { currency, formatOptionalDate } from './quoteDashboardFormatting'
import type { QuoteRecord } from './quoteStore'

export default function QuoteDashboardTable({ data, loading, unavailable, localQuotes, canGenerate, storageError, onGenerate, onPage }: {
  data: QuoteDashboardResponse | null; loading: boolean; unavailable: boolean
  localQuotes: QuoteRecord[]; canGenerate: boolean; storageError: boolean
  onGenerate: (quote: DashboardQuote) => void; onPage: (page: number) => void
}) {
  const pages = data ? dashboardLastPage(data.total, data.pageSize) : 1
  return <section className="qd-table-card" id="quote-dashboard-results" aria-label="Quotes" aria-busy={loading}>
    {loading && !data ? <div className="qd-empty" role="status"><LoaderCircle className="is-spinning" size={23} />Loading quotes…</div>
      : !data && unavailable ? <div className="qd-empty" role="status">Quote results are unavailable.</div>
        : !data || data.records.length === 0 ? <div className="qd-empty" role="status"><strong>No quotes in this view</strong><span>Try a different search or quote filter.</span></div>
        : <div className="qd-table-scroll"><table className="qd-table">
          <thead><tr><th scope="col">Quote</th><th scope="col">Part number</th><th scope="col">Customer</th><th scope="col">Estimator</th><th scope="col">Arda status</th><th scope="col">RFQ due</th><th scope="col">Value</th></tr></thead>
          <tbody>{data.records.map(quote => {
            const href = quote.canOpenQuoteStatus ? quoteStatusUrl(quote.quoteNumber) : null
            const linkedQuotes = localQuotes.filter(local => local.sourceQuote?.quoteHistoryId === quote.id)
            return <tr key={quote.id} className={href ? 'qd-openable' : undefined} onClick={href ? event => {
              if (event.target instanceof Element && event.target.closest('a, button')) return
              if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || window.getSelection()?.type === 'Range') return
              window.location.hash = href
            } : undefined}>
              <th scope="row">
                <span className="qd-quote-number">
                  {href ? <a href={href} aria-label={`Open Quote Status for quote ${quote.quoteNumber}`}>#{quote.quoteNumber}</a> : `#${quote.quoteNumber}`}
                  {quote.hasFulcrumWarnings && <span title="Production review needed"><AlertTriangle size={14} aria-label="Production review needed" /></span>}
                </span>
                {linkedQuotes.length > 0
                  ? <div className="qd-estimate-links">{linkedQuotes.map((local, index) => {
                    const part = (local.draft ?? local.revisions.at(-1))?.estimate.metadata.partNumber
                    return <a key={local.id} href={`#/calculator?quote=${encodeURIComponent(local.id)}`} aria-label={`Open estimate ${part || index + 1} for quote ${quote.quoteNumber}`}><Link2 size={13} />{part || `Estimate ${index + 1}`}</a>
                  })}</div>
                  : canGenerate && quote.canGenerateEstimate && <button className="qd-estimate-action" type="button"
                    disabled={storageError} onClick={() => onGenerate(quote)} aria-label={`Create estimate for quote ${quote.quoteNumber}`}><Plus size={13} />Create estimate</button>}
              </th>
              <td className="qd-parts" title={quote.partNumbers.length ? quote.partNumbers.join(', ') : 'Part numbers are not available in the current snapshot'}>{quote.partNumbers.length ? quote.partNumbers.join(', ') : '—'}</td>
              <td className="qd-customer">{quote.customer || '—'}</td>
              <td>{quote.estimatingRep || 'Unassigned'}</td>
              <td><span className="qd-status" data-status={quote.ardaStatus ?? 'Untouched'}><i aria-hidden="true" />{quote.ardaStatus ?? 'Untouched'}</span></td>
              <td><span className={`qd-due${quote.isOverdue ? ' is-overdue' : ''}`}>{formatOptionalDate(quote.rfqDueDate?.slice(0, 10) ?? null)}{quote.isOverdue && <small>Overdue</small>}</span></td>
              <td className="qd-value">{currency(quote.totalValue)}</td>
            </tr>
          })}</tbody>
        </table></div>}
    {data && (data.total > data.pageSize || data.page > 1) && <nav className="qd-pagination" aria-label="Quote result pages">
      <span>Page {data.page} of {pages.toLocaleString()}</span>
      <button type="button" disabled={loading || data.page <= 1} onClick={() => onPage(data.page - 1)}><ChevronLeft size={15} />Previous</button>
      <button type="button" disabled={loading || data.page >= pages} onClick={() => onPage(data.page + 1)}>Next<ChevronRight size={15} /></button>
    </nav>}
  </section>
}
