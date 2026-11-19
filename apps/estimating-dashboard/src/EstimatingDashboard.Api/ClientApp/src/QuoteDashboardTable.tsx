import { AlertTriangle, ChevronLeft, ChevronRight, LoaderCircle } from 'lucide-react'
import { dashboardLastPage, type DashboardQuote, type QuoteDashboardResponse } from './quoteDashboardApi'
import { currency, formatOptionalDate } from './quoteDashboardFormatting'

export default function QuoteDashboardTable({ data, loading, unavailable, selectedId, onSelect, onPage }: {
  data: QuoteDashboardResponse | null; loading: boolean; unavailable: boolean; selectedId: number | null
  onSelect: (quote: DashboardQuote, trigger: HTMLElement) => void; onPage: (page: number) => void
}) {
  const pages = data ? dashboardLastPage(data.total, data.pageSize) : 1
  return <section className="qd-table-card" id="quote-dashboard-results" aria-label="Quotes" aria-busy={loading}>
    {loading && !data ? <div className="qd-empty" role="status"><LoaderCircle className="is-spinning" size={23} />Loading quotes…</div>
      : !data && unavailable ? <div className="qd-empty" role="status">Quote results are unavailable.</div>
        : !data || data.records.length === 0 ? <div className="qd-empty" role="status"><strong>No quotes in this view</strong><span>Try another period, search, or status.</span></div>
        : <div className="qd-table-scroll"><table className="qd-table">
          <thead><tr><th scope="col">Quote</th><th scope="col">Part number</th><th scope="col">Customer</th><th scope="col">Estimator</th><th scope="col">Arda status</th><th scope="col">RFQ due</th><th scope="col">Value</th></tr></thead>
          <tbody>{data.records.map(quote => <tr key={quote.id} className={selectedId === quote.id ? 'is-selected' : undefined}
            tabIndex={0} aria-label={`View quote ${quote.quoteNumber}`} aria-selected={selectedId === quote.id}
            onClick={event => onSelect(quote, event.currentTarget)} onKeyDown={event => {
              if (event.target !== event.currentTarget || !['Enter', ' '].includes(event.key)) return
              event.preventDefault(); onSelect(quote, event.currentTarget)
            }}>
            <th scope="row"><span className="qd-quote-number">#{quote.quoteNumber}{quote.hasFulcrumWarnings && <span title="Production review needed"><AlertTriangle size={14} aria-label="Production review needed" /></span>}</span></th>
            <td className="qd-parts" title={quote.partNumbers.length ? quote.partNumbers.join(', ') : 'Part numbers are not available in the current snapshot'}>{quote.partNumbers.length ? quote.partNumbers.join(', ') : '—'}</td>
            <td className="qd-customer">{quote.customer || '—'}</td>
            <td>{quote.estimatingRep || 'Unassigned'}</td>
            <td><span className="qd-status" data-status={quote.ardaStatus ?? 'Untouched'}><i aria-hidden="true" />{quote.ardaStatus ?? 'Untouched'}</span></td>
            <td><span className={`qd-due${quote.isOverdue ? ' is-overdue' : ''}`}>{formatOptionalDate(quote.rfqDueDate?.slice(0, 10) ?? null)}{quote.isOverdue && <small>Overdue</small>}</span></td>
            <td className="qd-value">{currency(quote.totalValue)}</td>
          </tr>)}</tbody>
        </table></div>}
    {data && (data.total > data.pageSize || data.page > 1) && <nav className="qd-pagination" aria-label="Quote result pages">
      <span>Page {data.page} of {pages.toLocaleString()}</span>
      <button type="button" disabled={loading || data.page <= 1} onClick={() => onPage(data.page - 1)}><ChevronLeft size={15} />Previous</button>
      <button type="button" disabled={loading || data.page >= pages} onClick={() => onPage(data.page + 1)}>Next<ChevronRight size={15} /></button>
    </nav>}
  </section>
}
