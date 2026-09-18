import { ArrowLeft, ArrowRight, CalendarDays, ChevronRight, Clock3, GitBranch, Inbox, LoaderCircle, Mail, Plus, RefreshCw, Search, UserRound, X } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { estimatingPermissions, hasEstimatingPermission, type EstimatingMe } from '../authorization'
import { createEstimateDefaults } from '../estimateDefaults'
import { getQuoteStoreError, saveQuoteDraft } from '../quoteStore'
import { StatusBadge } from './ActivityTimeline'
import ConnectOutlookDialog from './ConnectOutlookDialog'
import Modal from './Modal'
import QuoteDetailPanel from './QuoteDetailPanel'
import { loadQuoteStatusDetail, loadQuoteStatuses, loadQuoteStatusOptions, loadVendorSync } from './api'
import { dateOnly, dateTime, DEFAULT_QUOTE_STATUS_SCOPE, isOverdue, QUOTE_STATUSES, quoteFromHash, quoteStatusRequestScope, shouldShowConnectOutlook, sortQuoteStatusesByDueDate, VENDOR_STATUSES, type QuoteStatusScope } from './model'
import type { QuoteStatusDetail, QuoteStatusPage as PageData, VendorSync } from './types'
import useDraftGuard from './useDraftGuard'
import './quote-status.css'
import './quote-status-workspace.css'
import './quote-request.css'

