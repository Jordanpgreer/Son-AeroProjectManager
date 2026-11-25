import { ChartNoAxesCombined, FileText, Search, UserRound, X } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { getQuoteStoreError, listQuotes } from './quoteStore'
import { ARDA_STATUS_OPTIONS, type PersonalQuote } from './quoteWorkflowApi'
import { dashboardLastPage, type DashboardQuery, type DashboardStatus } from './quoteDashboardApi'
import { useQuoteDashboard } from './useQuoteDashboard'
import { useArdaAutoRefresh } from './useArdaAutoRefresh'
import GenerateQuoteDialog from './GenerateQuoteDialog'
import QuoteFilterChip, { type QuoteFilterOption } from './QuoteFilterChip'
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
type Assignee = 'mine' | 'all' | `estimator:${string}`

const STATUS_OPTIONS: readonly QuoteFilterOption<DashboardStatus>[] = [
  { value: 'active', label: 'Active quotes' },
  { value: 'overdue', label: 'Overdue quotes' },
  { value: 'completed', label: 'Completed quotes' },
  { value: 'all', label: 'Any status' },
  ...ARDA_STATUS_OPTIONS.map((status, index) => ({ value: status, label: status, separator: index === 0 })),
]

export default function QuotesDashboardPage({ ownerAccountName, canGenerateQuotes, canViewAllQuotes = false }: {
  ownerAccountName: string; canGenerateQuotes: boolean; canViewAllQuotes?: boolean
}) {
  const [view, setView] = useState<DashboardTab>('quotes')
  const [assignee, setAssignee] = useState<Assignee>('mine')
  const [status, setStatus] = useState<DashboardStatus>('active')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [generatingQuote, setGeneratingQuote] = useState<PersonalQuote | null>(null)
  const [localRevision, setLocalRevision] = useState(0)
  const searchInput = useRef<HTMLInputElement>(null)
  const tabList = useRef<HTMLDivElement>(null)
  const tabId = useId()
  const assignedTo = canViewAllQuotes ? assignee : 'mine'
  const estimator = assignedTo.startsWith('estimator:') ? assignedTo.slice('estimator:'.length) : undefined
  const query = useMemo<DashboardQuery>(() => ({
    scope: assignedTo === 'mine' ? 'mine' : 'all', estimator, search, status, page, pageSize: 25,
  }), [assignedTo, estimator, search, status, page])
  const dashboard = useQuoteDashboard(ownerAccountName, query)
  const assigneeOptions: QuoteFilterOption<Assignee>[] = [{ value: 'mine', label: 'Me' }]
  if (canViewAllQuotes) {
    assigneeOptions.push({ value: 'all', label: 'All estimators' })
    dashboard.estimators.forEach(({ id, name }, index) => {
      assigneeOptions.push({ value: `estimator:${id}`, label: name, separator: index === 0 })
    })
  }
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
        <div className="qd-filter-chips">
          <QuoteFilterChip label="Assigned to" icon={UserRound} value={assignedTo} options={assigneeOptions} onChange={next => {
            if (next === assignedTo) return
            setAssignee(next)
            setPage(1)
          }} />
          <QuoteFilterChip label="Status" value={status} options={STATUS_OPTIONS} onChange={next => {
            if (next === status) return
            setStatus(next)
            setPage(1)
          }} />
        </div>
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
