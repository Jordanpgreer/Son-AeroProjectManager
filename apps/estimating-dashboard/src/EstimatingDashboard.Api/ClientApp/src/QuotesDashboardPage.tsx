import { ChartNoAxesCombined, ChevronDown, FileText, Search, UserRound, X } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { getQuoteStoreError, listQuotes } from './quoteStore'
import { ARDA_STATUS_OPTIONS, type PersonalQuote } from './quoteWorkflowApi'
import { dashboardLastPage, type DashboardQuery, type DashboardScope, type DashboardStatus } from './quoteDashboardApi'
import { useQuoteDashboard } from './useQuoteDashboard'
import { useArdaAutoRefresh } from './useArdaAutoRefresh'
import GenerateQuoteDialog from './GenerateQuoteDialog'
import QuoteDashboardTable from './QuoteDashboardTable'
import QuotesPerformance from './QuotesPerformance'
import './quote-dashboard.css'
import './quote-dashboard-states.css'
import './quote-dashboard-view.css'

const DASHBOARD_TABS = [
  { id: 'quotes', label: 'Quotes', icon: FileText },
  { id: 'performance', label: 'Performance', icon: ChartNoAxesCombined },
] as const

type DashboardTab = typeof DASHBOARD_TABS[number]['id']
type QuoteFilter = `${DashboardScope}:${DashboardStatus}`

const QUOTE_FILTERS = (['mine', 'all'] as const).map(scope => {
  const prefix = scope === 'mine' ? 'My' : 'All'
  const options: { status: DashboardStatus; label: string }[] = [
    { status: 'active', label: `${prefix} active quotes` },
    { status: 'all', label: scope === 'mine' ? 'All my quotes' : 'All quotes' },
    { status: 'overdue', label: `${prefix} overdue quotes` },
    { status: 'completed', label: `${prefix} completed quotes` },
    ...ARDA_STATUS_OPTIONS.map(status => ({ status, label: `${prefix} quotes · ${status}` })),
  ]
  return { scope, options }
})

export default function QuotesDashboardPage({ ownerAccountName, canGenerateQuotes, canViewAllQuotes = false }: {
  ownerAccountName: string; canGenerateQuotes: boolean; canViewAllQuotes?: boolean
}) {
  const [view, setView] = useState<DashboardTab>('quotes')
  const [filter, setFilter] = useState<QuoteFilter>('mine:active')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [generatingQuote, setGeneratingQuote] = useState<PersonalQuote | null>(null)
  const [localRevision, setLocalRevision] = useState(0)
  const searchInput = useRef<HTMLInputElement>(null)
  const tabList = useRef<HTMLDivElement>(null)
  const tabId = useId()
  const effectiveFilter = !canViewAllQuotes && filter.startsWith('all:') ? 'mine:active' : filter
  const [scope, status] = effectiveFilter.split(':') as [DashboardScope, DashboardStatus]
  const query = useMemo<DashboardQuery>(() => ({
    scope, search, status, page, pageSize: 25,
  }), [scope, search, status, page])
  const dashboard = useQuoteDashboard(ownerAccountName, query)
  const localQuotes = useMemo(() => listQuotes(ownerAccountName), [ownerAccountName, localRevision])
  const storageError = getQuoteStoreError()
  useArdaAutoRefresh(dashboard.refresh, view === 'quotes' && !dashboard.loading && !generatingQuote)
  useEffect(() => {
    if (!dashboard.data || dashboard.loading) return
    const lastPage = dashboardLastPage(dashboard.data.total, dashboard.data.pageSize)
    if (page > lastPage) setPage(lastPage)
  }, [dashboard.data, dashboard.loading, page])
  function moveTabFocus(event: ReactKeyboardEvent<HTMLButtonElement>, index: number) {
    const tabCount = DASHBOARD_TABS.length
    let target: number
    switch (event.key) {
      case 'ArrowLeft': target = (index + tabCount - 1) % tabCount; break
      case 'ArrowRight': target = (index + 1) % tabCount; break
      case 'Home': target = 0; break
      case 'End': target = tabCount - 1; break
      default: return
    }
    event.preventDefault()
    tabList.current?.querySelectorAll('button')[target]?.focus()
  }
  const total = dashboard.data?.total
  return <div className="quote-dashboard-page qd-dashboard">
    <div className="qd-tabs" role="tablist" aria-label="Quotes dashboard" ref={tabList}>
      {DASHBOARD_TABS.map(({ id, label, icon: Icon }, index) => (
        <button key={id} type="button" id={`${tabId}-${id}-tab`} role="tab" aria-selected={view === id}
          aria-controls={`${tabId}-${id}-panel`} tabIndex={view === id ? 0 : -1}
          onClick={() => setView(id)} onKeyDown={event => moveTabFocus(event, index)}>
          <Icon size={19} aria-hidden="true" />{label}
        </button>
      ))}
    </div>
    <section className="qd-quote-panel" id={`${tabId}-quotes-panel`} role="tabpanel"
      aria-labelledby={`${tabId}-quotes-tab`} hidden={view !== 'quotes'}>
      <label className="qd-search">
        <Search size={20} aria-hidden="true" /><input ref={searchInput} type="search" aria-label="Search part number, quote number, or customer" placeholder="Search part number, quote number, or customer…"
          value={search} maxLength={200} onChange={event => { setSearch(event.target.value); setPage(1) }} />
        {search && <button type="button" aria-label="Clear quote search" onClick={() => { setSearch(''); setPage(1); searchInput.current?.focus() }}><X size={15} /></button>}
      </label>
      <div className="qd-result-tools">
        <label className="qd-filter">
          <UserRound size={17} aria-hidden="true" />
          <select aria-label="Quote filter" value={effectiveFilter} onChange={event => {
            setFilter(event.target.value as QuoteFilter)
            setPage(1)
          }}>
            {QUOTE_FILTERS.filter(group => group.scope === 'mine' || canViewAllQuotes).map(group => (
              <optgroup key={group.scope} label={group.scope === 'mine' ? 'Assigned to me' : 'All estimators'}>
                {group.options.map(option => <option key={option.status} value={`${group.scope}:${option.status}`}>{option.label}</option>)}
              </optgroup>
            ))}
          </select>
          <ChevronDown size={16} aria-hidden="true" />
        </label>
        <p role="status">
          {total === undefined ? dashboard.loading ? 'Loading quotes…' : 'Quotes unavailable' : `${total.toLocaleString()} ${total === 1 ? 'quote' : 'quotes'}`}
          {' · All time'}
        </p>
      </div>
      {dashboard.error && <p className="qd-error" role="alert">{dashboard.error}{dashboard.data && ' Showing the previous results for this view.'}</p>}
      <QuoteDashboardTable data={dashboard.data} loading={dashboard.loading} unavailable={!!dashboard.error}
        localQuotes={localQuotes} canGenerate={canGenerateQuotes} storageError={!!storageError} onGenerate={setGeneratingQuote} onPage={setPage} />
      {storageError && <p className="quote-storage-error" role="alert">{storageError}</p>}
      {generatingQuote && <GenerateQuoteDialog quote={generatingQuote} ownerAccountName={ownerAccountName}
        onClose={() => { setGeneratingQuote(null); setLocalRevision(current => current + 1) }} />}
    </section>
    <section role="tabpanel" id={`${tabId}-performance-panel`} aria-labelledby={`${tabId}-performance-tab`}
      hidden={view !== 'performance'} tabIndex={0}>
      {view === 'performance' && <QuotesPerformance ownerAccountName={ownerAccountName} canViewAllQuotes={canViewAllQuotes} />}
    </section>
  </div>
}