export default function QuoteStatusPage({ me }: { me: EstimatingMe }) {
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('')
  const [scope, setScope] = useState<QuoteStatusScope>(DEFAULT_QUOTE_STATUS_SCOPE)
  const [quoteFilter, setQuoteFilter] = useState(() => quoteFromHash(window.location.hash))
  const [page, setPage] = useState(1)
  const [data, setData] = useState<PageData | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const activeSelection = useRef(selectedId)
  activeSelection.current = selectedId
  const [detail, setDetail] = useState<QuoteStatusDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [detailLoading, setDetailLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [revision, setRevision] = useState(0)
  const [dirty, setDirty] = useState(false)
  const [draftReset, setDraftReset] = useState(0)
  const [connect, setConnect] = useState(false)
  const [sync, setSync] = useState<VendorSync[]>([])
  const [syncError, setSyncError] = useState<string | null>(null)
  const [syncLoaded, setSyncLoaded] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [options, setOptions] = useState({ statuses: QUOTE_STATUSES, threadStatuses: VENDOR_STATUSES })
  const { guard, pending, cancel, discard } = useDraftGuard(dirty, () => { setDraftReset(value => value + 1); setDirty(false) })
  const canManage = !me.isPreview && hasEstimatingPermission(me, estimatingPermissions.manageQuotes)
  const storageError = getQuoteStoreError()
  const showConnectOutlook = shouldShowConnectOutlook(syncLoaded, syncError, sync)
  const visibleQuotes = useMemo(() => scope === 'mine-active' && !quoteFilter
    ? sortQuoteStatusesByDueDate(data?.items ?? [])
    : data?.items ?? [], [data?.items, quoteFilter, scope])
  useEffect(() => { const timer = window.setTimeout(() => { setQuery(search); setPage(1) }, 250); return () => window.clearTimeout(timer) }, [search])
  useEffect(() => {
    function onHash() { const next = quoteFromHash(window.location.hash); setQuoteFilter(next); if (!next) setSelectedId(null); setPage(1) }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null)
    void loadQuoteStatuses(quoteFilter ? '' : query, quoteFilter ? '' : status, quoteFilter, page, quoteStatusRequestScope(scope, quoteFilter), controller.signal).then(result => {
      if (controller.signal.aborted) return
      setData(result)
      if (quoteFilter) setSelectedId(result.items.length === 1 ? result.items[0].quoteHistoryId : null)
    }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Quotes could not be loaded.') }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [query, status, scope, quoteFilter, page, revision])
  useEffect(() => {
    const controller = new AbortController()
    setSyncLoaded(false)
    void loadQuoteStatusOptions(controller.signal).then(setOptions).catch(() => { /* Local labels remain usable if options cannot load. */ })
    void loadVendorSync(controller.signal).then(result => {
      if (controller.signal.aborted) return
      setSync(result)
      setSyncError(null)
      setSyncLoaded(true)
    }).catch(reason => {
      if (controller.signal.aborted) return
      setSyncError(reason instanceof Error ? reason.message : 'Connection status is unavailable.')
      setSyncLoaded(true)
    })
    return () => controller.abort()
  }, [revision, connect])
  useEffect(() => {
    if (!selectedId) { setDetail(null); setDetailError(null); setDetailLoading(false); return }
    const controller = new AbortController()
    setDetailLoading(true); setDetailError(null); setDetail(null)
    void loadQuoteStatusDetail(selectedId, controller.signal).then(result => { if (!controller.signal.aborted) setDetail(result) }).catch(reason => { if (!controller.signal.aborted) setDetailError(reason instanceof Error ? reason.message : 'Quote details could not be loaded.') }).finally(() => { if (!controller.signal.aborted) setDetailLoading(false) })
    return () => controller.abort()
  }, [selectedId])
  const changed = useCallback(async (provided?: QuoteStatusDetail) => {
    if (!selectedId) return null
    const updated = provided ?? await loadQuoteStatusDetail(selectedId)
    if (updated.quote.quoteHistoryId !== selectedId) return null
    if (activeSelection.current !== selectedId) return null
    setDetail(updated)
    setDetailError(null)
    setData(previous => previous ? { ...previous, items: previous.items.map(item => item.quoteHistoryId === selectedId ? updated.quote : item) } : previous)
    return updated
  }, [selectedId])
  function refresh() {
    guard(() => { setRevision(value => value + 1); if (selectedId) void changed().catch(reason => setDetailError(reason instanceof Error ? reason.message : 'Quote could not be refreshed.')) })
  }
  function backToQuotes() {
    guard(() => { setSelectedId(null); setQuoteFilter(null); setPage(1); window.history.replaceState(null, '', '#/quote-status') })
  }
  function createQuote() {
    if (!canManage || storageError) return
    setCreateError(null)
    const estimate = createEstimateDefaults('standard')
    const record = saveQuoteDraft({ ownerAccountName: me.accountName, estimate, selectedQuantity: estimate.quantities[0] })
    if (record) {
      window.location.hash = `/calculator?quote=${record.id}`
      return
    }
    setCreateError(getQuoteStoreError() ?? 'The quote draft could not be created in this browser.')
  }
  const hasSearchFilters = Boolean(query || status || quoteFilter)
  return <div className="vq-page qs-request-page">
    <header className="vq-page-heading">
      <div className="vq-page-actions"><button type="button" className="vq-button" onClick={refresh} disabled={loading} aria-label="Refresh quote records"><RefreshCw size={16} /><span>Refresh</span></button>{showConnectOutlook && <button type="button" className="vq-button" onClick={() => setConnect(true)}><Mail size={16} />Connect Outlook</button>}<button type="button" className="vq-button vq-button-primary" disabled={!canManage || Boolean(storageError)} title={storageError ?? (canManage ? 'Create a quote' : 'Editor access is required')} onClick={createQuote}><Plus size={16} aria-hidden="true" />Create Quote</button></div>
    </header>
    {(storageError || createError) && <p className="vq-create-error" role="alert">{storageError ?? createError}</p>}
    <div className={`vq-workspace${!selectedId && !quoteFilter ? ' vq-browse' : ''}${selectedId ? ' vq-has-selection' : ''}${quoteFilter || selectedId ? ' vq-focused' : ''}`}>
      {!selectedId && <section className="vq-list" aria-label={scope === 'mine-active' ? 'My active quotes' : 'All quotes'}><div className="vq-list-toolbar">
        <div className="vq-list-heading"><div className="vq-list-title"><h2>{scope === 'mine-active' ? 'My active quotes' : 'All quotes'}</h2><span>{data?.totalCount ?? '—'}</span></div><p>{scope === 'mine-active' ? 'Assigned to you and sorted by estimating due date.' : 'Search the complete quote record.'}</p></div>
        <div className="vq-list-filters"><label className="vq-search"><Search size={16} /><input aria-label="Search quotes, customers, parts, or vendors" placeholder="Search quotes, customers, vendors…" value={search} onChange={event => setSearch(event.target.value)} />{search && <button type="button" aria-label="Clear search" onClick={() => setSearch('')}><X size={14} /></button>}</label><label className="vq-status-filter"><span>Status</span><select value={status} onChange={event => { setStatus(event.target.value); setPage(1) }}><option value="">All statuses</option>{options.statuses.map(item => <option key={item}>{item}</option>)}</select></label></div>
        <div className="vq-active-filters" aria-label="Quote filters">{scope === 'mine-active' ? <button type="button" className="vq-filter-chip" aria-label="Remove My active quotes filter" onClick={() => { setScope('all'); setPage(1) }}><UserRound size={13} />My active quotes<X size={13} /></button> : <button type="button" className="vq-filter-restore" onClick={() => { setScope('mine-active'); setPage(1) }}><UserRound size={13} />Show my active quotes</button>}{quoteFilter && <button type="button" className="vq-filter-chip" onClick={() => { setQuoteFilter(null); setPage(1); window.history.replaceState(null, '', '#/quote-status') }}>Quote {quoteFilter}<X size={13} /></button>}</div>
      </div>
        {error ? <div className="vq-empty vq-empty-small" role="alert"><p>{error}</p><button type="button" className="vq-button" onClick={refresh}>Try again</button></div> : loading ? <div className="vq-list-loading" role="status"><LoaderCircle size={22} className="vq-spinner" /><span>Loading quote records…</span></div> : !visibleQuotes.length ? <div className="vq-empty vq-empty-small"><Inbox size={30} /><h3>{hasSearchFilters ? 'No matching quotes' : scope === 'mine-active' ? 'No active quotes are assigned to you' : 'No quotes available'}</h3><p>{hasSearchFilters ? 'Try another search or status.' : scope === 'mine-active' ? 'You can remove the personal filter to search other quote records.' : 'Quotes will appear here after the next Fulcrum sync.'}</p>{hasSearchFilters ? <button type="button" className="vq-button" onClick={() => { setSearch(''); setStatus(''); setQuoteFilter(null); setPage(1) }}>Clear search filters</button> : scope === 'mine-active' && <button type="button" className="vq-button" onClick={() => { setScope('all'); setPage(1) }}>Browse all quotes</button>}</div> : <div className="vq-list-items">{visibleQuotes.map(item => <button type="button" className={`vq-quote-row${selectedId === item.quoteHistoryId ? ' is-selected' : ''}`} key={item.quoteHistoryId} aria-pressed={selectedId === item.quoteHistoryId} onClick={() => guard(() => { setSelectedId(item.quoteHistoryId); window.history.replaceState(null, '', `#/quote-status?quote=${item.quoteNumber}`) })}>
          <span className="vq-row-identity"><strong>Quote {item.quoteNumber}</strong><span className="vq-row-customer">{item.customer || 'Customer not recorded'}</span><small>{item.estimatingRep || 'Estimator unassigned'}</small></span><span className="vq-row-status"><small>Arda status</small><StatusBadge status={item.status} /></span><span className={`vq-row-date vq-row-due${isOverdue(item.estimatingDueDate, item.status) ? ' vq-overdue-text' : ''}`}><small>Estimating due</small><strong><CalendarDays size={13} />{item.estimatingDueDate ? dateOnly(item.estimatingDueDate) : 'Not scheduled'}</strong></span><span className="vq-row-date vq-row-status-set"><small>Status set</small><strong><Clock3 size={13} /><time dateTime={item.statusChangedAt ?? undefined}>{dateTime(item.statusChangedAt)}</time></strong></span><span className="vq-row-activity"><small>Activity</small><strong><GitBranch size={13} />{item.threadCount}<Mail size={13} />{item.messageCount}</strong>{item.unassignedMessageCount > 0 && <em>{item.unassignedMessageCount} to organize</em>}</span><ChevronRight className="vq-row-open" size={17} aria-hidden="true" />
        </button>)}</div>}
        {data && data.totalCount > data.pageSize && <footer className="vq-pagination"><button className="vq-icon-button" aria-label="Previous page" disabled={page === 1 || loading} onClick={() => setPage(value => value - 1)}><ArrowLeft size={15} /></button><span>Page {page} of {Math.ceil(data.totalCount / data.pageSize)}</span><button className="vq-icon-button" aria-label="Next page" disabled={page * data.pageSize >= data.totalCount || loading} onClick={() => setPage(value => value + 1)}><ArrowRight size={15} /></button></footer>}
      </section>}
      {detailLoading ? <div className="vq-detail-placeholder" role="status"><LoaderCircle size={25} className="vq-spinner" /><p>Opening quote record…</p></div> : detailError ? <div className="vq-detail-placeholder" role="alert"><p>{detailError}</p><button className="vq-button" onClick={refresh}>Try again</button><button className="vq-button" onClick={backToQuotes}>All quotes</button></div> : detail ? <QuoteDetailPanel key={detail.quote.quoteHistoryId} detail={detail} statuses={options.statuses} threadStatuses={options.threadStatuses} canManage={canManage} canRemove={Boolean(detail.quote.canRemove) && !me.isPreview} draftReset={draftReset} onBack={backToQuotes} onChanged={changed} onRecordsMoved={() => setRevision(value => value + 1)} onDirty={setDirty} guard={guard} /> : quoteFilter ? <div className="vq-detail-placeholder"><Inbox size={28} /><h2>{loading ? 'Opening quote…' : 'Quote unavailable'}</h2><p>{error || (loading ? 'Loading the shared record.' : 'This quote could not be found or you do not have access.')}</p><button className="vq-button" onClick={backToQuotes}>All quotes</button></div> : null}
    </div>
    {connect && <ConnectOutlookDialog sync={sync} syncError={syncError} canConnect={canManage} onClose={() => setConnect(false)} />}
    {pending && <Modal title="Keep your unsaved update?" subtitle="Your status, dates, summary, and activity changes have not been saved." onClose={cancel}><footer className="vq-modal-footer"><button className="vq-button" onClick={discard}>Discard changes</button><button className="vq-button vq-button-primary" onClick={cancel}>Keep editing</button></footer></Modal>}
  </div>
}
