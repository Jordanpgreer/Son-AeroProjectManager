import { ArrowLeft, ArrowRight, CalendarDays, Check, ChevronRight, GitBranch, Inbox, LoaderCircle, Mail, RefreshCw, Search, X } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { estimatingPermissions, hasEstimatingPermission, type EstimatingMe } from '../authorization'
import { StatusBadge } from './ActivityTimeline'
import ConnectOutlookDialog from './ConnectOutlookDialog'
import Modal from './Modal'
import QuoteDetailPanel from './QuoteDetailPanel'
import { loadQuoteStatusDetail, loadQuoteStatuses, loadQuoteStatusOptions, loadVendorSync } from './api'
import { dateOnly, dateTime, isOverdue, QUOTE_STATUSES, quoteFromHash, relativeTime, syncState, VENDOR_STATUSES } from './model'
import type { QuoteStatusDetail, QuoteStatusPage as PageData, VendorSync } from './types'
import useDraftGuard from './useDraftGuard'
import './quote-status.css'
import './quote-status-workspace.css'

export default function QuoteStatusPage({ me }: { me: EstimatingMe }) {
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState('')
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
  const [options, setOptions] = useState({ statuses: QUOTE_STATUSES, threadStatuses: VENDOR_STATUSES })
  const { guard, pending, cancel, discard } = useDraftGuard(dirty, () => { setDraftReset(value => value + 1); setDirty(false) })
  const canManage = !me.isPreview && hasEstimatingPermission(me, estimatingPermissions.manageQuotes)
  const health = syncState(sync)
  useEffect(() => { const timer = window.setTimeout(() => { setQuery(search); setPage(1) }, 250); return () => window.clearTimeout(timer) }, [search])
  useEffect(() => {
    function onHash() { setQuoteFilter(quoteFromHash(window.location.hash)); setPage(1) }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    setLoading(true); setError(null)
    void loadQuoteStatuses(query, status, quoteFilter, page, controller.signal).then(result => {
      if (controller.signal.aborted) return
      setData(result)
      if (quoteFilter) setSelectedId(result.items.length === 1 ? result.items[0].quoteHistoryId : null)
    }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Quotes could not be loaded.') }).finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [query, status, quoteFilter, page, revision])
  useEffect(() => {
    const controller = new AbortController()
    void loadQuoteStatusOptions(controller.signal).then(setOptions).catch(() => { /* Local labels remain usable if options cannot load. */ })
    void loadVendorSync(controller.signal).then(result => { setSync(result); setSyncError(null) }).catch(reason => { if (!controller.signal.aborted) setSyncError(reason instanceof Error ? reason.message : 'Connection status is unavailable.') })
    return () => controller.abort()
  }, [revision, connect])
  useEffect(() => {
    if (!selectedId) { setDetail(null); setDetailError(null); return }
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
  const hasFilters = Boolean(query || status || quoteFilter)
  return <div className="vq-page">
    <header className="vq-page-heading"><div className="vq-toolbar-context"><span><Check size={14} /> Shared with your estimating team</span><button className={`vq-sync vq-sync-${syncError ? 'amber' : health.tone}`} onClick={() => setConnect(true)}><i />{syncError ? 'Connection status unavailable' : health.label}<ChevronRight size={13} /></button></div><div className="vq-page-actions"><button className="vq-button" onClick={refresh} disabled={loading} aria-label="Refresh quote records"><RefreshCw size={16} /><span>Refresh</span></button><button className="vq-button" onClick={() => setConnect(true)}><Mail size={16} />Connect Outlook</button></div></header>
    <div className={`vq-workspace${selectedId ? ' vq-has-selection' : ''}${quoteFilter ? ' vq-focused' : ''}`}>
      <section className="vq-list" aria-label="Quotes"><div className="vq-list-toolbar"><div className="vq-list-title"><h2>Quotes</h2><span>{data?.totalCount ?? '—'}</span></div><label className="vq-search"><Search size={16} /><input aria-label="Search quotes, customers, parts, or vendors" placeholder="Search quotes, customers, vendors…" value={search} onChange={event => setSearch(event.target.value)} />{search && <button aria-label="Clear search" onClick={() => setSearch('')}><X size={14} /></button>}</label><label className="vq-status-filter"><span>Status</span><select value={status} onChange={event => { setStatus(event.target.value); setPage(1) }}><option value="">All statuses</option>{options.statuses.map(item => <option key={item}>{item}</option>)}</select></label>{quoteFilter && <button className="vq-filter-chip" onClick={() => { setQuoteFilter(null); setPage(1); window.history.replaceState(null, '', '#/quote-status') }}>Quote {quoteFilter}<X size={13} /></button>}</div>
        {error ? <div className="vq-empty vq-empty-small" role="alert"><p>{error}</p><button className="vq-button" onClick={refresh}>Try again</button></div> : loading ? <div className="vq-list-loading" role="status"><LoaderCircle size={22} className="vq-spinner" /><span>Loading quote records…</span></div> : !data?.items.length ? <div className="vq-empty vq-empty-small"><Inbox size={30} /><h3>{hasFilters ? 'No matching quotes' : 'Your quote record starts here'}</h3><p>{hasFilters ? 'Try another search or status.' : 'Quotes assigned to you will appear here. Once available, you can add statuses, notes, and vendor threads.'}</p>{hasFilters && <button className="vq-button" onClick={() => { setSearch(''); setStatus(''); setQuoteFilter(null); setPage(1) }}>Clear filters</button>}</div> : <div className="vq-list-items">{data.items.map(item => <button className={`vq-quote-row${selectedId === item.quoteHistoryId ? ' is-selected' : ''}`} key={item.quoteHistoryId} aria-pressed={selectedId === item.quoteHistoryId} onClick={() => guard(() => setSelectedId(item.quoteHistoryId))}>
          <div className="vq-row-top"><strong>Quote {item.quoteNumber}</strong><time title={dateTime(item.updatedAt)} dateTime={item.updatedAt}>{relativeTime(item.updatedAt)}</time></div><span className="vq-row-customer">{item.customer || 'Customer not recorded'}</span><StatusBadge status={item.status} /><div className="vq-row-bottom"><span><GitBranch size={13} />{item.threadCount} <Mail size={13} />{item.messageCount}</span>{item.followUpDate && <span className={isOverdue(item.followUpDate, item.status) ? 'vq-overdue-text' : ''}><CalendarDays size={12} />{dateOnly(item.followUpDate)}</span>}</div>{item.unassignedMessageCount > 0 && <span className="vq-unassigned-label">{item.unassignedMessageCount} email{item.unassignedMessageCount === 1 ? '' : 's'} to organize</span>}
        </button>)}</div>}
        {data && data.totalCount > data.pageSize && <footer className="vq-pagination"><button className="vq-icon-button" aria-label="Previous page" disabled={page === 1 || loading} onClick={() => setPage(value => value - 1)}><ArrowLeft size={15} /></button><span>Page {page} of {Math.ceil(data.totalCount / data.pageSize)}</span><button className="vq-icon-button" aria-label="Next page" disabled={page * data.pageSize >= data.totalCount || loading} onClick={() => setPage(value => value + 1)}><ArrowRight size={15} /></button></footer>}
      </section>
      {detailLoading ? <div className="vq-detail-placeholder" role="status"><LoaderCircle size={25} className="vq-spinner" /><p>Opening quote record…</p></div> : detailError ? <div className="vq-detail-placeholder" role="alert"><p>{detailError}</p><button className="vq-button" onClick={refresh}>Try again</button><button className="vq-button" onClick={backToQuotes}>All quotes</button></div> : detail ? <QuoteDetailPanel key={detail.quote.quoteHistoryId} detail={detail} statuses={options.statuses} threadStatuses={options.threadStatuses} canManage={canManage} draftReset={draftReset} focused={Boolean(quoteFilter)} onBack={backToQuotes} onChanged={changed} onDirty={setDirty} guard={guard} /> : <div className="vq-detail-placeholder"><div className="vq-placeholder-art"><HistoryIcon /></div><span className="vq-eyebrow">THE WHOLE PICTURE</span><h2>A place for every update</h2><p>Select a quote to see its status, track each part and vendor, and keep the full conversation in context.</p><div className="vq-placeholder-path"><span>Quote</span><ChevronRight size={13} /><span>Part</span><ChevronRight size={13} /><span>Thread</span></div>{quoteFilter && <button className="vq-button" onClick={backToQuotes}>All quotes</button>}</div>}
    </div>
    {connect && <ConnectOutlookDialog sync={sync} syncError={syncError} canConnect={canManage} onClose={() => setConnect(false)} />}
    {pending && <Modal title="Keep your unsaved update?" subtitle="Your status, dates, summary, and activity changes have not been saved." onClose={cancel}><footer className="vq-modal-footer"><button className="vq-button" onClick={discard}>Discard changes</button><button className="vq-button vq-button-primary" onClick={cancel}>Keep editing</button></footer></Modal>}
  </div>
}

function HistoryIcon() {
  return <><div><GitBranch size={25} /></div><span /><div><Mail size={20} /></div><div><Check size={19} /></div></>
}
