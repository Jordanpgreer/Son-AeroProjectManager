import {
  Archive,
  CircleDollarSign,
  FileClock,
  FileText,
  Plus,
  Search,
} from 'lucide-react'
import { useMemo, useState } from 'react'

import { calculateEstimate } from './calculations'
import { createEstimateDefaults } from './estimateDefaults'
import {
  deleteQuote,
  listQuotes,
  saveQuote,
  type QuoteRecord,
  type QuoteStatus,
} from './quoteStore'
import './quote-dashboard.css'

type QuoteFilter = 'all' | QuoteStatus

function currency(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 0,
  })
}

function quoteTitle(quote: QuoteRecord) {
  return quote.estimate.metadata.quoteLogNumber
    || quote.estimate.metadata.partNumber
    || 'Untitled quote'
}

function quoteValue(quote: QuoteRecord) {
  const result = calculateEstimate(quote.estimate)
  return result.ok
    ? result.quantities[quote.selectedQuantity]?.extendedValue ?? 0
    : 0
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  }).format(new Date(value))
}

export default function QuotesDashboardPage({
  ownerAccountName,
  canManageQuotes,
}: {
  ownerAccountName: string
  canManageQuotes: boolean
}) {
  const [revision, setRevision] = useState(0)
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<QuoteFilter>('all')
  const quotes = useMemo(
    () => listQuotes(ownerAccountName),
    [ownerAccountName, revision],
  )
  const filteredQuotes = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    return quotes.filter((quote) => {
      if (filter !== 'all' && quote.status !== filter) return false
      if (!query) return true
      return [
        quote.estimate.metadata.quoteLogNumber,
        quote.estimate.metadata.customer,
        quote.estimate.metadata.partNumber,
        quote.estimate.metadata.rfqNumber,
        quote.estimate.metadata.estimator,
      ].some((value) => value.toLocaleLowerCase().includes(query))
    })
  }, [filter, quotes, search])
  const counts = {
    draft: quotes.filter((quote) => quote.status === 'draft').length,
    current: quotes.filter((quote) => quote.status === 'current').length,
    past: quotes.filter((quote) => quote.status === 'past').length,
  }
  const currentValue = quotes
    .filter((quote) => quote.status === 'current')
    .reduce((total, quote) => total + quoteValue(quote), 0)

  const createQuote = () => {
    if (!canManageQuotes) return
    const estimate = createEstimateDefaults('standard')
    const record = saveQuote({
      ownerAccountName,
      status: 'draft',
      estimate,
      selectedQuantity: estimate.quantities[0],
    })
    if (record) window.location.hash = `/calculator?quote=${record.id}`
  }

  return (
    <div className="quote-dashboard-page">
      <section className="quote-dashboard-intro">
        <div>
          <span className="section-kicker">Quote workspace</span>
          <h2>Estimating Pipeline</h2>
          <p>Continue drafts, track current quotes, and retain completed quote history.</p>
        </div>
        <button
          type="button"
          className="primary-action-button"
          disabled={!canManageQuotes}
          title={canManageQuotes ? 'Create a quote' : 'Editor access is required'}
          onClick={createQuote}
        >
          <Plus size={17} aria-hidden="true" />
          New quote
        </button>
      </section>

      <section className="quote-kpi-grid" aria-label="Quote portfolio summary">
        <button type="button" onClick={() => setFilter('draft')}>
          <span><FileClock size={18} aria-hidden="true" /> Draft quotes</span>
          <strong>{counts.draft}</strong>
          <small>Waiting for completion</small>
        </button>
        <button type="button" onClick={() => setFilter('current')}>
          <span><FileText size={18} aria-hidden="true" /> Current quotes</span>
          <strong>{counts.current}</strong>
          <small>Actively quoted</small>
        </button>
        <button type="button" onClick={() => setFilter('past')}>
          <span><Archive size={18} aria-hidden="true" /> Past quotes</span>
          <strong>{counts.past}</strong>
          <small>Completed history</small>
        </button>
        <div>
          <span><CircleDollarSign size={18} aria-hidden="true" /> Current value</span>
          <strong>{currency(currentValue)}</strong>
          <small>Extended quote value</small>
        </div>
      </section>

      <section className="quote-list-card" aria-labelledby="quote-list-heading">
        <div className="quote-list-toolbar">
          <div>
            <span className="section-kicker">Local quote records</span>
            <h2 id="quote-list-heading">Quotes</h2>
          </div>
          <label className="quote-search">
            <Search size={15} aria-hidden="true" />
            <input
              type="search"
              value={search}
              placeholder="Search quote, customer, part, RFQ…"
              aria-label="Search quotes"
              onChange={(event) => setSearch(event.currentTarget.value)}
            />
          </label>
        </div>

        <div className="quote-filter-tabs" role="group" aria-label="Filter quotes by status">
          {(['all', 'draft', 'current', 'past'] as const).map((status) => (
            <button
              type="button"
              className={filter === status ? 'active' : undefined}
              aria-pressed={filter === status}
              key={status}
              onClick={() => setFilter(status)}
            >
              {status === 'all' ? 'All quotes' : status}
            </button>
          ))}
        </div>

        {filteredQuotes.length === 0 ? (
          <div className="quote-empty-state">
            <FileText size={30} aria-hidden="true" />
            <strong>No {filter === 'all' ? '' : `${filter} `}quotes yet</strong>
            <span>Create a quote or change the current filters.</span>
          </div>
        ) : (
          <div className="table-scroll">
            <table className="quote-table">
              <thead>
                <tr>
                  <th scope="col">Quote</th>
                  <th scope="col">Customer</th>
                  <th scope="col">Part / Rev</th>
                  <th scope="col">Status</th>
                  <th scope="col">Quantities</th>
                  <th scope="col">Value</th>
                  <th scope="col">Updated</th>
                  <th scope="col">Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredQuotes.map((quote) => (
                  <tr key={quote.id}>
                    <th scope="row">{quoteTitle(quote)}</th>
                    <td>{quote.estimate.metadata.customer || '—'}</td>
                    <td>
                      {quote.estimate.metadata.partNumber || '—'}
                      {quote.estimate.metadata.revision
                        ? ` / ${quote.estimate.metadata.revision}`
                        : ''}
                    </td>
                    <td><span className={`quote-status status-${quote.status}`}>{quote.status}</span></td>
                    <td>{quote.estimate.quantities.join(', ')}</td>
                    <td>{currency(quoteValue(quote))}</td>
                    <td>{formatDate(quote.updatedAt)}</td>
                    <td>
                      <div className="quote-row-actions">
                        <button
                          type="button"
                          onClick={() => {
                            window.location.hash = `/calculator?quote=${quote.id}`
                          }}
                        >
                          Open
                        </button>
                        {canManageQuotes && quote.status === 'draft' && (
                          <button
                            type="button"
                            className="danger-link"
                            onClick={() => {
                              if (window.confirm(`Delete ${quoteTitle(quote)}?`)) {
                                deleteQuote(quote.id, ownerAccountName)
                                setRevision((current) => current + 1)
                              }
                            }}
                          >
                            Delete
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  )
}
