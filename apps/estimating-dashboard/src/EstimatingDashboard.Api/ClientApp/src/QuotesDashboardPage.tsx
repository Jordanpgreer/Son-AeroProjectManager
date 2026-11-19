import { Search, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { getQuoteStoreError, listQuotes } from './quoteStore'
import { ARDA_STATUS_OPTIONS, type PersonalQuote } from './quoteWorkflowApi'
import { dashboardLastPage, type DashboardQuery, type DashboardScope, type DashboardStatus } from './quoteDashboardApi'
import { dashboardPeriod, periodDescription, type DashboardPeriod } from './quoteDashboardPeriods'
import { useQuoteDashboard } from './useQuoteDashboard'
import { useArdaAutoRefresh } from './useArdaAutoRefresh'
import GenerateQuoteDialog from './GenerateQuoteDialog'
import QuoteDashboardDateFilter from './QuoteDashboardDateFilter'
import QuoteDashboardSummary from './QuoteDashboardSummary'
import QuoteDashboardTable from './QuoteDashboardTable'
import QuoteDashboardDetail from './QuoteDashboardDetail'
import './quote-dashboard.css'
import './quote-dashboard-states.css'
import './quote-dashboard-view.css'

export default function QuotesDashboardPage({ ownerAccountName, canGenerateQuotes, canViewAllQuotes = false }: {
  ownerAccountName: string; canManageQuotes: boolean; canDeleteQuotes: boolean
  canGenerateQuotes: boolean; canViewAllQuotes?: boolean
}) {
  const [scope, setScope] = useState<DashboardScope>(() => canViewAllQuotes ? 'all' : 'mine')
  const [period, setPeriod] = useState<DashboardPeriod>(() => dashboardPeriod('this-month'))
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<DashboardStatus>('all')
  const [page, setPage] = useState(1)
  const [selection, setSelection] = useState<{ id: number; key: string } | null>(null)
  const [generatingQuote, setGeneratingQuote] = useState<PersonalQuote | null>(null)
  const [localRevision, setLocalRevision] = useState(0)
  const rowTrigger = useRef<HTMLElement | null>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  const effectiveScope = canViewAllQuotes ? scope : 'mine'
  const query = useMemo<DashboardQuery>(() => ({
    scope: effectiveScope, from: period.from, to: period.to, search, status, page, pageSize: 25,
  }), [effectiveScope, period.from, period.to, search, status, page])
  const dashboard = useQuoteDashboard(ownerAccountName, query)
  const selectedQuote = selection?.key === dashboard.key ? dashboard.data?.records.find(quote => quote.id === selection.id) : undefined
  const localQuotes = useMemo(() => listQuotes(ownerAccountName), [ownerAccountName, localRevision])
  const storageError = getQuoteStoreError()
  useArdaAutoRefresh(dashboard.refresh, !dashboard.loading && !generatingQuote)
  useEffect(() => {
    if (!dashboard.data || dashboard.loading) return
    const lastPage = dashboardLastPage(dashboard.data.total, dashboard.data.pageSize)
    if (page > lastPage) { setSelection(null); setPage(lastPage) }
  }, [dashboard.data, dashboard.loading, page])
  function closeDetails() {
    setSelection(null)
    if (rowTrigger.current?.isConnected) rowTrigger.current.focus()
    else searchInput.current?.focus()
  }
  useEffect(() => {
    if (!selectedQuote || generatingQuote) return
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented) { event.preventDefault(); closeDetails() }
    }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [selectedQuote, generatingQuote])
  function changeStatus(next: DashboardStatus) { setSelection(null); setStatus(next); setPage(1) }
  const firstRecord = dashboard.data?.total ? (dashboard.data.page - 1) * dashboard.data.pageSize + 1 : 0
  const lastRecord = dashboard.data ? firstRecord + dashboard.data.records.length - (firstRecord ? 1 : 0) : 0
  return <div className="quote-dashboard-page qd-dashboard">
    <div className="qd-heading">
      <div className="qd-scope" role="group" aria-label="Quote scope">
        <button type="button" aria-pressed={effectiveScope === 'mine'} onClick={() => { setSelection(null); setScope('mine'); setPage(1) }}>My quotes</button>
        {canViewAllQuotes && <button type="button" aria-pressed={effectiveScope === 'all'} onClick={() => { setSelection(null); setScope('all'); setPage(1) }}>All quotes</button>}
      </div>
    </div>
    <div className="qd-filters">
      <div className="qd-search-wrap"><label className="qd-search">
        <Search size={20} aria-hidden="true" /><input ref={searchInput} type="search" aria-label="Search part number, quote number, or customer" placeholder="Search part number, quote number, or customer…"
          value={search} maxLength={200} onChange={event => { setSelection(null); setSearch(event.target.value); setPage(1) }} />
        {search && <button type="button" aria-label="Clear quote search" onClick={() => { setSelection(null); setSearch(''); setPage(1); searchInput.current?.focus() }}><X size={15} /></button>}
      </label><small>Filters as you type</small></div>
      <QuoteDashboardDateFilter value={period} onChange={next => { setSelection(null); setPeriod(next); setPage(1) }} />
    </div>
    <QuoteDashboardSummary scope={effectiveScope} period={periodDescription(period)} stats={dashboard.stats} selected={status} onSelect={changeStatus} />
    <div className="qd-result-tools">
      <p role="status" aria-live="polite">{dashboard.data
        ? <>{firstRecord > 0 ? `${firstRecord.toLocaleString()}–${lastRecord.toLocaleString()}` : '0'} of {dashboard.data.total.toLocaleString()} quotes</>
        : dashboard.loading ? 'Loading quotes…' : 'Quotes unavailable'} <span>· {effectiveScope === 'all' ? 'All estimators' : 'Assigned to me'}</span>{dashboard.loading && dashboard.data && <small>Updating…</small>}</p>
      <label><span className="sr-only">Quote status</span><select aria-label="Quote status" value={status} onChange={event => changeStatus(event.target.value as DashboardStatus)}>
        <option value="all">Any status</option><optgroup label="Quote groups"><option value="active">Active quotes</option><option value="overdue">Overdue quotes</option><option value="completed">Completed quotes</option></optgroup>
        <optgroup label="Arda status">{ARDA_STATUS_OPTIONS.map(value => <option key={value} value={value}>{value}</option>)}</optgroup>
      </select></label>
    </div>
    {dashboard.error && <p className="qd-error" role="alert">{dashboard.error}{dashboard.data && ' Showing the previous results for this view.'}</p>}
    <div className={`qd-workspace${selectedQuote ? ' has-detail' : ''}`}>
      <QuoteDashboardTable data={dashboard.data} loading={dashboard.loading} unavailable={!!dashboard.error} selectedId={selectedQuote?.id ?? null}
        onSelect={(quote, trigger) => { rowTrigger.current = trigger; setSelection({ id: quote.id, key: dashboard.key }) }}
        onPage={next => { setSelection(null); setPage(next) }} />
      {selectedQuote && <QuoteDashboardDetail quote={selectedQuote} linkedQuotes={localQuotes.filter(local => local.sourceQuote?.quoteHistoryId === selectedQuote.id)}
        canGenerate={canGenerateQuotes} storageError={!!storageError} onClose={closeDetails} onGenerate={() => setGeneratingQuote(selectedQuote)} />}
    </div>
    {storageError && <p className="quote-storage-error" role="alert">{storageError}</p>}
    {generatingQuote && <GenerateQuoteDialog quote={generatingQuote} ownerAccountName={ownerAccountName}
      onClose={() => { setGeneratingQuote(null); setLocalRevision(current => current + 1) }} />}
  </div>
}
