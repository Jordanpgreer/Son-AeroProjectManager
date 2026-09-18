import {
  AlertTriangle,
  CalendarDays,
  ArrowUpRight,
  CheckCircle2,
  Clock3,
  FileText,
  Link2,
  Plus,
  RefreshCw,
  Search,
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  deleteQuote,
  discardQuoteRevisionDraft,
  getQuoteStoreError,
  listQuotes,
} from './quoteStore'
import {
  quoteDashboardStatus,
  quoteDashboardVersion,
  sortActivePersonalQuotes,
  sortCompletedPersonalQuotes,
  type QuoteDashboardFilter,
  type PersonalQuoteView,
} from './quoteDashboardModel'
import { formatQuoteRevision } from './quoteRevision'
import {
  loadPersonalQuotes,
  refreshPersonalQuoteAssignments,
  statusSetLabel,
  type PersonalQuote,
} from './quoteWorkflowApi'
import './quote-dashboard.css'
import './quote-dashboard-states.css'
import GenerateQuoteDialog from './GenerateQuoteDialog'
import { quoteStatusUrl } from './estimatingNavigation'
import { currency, quoteTitle, quoteValue, formatDate, formatOptionalDate, linkedSourceFromHash } from './quoteDashboardFormatting'
export default function QuotesDashboardPage({
  ownerAccountName,
  canManageQuotes,
  canDeleteQuotes,
  canGenerateQuotes,
}: {
  ownerAccountName: string
  canManageQuotes: boolean
  canDeleteQuotes: boolean
  canGenerateQuotes: boolean
}) {
  const [revision, setRevision] = useState(0)
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<QuoteDashboardFilter>('all')
  const [actionError, setActionError] = useState<string | null>(null)
  const [personalQuotes, setPersonalQuotes] = useState<PersonalQuote[]>([])
  const [personalLoading, setPersonalLoading] = useState(true)
  const [personalError, setPersonalError] = useState<string | null>(null)
  const [personalView, setPersonalView] = useState<PersonalQuoteView>('active')
  const [generatingQuote, setGeneratingQuote] = useState<PersonalQuote | null>(null)
  const [sourceFilter, setSourceFilter] = useState<number | null>(linkedSourceFromHash)
  const personalQuotesHeading = useRef<HTMLHeadingElement>(null)
  const localDraftsHeading = useRef<HTMLHeadingElement>(null)
  const quotes = useMemo(
    () => listQuotes(ownerAccountName),
    [ownerAccountName, revision],
  )
  const storageError = getQuoteStoreError()
  const showPersonalActions = canGenerateQuotes || quotes.some((quote) => quote.sourceQuote
    && personalQuotes.some((personal) => personal.id === quote.sourceQuote!.quoteHistoryId))
  const filteredQuotes = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    return quotes.filter((quote) => {
      const version = quoteDashboardVersion(quote, filter)
      if (sourceFilter !== null && quote.sourceQuote?.quoteHistoryId !== sourceFilter) return false
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
  }, [filter, quotes, search, sourceFilter])
  const showLinkedEstimates = (quoteId: number) => {
    setSourceFilter(quoteId)
    setSearch('')
    setFilter('all')
    window.history.replaceState(null, '', `#/quotes?source=${quoteId}`)
    requestAnimationFrame(() => {
      localDraftsHeading.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      localDraftsHeading.current?.focus({ preventScroll: true })
    })
  }
  useEffect(() => {
    const syncSource = () => {
      setSourceFilter(linkedSourceFromHash())
      setFilter('all')
      setSearch('')
    }
    window.addEventListener('hashchange', syncSource)
    return () => window.removeEventListener('hashchange', syncSource)
  }, [])
  useEffect(() => {
    if (sourceFilter !== null) localDraftsHeading.current?.scrollIntoView({ block: 'start' })
  }, [sourceFilter])
  const activePersonalQuotes = useMemo(() => sortActivePersonalQuotes(personalQuotes), [personalQuotes])
  const completedPersonalQuotes = useMemo(() => sortCompletedPersonalQuotes(personalQuotes), [personalQuotes])
  const overduePersonalQuotes = useMemo(
    () => activePersonalQuotes.filter((quote) => quote.isOverdue),
    [activePersonalQuotes],
  )
  const visiblePersonalQuotes = personalView === 'completed'
    ? completedPersonalQuotes
    : personalView === 'overdue'
      ? overduePersonalQuotes
      : activePersonalQuotes

  const refreshPersonalQuotes = useCallback(async (pullFromFulcrum = false) => {
    setPersonalLoading(true)
    setPersonalError(null)
    try {
      setPersonalQuotes(await (pullFromFulcrum
        ? refreshPersonalQuoteAssignments()
        : loadPersonalQuotes()))
    } catch (error) {
      setPersonalError(error instanceof Error ? error.message : 'Your assigned quotes could not be loaded.')
    } finally {
      setPersonalLoading(false)
    }
  }, [])

  useEffect(() => {
    void refreshPersonalQuotes(false)
  }, [ownerAccountName, refreshPersonalQuotes])

  const showPersonalView = (view: PersonalQuoteView) => {
    setPersonalView(view)
    requestAnimationFrame(() => {
      personalQuotesHeading.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      personalQuotesHeading.current?.focus({ preventScroll: true })
    })
  }
  return (
    <div className="quote-dashboard-page">
      {(storageError || actionError) && (
        <p className="quote-storage-error" role="alert">{storageError ?? actionError}</p>
      )}

      <section className="quote-kpi-grid" aria-label="My quote workload summary">
        <button type="button" className={personalView === 'active' ? 'is-selected' : undefined} aria-pressed={personalView === 'active'} onClick={() => showPersonalView('active')}>
          <span><FileText size={18} aria-hidden="true" /> Active quotes</span>
          <strong>{personalLoading ? '—' : activePersonalQuotes.length}</strong>
          <small>Open my due-date queue</small>
        </button>
        <button type="button" className={`is-overdue${personalView === 'overdue' ? ' is-selected' : ''}`} aria-pressed={personalView === 'overdue'} onClick={() => showPersonalView('overdue')}>
          <span><AlertTriangle size={18} aria-hidden="true" /> Overdue quotes</span>
          <strong>{personalLoading ? '—' : overduePersonalQuotes.length}</strong>
          <small>Open the overdue subset</small>
        </button>
        <button type="button" className={personalView === 'completed' ? 'is-selected' : undefined} aria-pressed={personalView === 'completed'} onClick={() => showPersonalView('completed')}>
          <span><CheckCircle2 size={18} aria-hidden="true" /> Completed quotes</span>
          <strong>{personalLoading ? '—' : completedPersonalQuotes.length}</strong>
          <small>View my Fulcrum outcomes</small>
        </button>
      </section>

      <section className="quote-list-card personal-quote-card" aria-labelledby="personal-quotes-heading">
        <div className="quote-list-toolbar">
          <div>
            <span className="section-kicker">Assigned in Fulcrum</span>
            <h2 id="personal-quotes-heading" ref={personalQuotesHeading} tabIndex={-1}>
              {personalView === 'completed' ? 'My completed quotes' : personalView === 'overdue' ? 'My overdue quotes' : 'My active quotes'}
            </h2>
            <p className="quote-section-description">
              {personalView === 'completed'
                ? 'Completed work is shown with its final Fulcrum outcome.'
                : 'Active work is ordered by estimating due date. Open a quote to manage its Arda workflow.'}
            </p>
          </div>
          <button
            type="button"
            className="quote-refresh-button"
            disabled={personalLoading || !canManageQuotes}
            title={canManageQuotes ? 'Pull new and updated Fulcrum assignments' : 'Editor access is required'}
            onClick={() => void refreshPersonalQuotes(true)}
          >
            <RefreshCw size={14} aria-hidden="true" />
            Refresh
          </button>
        </div>

        <div className="personal-quote-view-tabs" role="group" aria-label="Choose assigned quote view">
          <button type="button" className={personalView === 'active' ? 'active' : undefined} aria-pressed={personalView === 'active'} onClick={() => setPersonalView('active')}>
            Active <span>{activePersonalQuotes.length}</span>
          </button>
          <button type="button" className={personalView === 'overdue' ? 'active' : undefined} aria-pressed={personalView === 'overdue'} onClick={() => setPersonalView('overdue')}>
            Overdue <span>{overduePersonalQuotes.length}</span>
          </button>
          <button type="button" className={personalView === 'completed' ? 'active' : undefined} aria-pressed={personalView === 'completed'} onClick={() => setPersonalView('completed')}>
            Completed <span>{completedPersonalQuotes.length}</span>
          </button>
        </div>

        {personalError && <p className="personal-quote-error" role="alert">{personalError}</p>}

        {personalLoading ? (
          <div className="quote-empty-state compact">
            <RefreshCw className="is-spinning" size={26} aria-hidden="true" />
            <strong>Loading your quotes…</strong>
          </div>
        ) : visiblePersonalQuotes.length === 0 ? (
          <div className="quote-empty-state compact">
            {personalView === 'completed' ? <CheckCircle2 size={28} aria-hidden="true" /> : <FileText size={28} aria-hidden="true" />}
            <strong>{personalView === 'completed' ? 'No completed quotes found for you' : personalView === 'overdue' ? 'You have no overdue quotes' : 'No active quotes are assigned to you'}</strong>
            <span>{personalView === 'overdue' ? 'Your active queue is currently on schedule.' : 'Assignments appear here after the next Fulcrum sync.'}</span>
          </div>
        ) : (
          <div className="table-scroll">
            <table className="quote-table personal-quote-table">
              <thead>
                <tr>
                  <th scope="col">Quote</th>
                  <th scope="col">Customer</th>
                  {personalView === 'completed' ? <>
                    <th scope="col">Fulcrum status</th>
                    <th scope="col">Completed</th>
                  </> : <>
                    <th scope="col">Arda status</th>
                    <th scope="col">Estimating due</th>
                    <th scope="col">Status set</th>
                  </>}
                  {personalView !== 'completed' && showPersonalActions && <th scope="col"><span className="sr-only">Linked estimates</span></th>}
                </tr>
              </thead>
              <tbody>
                {visiblePersonalQuotes.map((quote) => {
                  const linkedQuotes = quotes.filter((local) => local.sourceQuote?.quoteHistoryId === quote.id)
                  return (
                    <tr
                      key={`quote-${quote.id}`}
                      id={`active-quote-${quote.id}`}
                      className={`personal-quote-row is-actionable${quote.isOverdue ? ' is-overdue' : ''}`}
                      tabIndex={0}
                      aria-label={`Open status for quote ${quote.quoteNumber}`}
                      onClick={(event) => {
                        if ((event.target as HTMLElement).closest('a, button, input, select, textarea')) return
                        window.location.hash = quoteStatusUrl(quote.quoteNumber)
                      }}
                      onKeyDown={(event) => {
                        if (event.target !== event.currentTarget || (event.key !== 'Enter' && event.key !== ' ')) return
                        event.preventDefault()
                        window.location.hash = quoteStatusUrl(quote.quoteNumber)
                      }}
                    >
                      <th scope="row">
                        <span className="personal-quote-heading">
                          <span>
                            <a className="personal-quote-number personal-quote-link" href={quoteStatusUrl(quote.quoteNumber)} aria-label={`Open quote ${quote.quoteNumber} status`}>#{quote.quoteNumber}</a>
                            <small>{currency(quote.totalValue)}</small>
                          </span>
                          <ArrowUpRight className="personal-quote-open-icon" size={16} aria-hidden="true" />
                        </span>
                      </th>
                      <td>{quote.customer}</td>
                      {personalView === 'completed' ? <>
                        <td>
                          <span className="quote-status fulcrum-status">{quote.fulcrumQuoteStatus || 'Completed'}</span>
                        </td>
                        <td>
                          <span className="quote-due-date"><CheckCircle2 size={13} aria-hidden="true" />{formatOptionalDate(quote.estimatingCompletionDate)}</span>
                        </td>
                      </> : <>
                        <td>
                        <span className={`quote-status arda-status${quote.ardaStatus ? '' : ' unset'}`}>
                          {quote.ardaStatus ?? 'Untouched'}
                        </span>
                        {quote.ardaStatusNotes && (
                          <small className="quote-cell-detail quote-note-preview" title={quote.ardaStatusNotes}>
                            {quote.ardaStatusNotes}
                          </small>
                        )}
                        </td>
                        <td>
                          <span className={`quote-due-date${quote.isOverdue ? ' is-overdue' : ''}`}>
                          <CalendarDays size={13} aria-hidden="true" />
                          {formatOptionalDate(quote.estimatingDueDate)}
                          {quote.isOverdue && <small>Overdue</small>}
                          </span>
                        </td>
                        <td>
                          <span className="quote-status-age">
                          <Clock3 size={13} aria-hidden="true" />
                          {statusSetLabel(quote.ardaStatusChangedAt)}
                          </span>
                          {quote.ardaStatusChangedBy && (
                          <small className="quote-cell-detail">by {quote.ardaStatusChangedBy}</small>
                          )}
                        </td>
                      </>}
                      {personalView !== 'completed' && showPersonalActions && <td>
                        {(canGenerateQuotes || linkedQuotes.length > 0) &&
                        <button
                          type="button"
                          className="quote-create-button"
                          disabled={Boolean(storageError) && linkedQuotes.length === 0}
                          aria-label={linkedQuotes.length ? `Open ${linkedQuotes.length} estimates for quote ${quote.quoteNumber}` : `Create quote ${quote.quoteNumber}`}
                          onClick={(event) => {
                            event.stopPropagation()
                            if (linkedQuotes.length) showLinkedEstimates(quote.id)
                            else setGeneratingQuote(quote)
                          }}
                        >
                          {linkedQuotes.length ? <><Link2 size={14} aria-hidden="true" /> Open estimates ({linkedQuotes.length})</> : <><Plus size={14} aria-hidden="true" /> Create estimate</>}
                        </button>
                        }
                      </td>}
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="quote-list-card" aria-labelledby="quote-list-heading">
        <div className="quote-list-toolbar">
          <div>
            <span className="section-kicker">Calculator workspace</span>
            <h2 id="quote-list-heading" ref={localDraftsHeading} tabIndex={-1}>Local drafts and revisions</h2>
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

        {sourceFilter !== null && <div className="quote-source-filter">
          <Link2 size={16} aria-hidden="true" />
          <span>Estimates linked to Fulcrum quote #{personalQuotes.find((quote) => quote.id === sourceFilter)?.quoteNumber
            ?? quotes.find((quote) => quote.sourceQuote?.quoteHistoryId === sourceFilter)?.sourceQuote?.quoteNumber ?? sourceFilter}</span>
          <button type="button" onClick={() => {
            setSourceFilter(null)
            window.history.replaceState(null, '', '#/quotes')
          }}>Show all quotes</button>
        </div>}

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
            <span>No local calculator records match the current filters.</span>
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
                      <th scope="row">
                        {quoteTitle(displayVersion)}
                        {quote.sourceQuote ? <button type="button" className="quote-source-link" onClick={() => {
                          window.location.hash = quoteStatusUrl(quote.sourceQuote!.quoteNumber)
                        }}><Link2 size={12} aria-hidden="true" /> Fulcrum #{quote.sourceQuote.quoteNumber}</button>
                          : <small className="quote-cell-detail">Ad hoc</small>}
                      </th>
                      <td>{displayVersion.estimate.metadata.customer || '—'}</td>
                      <td>
                        {displayVersion.estimate.metadata.partNumber || '—'}
                        {displayVersion.estimate.metadata.revision
                          ? ` / ${displayVersion.estimate.metadata.revision}`
                          : ''}
                      </td>
                      <td className="quote-revision-cell">
                        {formatQuoteRevision(displayVersion.revisionNumber)}
                        <small>{isDraftVersion ? 'Draft' : 'Published'}</small>
                        {!isDraftVersion && quote.draft && (
                          <small>{formatQuoteRevision(quote.draft.revisionNumber)} draft available</small>
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
                          {canDeleteQuotes && (
                            <button
                              type="button"
                              className="danger-link"
                              onClick={() => {
                                const revisionWarning = quote.revisions.length > 0
                                  ? ` This will permanently remove ${quote.revisions.length} published ${quote.revisions.length === 1 ? 'revision' : 'revisions'}${quote.draft ? ' and the current draft' : ''}.`
                                  : ''
                                if (!window.confirm(`Delete ${quoteTitle(displayVersion)}?${revisionWarning}`)) return
                                setActionError(null)
                                if (deleteQuote(quote.id, ownerAccountName)) {
                                  setRevision((current) => current + 1)
                                } else {
                                  setActionError(getQuoteStoreError() ?? 'The quote could not be deleted.')
                                }
                              }}
                              aria-label={`Delete ${quoteTitle(displayVersion)}`}
                            >
                              Delete
                            </button>
                          )}
                          {canManageQuotes && quote.draft && quote.revisions.length > 0 && (
                            <button
                              type="button"
                              className="danger-link"
                              onClick={() => {
                                if (!window.confirm(`Discard the ${formatQuoteRevision(quote.draft?.revisionNumber ?? 1)} draft? Published revs will be kept.`)) return
                                setActionError(null)
                                if (discardQuoteRevisionDraft(quote.id, ownerAccountName)) {
                                  setRevision((current) => current + 1)
                                } else {
                                  setActionError(getQuoteStoreError() ?? 'The rev draft could not be discarded.')
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
      {generatingQuote && <GenerateQuoteDialog
        quote={generatingQuote}
        ownerAccountName={ownerAccountName}
        onClose={() => {
          setGeneratingQuote(null)
          setRevision((current) => current + 1)
        }}
      />}
    </div>
  )
}
