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
  discardQuoteRevisionDraft,
  getQuoteStoreError,
  getLatestPublishedRevision,
  listQuotes,
  saveQuoteDraft,
  type QuoteRevision,
} from './quoteStore'
import {
  quoteDashboardStatus,
  quoteDashboardVersion,
  type QuoteDashboardFilter,
} from './quoteDashboardModel'
import './quote-dashboard.css'

function currency(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 0,
  })
}

function quoteTitle(version: QuoteRevision | null) {
  return version?.estimate.metadata.quoteLogNumber
    || version?.estimate.metadata.partNumber
    || 'Untitled quote'
}

function quoteValue(version: QuoteRevision | null) {
  if (!version) return 0
  const result = calculateEstimate(version.estimate)
  return result.ok
    ? result.quantities[version.selectedQuantity]?.extendedValue ?? 0
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
  const [filter, setFilter] = useState<QuoteDashboardFilter>('all')
  const [actionError, setActionError] = useState<string | null>(null)
  const quotes = useMemo(
    () => listQuotes(ownerAccountName),
    [ownerAccountName, revision],
  )
  const storageError = getQuoteStoreError()
  const filteredQuotes = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    return quotes.filter((quote) => {
      const version = quoteDashboardVersion(quote, filter)
      if (!version) return false
      if (filter === 'draft' && !quote.draft) return false
      if (filter !== 'all' && filter !== 'draft' && quote.status !== filter) return false
      if (!query) return true
      return [
        version.estimate.metadata.quoteLogNumber,
        version.estimate.metadata.customer,
        version.estimate.metadata.partNumber,
        version.estimate.metadata.rfqNumber,
        version.estimate.metadata.estimator,
      ].some((value) => value.toLocaleLowerCase().includes(query))
    })
  }, [filter, quotes, search])
  const counts = {
    draft: quotes.filter((quote) => Boolean(quote.draft)).length,
    current: quotes.filter((quote) => quote.status === 'current').length,
    past: quotes.filter((quote) => quote.status === 'past').length,
  }
  const currentValue = quotes
    .filter((quote) => quote.status === 'current')
    .reduce((total, quote) => total + quoteValue(getLatestPublishedRevision(quote)), 0)

  const createQuote = () => {
    if (!canManageQuotes || storageError) return
    setActionError(null)
    const estimate = createEstimateDefaults('standard')
    const record = saveQuoteDraft({
      ownerAccountName,
      estimate,
      selectedQuantity: estimate.quantities[0],
    })
    if (record) {
      window.location.hash = `/calculator?quote=${record.id}`
      return
    }
    setActionError(getQuoteStoreError() ?? 'The quote draft could not be created in this browser.')
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
          disabled={!canManageQuotes || Boolean(storageError)}
          title={storageError ?? (canManageQuotes ? 'Create a quote' : 'Editor access is required')}
          onClick={createQuote}
        >
          <Plus size={17} aria-hidden="true" />
          New quote
        </button>
      </section>

      {(storageError || actionError) && (
        <p className="quote-storage-error" role="alert">{storageError ?? actionError}</p>
      )}

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
                  <th scope="col">Part / Drawing Rev</th>
                  <th scope="col">Quote Rev</th>
                  <th scope="col">Status</th>
                  <th scope="col">Quantities</th>
                  <th scope="col">Value</th>
                  <th scope="col">Updated</th>
                  <th scope="col">Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredQuotes.map((quote) => {
                  const displayVersion = quoteDashboardVersion(quote, filter)
                  if (!displayVersion) return null
                  const rowStatus = quoteDashboardStatus(quote, filter)
                  const isDraftVersion = quote.draft?.id === displayVersion.id
                  return (
                    <tr key={quote.id}>
                      <th scope="row">{quoteTitle(displayVersion)}</th>
                      <td>{displayVersion.estimate.metadata.customer || '—'}</td>
                      <td>
                        {displayVersion.estimate.metadata.partNumber || '—'}
                        {displayVersion.estimate.metadata.revision
                          ? ` / ${displayVersion.estimate.metadata.revision}`
                          : ''}
                      </td>
                      <td className="quote-revision-cell">
                        R{displayVersion.revisionNumber}
                        <small>{isDraftVersion ? 'Draft' : 'Published'}</small>
                        {!isDraftVersion && quote.draft && (
                          <small>R{quote.draft.revisionNumber} draft available</small>
                        )}
                      </td>
                      <td>
                        <span className={`quote-status status-${rowStatus}`}>
                          {rowStatus}
                        </span>
                      </td>
                      <td>{displayVersion.estimate.quantities.join(', ')}</td>
                      <td>{currency(quoteValue(displayVersion))}</td>
                      <td>{formatDate(quote.updatedAt)}</td>
                      <td>
                        <div className="quote-row-actions">
                          <button
                            type="button"
                            onClick={() => {
                              window.location.hash = `/calculator?quote=${quote.id}`
                            }}
                          >
                            {quote.draft ? 'Continue draft' : 'View'}
                          </button>
                          {canManageQuotes && quote.draft && quote.revisions.length === 0 && (
                            <button
                              type="button"
                              className="danger-link"
                              onClick={() => {
                                if (!window.confirm(`Delete ${quoteTitle(displayVersion)}?`)) return
                                setActionError(null)
                                if (deleteQuote(quote.id, ownerAccountName)) {
                                  setRevision((current) => current + 1)
                                } else {
                                  setActionError(getQuoteStoreError() ?? 'The draft could not be deleted.')
                                }
                              }}
                            >
                              Delete
                            </button>
                          )}
                          {canManageQuotes && quote.draft && quote.revisions.length > 0 && (
                            <button
                              type="button"
                              className="danger-link"
                              onClick={() => {
                                if (!window.confirm(`Discard the R${quote.draft?.revisionNumber} draft? Published revisions will be kept.`)) return
                                setActionError(null)
                                if (discardQuoteRevisionDraft(quote.id, ownerAccountName)) {
                                  setRevision((current) => current + 1)
                                } else {
                                  setActionError(getQuoteStoreError() ?? 'The revision draft could not be discarded.')
                                }
                              }}
                            >
                              Discard draft
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  )
}
