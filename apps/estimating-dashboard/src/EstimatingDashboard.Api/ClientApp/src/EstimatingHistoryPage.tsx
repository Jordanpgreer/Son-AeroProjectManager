import {
  AlertTriangle,
  BarChart3,
  CalendarCheck,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Download,
  FileSpreadsheet,
  Filter,
  RefreshCw,
  Search,
  Upload,
  Users,
  X,
} from 'lucide-react'
import { useDeferredValue, useEffect, useState, type ReactNode } from 'react'
import './estimating-history.css'

interface HistoryRecord {
  id: number
  sourceId: string
  quoteNumber: number
  customer: string
  customerContact: string | null
  salesPerson: string
  quoteStatus: string
  rfqReferenceNumber: string | null
  estimatingRep: string
  totalValue: number
  rfqDueDate: string | null
  dateToEstimating: string | null
  issues: string | null
  quoteOnTrack: string | null
  quoteComplexity: string | null
  numberOfParts: number
  estimatingStatus: string | null
  estimatingCompletionDate: string | null
  onTimeStatus: 'OnTime' | 'Late' | 'NoData'
  daysLate: number
  workdays: number | null
  isCompleted: boolean
}

interface HistoryPage {
  records: HistoryRecord[]
  total: number
  page: number
  pageSize: number
}

interface UserStats {
  estimator: string
  inQueue: number
  completedThisWeek: number
  completedThisMonth: number
  completedAllTime: number
  totalQuoteValue: number
  completedQuoteValue: number
  averageCompletionWorkdays: number | null
  completedInPeriod: number
  completedValueInPeriod: number
  onTimeInPeriod: number
  lateInPeriod: number
  averageCompletionWorkdaysInPeriod: number | null
}

interface DepartmentStats extends Omit<UserStats, 'estimator'> {}

interface HistoryDashboard {
  generatedAt: string
  period: 'week' | 'month' | 'all'
  periodLabel: string
  periodStart: string | null
  periodEnd: string | null
  isTeamView: boolean
  department: DepartmentStats
  users: UserStats[]
}

interface FilterOptions {
  estimators: string[]
  salesPersons?: string[]
  customers: string[]
  quoteStatuses: string[]
}

interface ImportIssue {
  row: number
  column: string | null
  message: string
}

interface ImportChange {
  row: number
  sourceId: string
  quoteNumber: number
  customer: string
  changeType: 'New' | 'Updated'
}

interface ImportValidation {
  reviewId: string
  expiresAt: string
  fileName: string
  totalRows: number
  newRecords: number
  updatedRecords: number
  unchangedRecords: number
  errorRows: number
  errors: ImportIssue[]
  changes: ImportChange[]
  canApply: boolean
}

interface Filters {
  search: string
  estimator: string
  salesPerson: string
  customer: string
  quoteStatus: string
  onTime: string
  dueFrom: string
  dueTo: string
}

const emptyFilters: Filters = {
  search: '',
  estimator: '',
  salesPerson: '',
  customer: '',
  quoteStatus: '',
  onTime: '',
  dueFrom: '',
  dueTo: '',
}

const emptyOptions: FilterOptions = {
  estimators: [],
  salesPersons: [],
  customers: [],
  quoteStatuses: [],
}

type StatsPeriod = 'week' | 'month' | 'all'
type SummaryPreset = 'queue' | 'completed' | 'onTime' | 'late' | null
const historyPageSize = 50

function currency(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 0,
  })
}

function date(value: string | null) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(new Date(value))
}

function inclusivePeriodEnd(value: string | null) {
  if (!value) return null
  const [year, month, day] = value.slice(0, 10).split('-').map(Number)
  const date = new Date(Date.UTC(year, month - 1, day))
  date.setUTCDate(date.getUTCDate() - 1)
  return date.toISOString().slice(0, 10)
}

function measuredPercentage(value: number, remainder: number) {
  const measured = value + remainder
  return measured === 0 ? '0%' : `${Math.round((value / measured) * 100)}%`
}

function onTimeLabel(record: Pick<HistoryRecord, 'onTimeStatus' | 'daysLate'>) {
  if (record.onTimeStatus === 'OnTime') return 'On time'
  if (record.onTimeStatus === 'NoData') return 'No data'
  return `${record.daysLate}d late`
}

function HighlightText({ value, query }: { value: string | number; query: string }) {
  const text = String(value)
  const normalizedQuery = query.trim().toLocaleLowerCase()
  if (!normalizedQuery) return <>{text}</>

  const normalizedText = text.toLocaleLowerCase()
  const parts: ReactNode[] = []
  let cursor = 0
  let matchIndex = normalizedText.indexOf(normalizedQuery)

  while (matchIndex >= 0) {
    if (matchIndex > cursor) {
      parts.push(<span key={`text-${cursor}`}>{text.slice(cursor, matchIndex)}</span>)
    }
    parts.push(
      <mark key={`match-${matchIndex}`}>
        {text.slice(matchIndex, matchIndex + normalizedQuery.length)}
      </mark>,
    )
    cursor = matchIndex + normalizedQuery.length
    matchIndex = normalizedText.indexOf(normalizedQuery, cursor)
  }

  if (cursor < text.length) {
    parts.push(<span key={`text-${cursor}`}>{text.slice(cursor)}</span>)
  }

  return <>{parts}</>
}

function historyQueryParams({
  page,
  pageSize,
  view,
  sort,
  direction,
  filters,
  search,
  summaryPreset,
  periodStart,
  periodEnd,
}: {
  page?: number
  pageSize?: number
  view: 'live' | 'history'
  sort: string
  direction: string
  filters: Filters
  search: string
  summaryPreset: SummaryPreset
  periodStart: string | null
  periodEnd: string | null
}) {
  const params = new URLSearchParams({ view, sort, direction })
  if (page !== undefined) params.set('page', String(page))
  if (pageSize !== undefined) params.set('pageSize', String(pageSize))

  const values: Record<string, string> = { ...filters, search }
  for (const [key, value] of Object.entries(values)) {
    if (value.trim()) params.set(key, value.trim())
  }
  if (summaryPreset && summaryPreset !== 'queue') {
    params.set('completion', 'completed')
    if (periodStart) params.set('completedFrom', periodStart.slice(0, 10))
    const completedTo = inclusivePeriodEnd(periodEnd)
    if (completedTo) params.set('completedTo', completedTo)
    if (summaryPreset === 'onTime') params.set('onTime', 'OnTime')
    if (summaryPreset === 'late') params.set('onTime', 'Late')
  }
  return params
}

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(url, { credentials: 'include', ...init })
  } catch {
    throw new Error('Could not reach the Estimating service. Confirm the local application is running, then try again.')
  }
  if (!response.ok) {
    const payload = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(payload?.message ?? `Request failed with status ${response.status}.`)
  }
  return response.json() as Promise<T>
}

function SelectFilter({
  label,
  value,
  options,
  onChange,
}: {
  label: string
  value: string
  options: string[]
  onChange: (value: string) => void
}) {
  return (
    <label>
      <span>{label}</span>
      <select value={value} onChange={(event) => onChange(event.currentTarget.value)}>
        <option value="">All</option>
        {options.map((option) => <option key={option} value={option}>{option}</option>)}
      </select>
    </label>
  )
}

function SortButton({
  label,
  field,
  sort,
  direction,
  onSort,
}: {
  label: string
  field: string
  sort: string
  direction: string
  onSort: (field: string) => void
}) {
  return (
    <button type="button" onClick={() => onSort(field)}>
      {label}
      {sort === field && <span aria-label={direction === 'asc' ? 'ascending' : 'descending'}>
        {direction === 'asc' ? '↑' : '↓'}
      </span>}
    </button>
  )
}

export default function EstimatingHistoryPage({
  canImport,
  importOpen,
  onImportOpenChange,
}: {
  canImport: boolean
  importOpen: boolean
  onImportOpenChange: (open: boolean) => void
}) {
  const [dashboard, setDashboard] = useState<HistoryDashboard | null>(null)
  const [pageData, setPageData] = useState<HistoryPage>({ records: [], total: 0, page: 1, pageSize: historyPageSize })
  const [options, setOptions] = useState<FilterOptions>(emptyOptions)
  const [filters, setFilters] = useState<Filters>(emptyFilters)
  const [page, setPage] = useState(1)
  const [view, setView] = useState<'live' | 'history'>('live')
  const [statsPeriod, setStatsPeriod] = useState<StatsPeriod>('week')
  const [summaryPreset, setSummaryPreset] = useState<SummaryPreset>('queue')
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [reportBusy, setReportBusy] = useState(false)
  const [summaryExportBusy, setSummaryExportBusy] = useState(false)
  const [reportError, setReportError] = useState<string | null>(null)
  const [sort, setSort] = useState('due')
  const [direction, setDirection] = useState('asc')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [importFile, setImportFile] = useState<File | null>(null)
  const [validation, setValidation] = useState<ImportValidation | null>(null)
  const [importBusy, setImportBusy] = useState(false)
  const [importError, setImportError] = useState<string | null>(null)
  const [forceConfirm, setForceConfirm] = useState(false)
  const [revision, setRevision] = useState(0)
  const [appliedSearch, setAppliedSearch] = useState('')
  const deferredSearch = useDeferredValue(filters.search)

  useEffect(() => {
    let active = true
    void Promise.all([
      api<HistoryDashboard>(`/api/quote-history/dashboard?period=${statsPeriod}`),
      api<FilterOptions>('/api/quote-history/filters'),
    ]).then(([nextDashboard, nextOptions]) => {
      if (!active) return
      setDashboard(nextDashboard)
      setOptions(nextOptions)
      setError(null)
    }).catch((cause) => {
      if (active) setError(cause instanceof Error ? cause.message : 'Unable to load estimating history.')
    })
    return () => { active = false }
  }, [revision, statsPeriod])

  useEffect(() => {
    let active = true
    const requestSearch = deferredSearch.trim()
    const params = historyQueryParams({
      page,
      pageSize: historyPageSize,
      view,
      sort,
      direction,
      filters,
      search: deferredSearch,
      summaryPreset,
      periodStart: dashboard?.periodStart ?? null,
      periodEnd: dashboard?.periodEnd ?? null,
    })
    setLoading(true)
    void api<HistoryPage>(`/api/quote-history?${params.toString()}`)
      .then((result) => {
        if (!active) return
        setPageData(result)
        setAppliedSearch(requestSearch)
        setError(null)
      })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to load quote records.')
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => { active = false }
  }, [
    dashboard?.periodEnd,
    dashboard?.periodStart,
    deferredSearch,
    direction,
    filters.customer,
    filters.dueFrom,
    filters.dueTo,
    filters.estimator,
    filters.onTime,
    filters.quoteStatus,
    filters.salesPerson,
    page,
    revision,
    sort,
    summaryPreset,
    view,
  ])

  const updateFilter = (key: keyof Filters, value: string) => {
    setFilters((current) => ({ ...current, [key]: value }))
    setPage(1)
  }

  const updateSort = (field: string) => {
    if (field === sort) setDirection((current) => current === 'asc' ? 'desc' : 'asc')
    else {
      setSort(field)
      setDirection('asc')
    }
    setPage(1)
  }

  const updateView = (nextView: 'live' | 'history') => {
    setView(nextView)
    setSummaryPreset(nextView === 'live' ? 'queue' : null)
    setSort(nextView === 'live' ? 'due' : 'number')
    setDirection(nextView === 'live' ? 'asc' : 'desc')
    setPage(1)
  }

  const applySummaryPreset = (preset: Exclude<SummaryPreset, null>) => {
    if (summaryPreset === preset) {
      setSummaryPreset(null)
      if (preset === 'queue') {
        setView('history')
        setSort('number')
        setDirection('desc')
      }
      setPage(1)
      return
    }
    setSummaryPreset(preset)
    if (preset === 'queue') {
      setView('live')
      setSort('due')
      setDirection('asc')
    } else {
      setView('history')
      setSort('completed')
      setDirection('desc')
    }
    setPage(1)
  }

  const closeImport = () => {
    if (importBusy) return
    onImportOpenChange(false)
    setImportFile(null)
    setValidation(null)
    setImportError(null)
    setForceConfirm(false)
  }

  const validateImport = async () => {
    if (!importFile) return
    setImportBusy(true)
    setImportError(null)
    setValidation(null)
    try {
      const form = new FormData()
      form.append('file', importFile)
      setValidation(await api<ImportValidation>('/api/quote-history/import/validate', {
        method: 'POST',
        body: form,
      }))
    } catch (cause) {
      setImportError(cause instanceof Error ? cause.message : 'Workbook validation failed.')
    } finally {
      setImportBusy(false)
    }
  }

  const applyImport = async (continueWithErrors: boolean) => {
    if (!validation) return
    setImportBusy(true)
    setImportError(null)
    try {
      await api(`/api/quote-history/import/${validation.reviewId}/apply`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ continueWithErrors }),
      })
      closeImport()
      setRevision((current) => current + 1)
    } catch (cause) {
      setImportError(cause instanceof Error ? cause.message : 'Unable to apply the reviewed import.')
    } finally {
      setImportBusy(false)
    }
  }

  const downloadFilteredResults = async () => {
    setReportBusy(true)
    setReportError(null)
    try {
      const params = historyQueryParams({
        view,
        sort,
        direction,
        filters,
        search: filters.search,
        summaryPreset,
        periodStart: dashboard?.periodStart ?? null,
        periodEnd: dashboard?.periodEnd ?? null,
      })
      const response = await fetch(`/api/quote-history/export?${params.toString()}`, {
        credentials: 'include',
      })
      if (!response.ok) {
        const payload = await response.json().catch(() => null) as { message?: string } | null
        throw new Error(payload?.message ?? 'Unable to export the current quote results.')
      }
      const disposition = response.headers.get('content-disposition') ?? ''
      const fileName = disposition.match(/filename\*?=(?:UTF-8'')?"?([^";]+)/i)?.[1]
        ?? `estimating-${view === 'live' ? 'active-quotes' : 'quote-history'}-${new Date().toISOString().slice(0, 10)}.xlsx`
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url
      link.download = decodeURIComponent(fileName)
      document.body.append(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (cause) {
      setReportError(cause instanceof Error ? cause.message : 'Unable to export the current quote results.')
    } finally {
      setReportBusy(false)
    }
  }

  const downloadEstimatorSummary = async () => {
    setSummaryExportBusy(true)
    setReportError(null)
    try {
      const response = await fetch(`/api/quote-history/estimator-summary?period=${statsPeriod}`, {
        credentials: 'include',
      })
      if (!response.ok) {
        const payload = await response.json().catch(() => null) as { message?: string } | null
        throw new Error(payload?.message ?? 'Unable to export estimator statistics.')
      }
      const disposition = response.headers.get('content-disposition') ?? ''
      const fileName = disposition.match(/filename\*?=(?:UTF-8'')?"?([^";]+)/i)?.[1]
        ?? `estimator-summary-${statsPeriod}-${new Date().toISOString().slice(0, 10)}.pdf`
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url
      link.download = decodeURIComponent(fileName)
      document.body.append(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (cause) {
      setReportError(cause instanceof Error ? cause.message : 'Unable to export estimator statistics.')
    } finally {
      setSummaryExportBusy(false)
    }
  }

  const department = dashboard?.department
  const totalPages = Math.max(1, Math.ceil(pageData.total / pageData.pageSize))
  const searchPending = filters.search.trim().toLocaleLowerCase() !== appliedSearch.toLocaleLowerCase()
  const activeFilterCount = [filters.estimator, filters.salesPerson, filters.customer, filters.quoteStatus, filters.onTime]
    .filter((value) => value.trim())
    .length + (filters.dueFrom || filters.dueTo ? 1 : 0)

  return (
    <div className="history-page">
      {error && <div className="history-error" role="alert">
        <AlertTriangle size={17} aria-hidden="true" />
        {error}
      </div>}

      <section className="history-stats-toolbar" aria-label="Statistics controls">
        <div className="history-stats-heading">
          <span className="section-kicker">{dashboard?.isTeamView ? 'Department statistics' : 'Your statistics'}</span>
          <div className="history-period-tabs" role="group" aria-label="Statistics time period">
            {([['week', 'This week'], ['month', 'This month'], ['all', 'All time']] as const).map(([period, label]) => (
              <button key={period} type="button" aria-pressed={statsPeriod === period} onClick={() => setStatsPeriod(period)}>{label}</button>
            ))}
          </div>
        </div>
        {canImport && <button type="button" className="history-import-button" onClick={() => onImportOpenChange(true)}>
          <Upload size={16} aria-hidden="true" />
          Import Excel
        </button>}
      </section>

      {reportError && <div className="history-error" role="alert">
        <AlertTriangle size={17} aria-hidden="true" /> {reportError}
      </div>}

      <section className="history-kpis" aria-label="Estimating history statistics">
        <button type="button" className={summaryPreset === 'queue' ? 'selected' : ''} aria-pressed={summaryPreset === 'queue'} onClick={() => applySummaryPreset('queue')}>
          <span><Clock3 size={17} aria-hidden="true" /> Active quotes</span>
          <strong>{department?.inQueue ?? 0}</strong>
          <small>Needs Approval</small>
        </button>
        <button type="button" className={summaryPreset === 'completed' ? 'selected' : ''} aria-pressed={summaryPreset === 'completed'} onClick={() => applySummaryPreset('completed')}>
          <span><CheckCircle2 size={17} aria-hidden="true" /> Completed</span>
          <strong>{department?.completedInPeriod ?? 0}</strong>
          <small>{dashboard?.periodLabel ?? 'This week'}</small>
        </button>
        <button type="button" className={summaryPreset === 'onTime' ? 'selected' : ''} aria-pressed={summaryPreset === 'onTime'} onClick={() => applySummaryPreset('onTime')}>
          <span><CalendarCheck size={17} aria-hidden="true" /> On time</span>
          <span className="history-kpi-metric"><strong>{department?.onTimeInPeriod ?? 0}</strong><b>{measuredPercentage(department?.onTimeInPeriod ?? 0, department?.lateInPeriod ?? 0)}</b></span>
          <small>Completed within target</small>
        </button>
        <button type="button" className={summaryPreset === 'late' ? 'selected' : ''} aria-pressed={summaryPreset === 'late'} onClick={() => applySummaryPreset('late')}>
          <span><BarChart3 size={17} aria-hidden="true" /> Late</span>
          <span className="history-kpi-metric"><strong>{department?.lateInPeriod ?? 0}</strong><b>{measuredPercentage(department?.lateInPeriod ?? 0, department?.onTimeInPeriod ?? 0)}</b></span>
          <small>Completed after due date</small>
        </button>
        <div className="history-kpi-static">
          <span><Clock3 size={17} aria-hidden="true" /> Avg. completion</span>
          <strong>{department?.averageCompletionWorkdaysInPeriod == null ? '—' : `${department.averageCompletionWorkdaysInPeriod}d`}</strong>
          <small>Inclusive business days</small>
        </div>
      </section>

      <section className="estimator-stats-card" aria-labelledby="estimator-stats-heading">
        <div className="history-card-heading">
          <div>
            <span className="section-kicker">{dashboard?.isTeamView ? 'Team performance' : 'Personal performance'}</span>
            <h2 id="estimator-stats-heading">{dashboard?.isTeamView ? 'Estimator Statistics' : 'Your Estimating Statistics'}</h2>
          </div>
          <div className="estimator-stats-actions">
            <span className="history-updated"><Users size={15} aria-hidden="true" /> {dashboard?.isTeamView ? `${dashboard.users.length} estimators` : 'Private view'}</span>
            <button
              type="button"
              className="history-export-button estimator-summary-export"
              disabled={summaryExportBusy || !dashboard}
              onClick={() => void downloadEstimatorSummary()}
            >
              <Download size={16} aria-hidden="true" />
              {summaryExportBusy ? 'Exporting…' : 'Export estimator stats'}
            </button>
          </div>
        </div>
        {dashboard?.users.length ? (
          <div className="estimator-stat-grid">
            {dashboard.users.map((user) => <button
              key={user.estimator}
              type="button"
              aria-pressed={filters.estimator === user.estimator}
              aria-label={`${filters.estimator === user.estimator ? 'Remove' : 'Apply'} estimator filter for ${user.estimator}`}
              onClick={() => updateFilter('estimator', filters.estimator === user.estimator ? '' : user.estimator)}
            >
              <div className="estimator-stat-name">
                <span>{user.estimator.slice(0, 1).toUpperCase()}</span>
                <strong>{user.estimator}</strong>
              </div>
              <dl>
                <div><dt>Queue</dt><dd>{user.inQueue}</dd></div>
                <div><dt>Completed</dt><dd>{user.completedInPeriod}</dd></div>
                <div><dt>On time</dt><dd>{measuredPercentage(user.onTimeInPeriod, user.lateInPeriod)}</dd></div>
                <div><dt>Late</dt><dd>{user.lateInPeriod}</dd></div>
                <div><dt>Completed value</dt><dd>{currency(user.completedValueInPeriod)}</dd></div>
                <div><dt>Avg. time</dt><dd>{user.averageCompletionWorkdaysInPeriod == null ? '—' : `${user.averageCompletionWorkdaysInPeriod} days`}</dd></div>
              </dl>
            </button>)}
          </div>
        ) : <div className="history-empty compact">
          <Users size={27} aria-hidden="true" />
          <strong>No estimator statistics yet</strong>
          <span>Fulcrum quotes refresh automatically at 2:00 AM and 7:00 PM Mountain time. You can also import a Fulcrum export or Daily Quote Log workbook.</span>
        </div>}
      </section>

      <section className="history-register-card" aria-labelledby="history-register-heading" aria-busy={loading || searchPending}>
        <div className="history-card-heading register-heading">
          <div className="history-register-title">
            <div>
              <span className="section-kicker">{view === 'live' ? 'Daily quote log' : 'Normalized history'}</span>
              <h2 id="history-register-heading">{view === 'live' ? 'Live Estimating Queue' : 'Quote History'}</h2>
            </div>
            <div className="history-register-tabs" role="tablist" aria-label="Quote register view">
              <button type="button" role="tab" aria-selected={view === 'live'} onClick={() => updateView('live')}>Active quotes</button>
              <button type="button" role="tab" aria-selected={view === 'history'} onClick={() => updateView('history')}>All quotes</button>
            </div>
          </div>
          <div className="history-register-tools">
            <label className="history-search">
              <Search size={16} aria-hidden="true" />
              <input
                type="search"
                value={filters.search}
                placeholder="Search quotes, customers, estimators, salespeople…"
                aria-label="Search estimating quotes"
                onChange={(event) => updateFilter('search', event.currentTarget.value)}
              />
            </label>
            <button type="button" className="history-filter-toggle" aria-expanded={filtersOpen} onClick={() => setFiltersOpen((current) => !current)}>
              <Filter size={15} aria-hidden="true" />
              <span>Filter results</span>
              {activeFilterCount > 0 && <b>{activeFilterCount}</b>}
              <ChevronDown className="history-filter-chevron" size={14} aria-hidden="true" />
            </button>
            <button type="button" className="history-export-button" disabled={reportBusy} onClick={() => void downloadFilteredResults()}>
              <Download size={16} aria-hidden="true" />
              {reportBusy ? 'Exporting…' : 'Export results'}
            </button>
          </div>
        </div>

        {filtersOpen && <div className="history-filter-panel" role="region" aria-label="Quote result filters">
          <div className="history-filter-title">
            <div>
              <span><Filter size={15} aria-hidden="true" /> Refine quote results</span>
              <small>{activeFilterCount === 0 ? 'Showing all available values' : `${activeFilterCount} ${activeFilterCount === 1 ? 'filter' : 'filters'} applied`}</small>
            </div>
            <div className="history-filter-actions">
              <button type="button" disabled={activeFilterCount === 0} onClick={() => { setFilters((current) => ({ ...emptyFilters, search: current.search })); setPage(1) }}>Clear filters</button>
              <button type="button" className="history-filter-done" onClick={() => setFiltersOpen(false)}>Done</button>
            </div>
          </div>
          <div className="history-filter-grid">
            <SelectFilter label="Estimator" value={filters.estimator} options={options.estimators} onChange={(value) => updateFilter('estimator', value)} />
            <SelectFilter label="Salesperson" value={filters.salesPerson} options={options.salesPersons ?? []} onChange={(value) => updateFilter('salesPerson', value)} />
            <SelectFilter label="Customer" value={filters.customer} options={options.customers} onChange={(value) => updateFilter('customer', value)} />
            <SelectFilter label="Quote status" value={filters.quoteStatus} options={options.quoteStatuses} onChange={(value) => updateFilter('quoteStatus', value)} />
            <label><span>On time</span><select value={filters.onTime} onChange={(event) => updateFilter('onTime', event.currentTarget.value)}><option value="">All</option><option value="OnTime">On time</option><option value="Late">Late</option><option value="NoData">No data</option></select></label>
            <label className="history-date-range"><span>RFQ due date range</span><span className="history-date-inputs"><input aria-label="RFQ due from" type="date" value={filters.dueFrom} onChange={(event) => updateFilter('dueFrom', event.currentTarget.value)} /><i>to</i><input aria-label="RFQ due through" type="date" value={filters.dueTo} onChange={(event) => updateFilter('dueTo', event.currentTarget.value)} /></span></label>
          </div>
        </div>}

        {activeFilterCount > 0 && <div className="history-filter-chips" aria-label="Active quote filters">
          {filters.estimator && <button type="button" onClick={() => updateFilter('estimator', '')}>Estimator: {filters.estimator}<X size={12} aria-hidden="true" /></button>}
          {filters.salesPerson && <button type="button" onClick={() => updateFilter('salesPerson', '')}>Salesperson: {filters.salesPerson}<X size={12} aria-hidden="true" /></button>}
          {filters.customer && <button type="button" onClick={() => updateFilter('customer', '')}>Customer: {filters.customer}<X size={12} aria-hidden="true" /></button>}
          {filters.quoteStatus && <button type="button" onClick={() => updateFilter('quoteStatus', '')}>Status: {filters.quoteStatus}<X size={12} aria-hidden="true" /></button>}
          {filters.onTime && <button type="button" onClick={() => updateFilter('onTime', '')}>On time: {filters.onTime}<X size={12} aria-hidden="true" /></button>}
          {(filters.dueFrom || filters.dueTo) && <button type="button" onClick={() => { setFilters((current) => ({ ...current, dueFrom: '', dueTo: '' })); setPage(1) }}>Due: {filters.dueFrom || 'Any'} – {filters.dueTo || 'Any'}<X size={12} aria-hidden="true" /></button>}
        </div>}

        <div className="history-result-summary">
          <span role="status" aria-live="polite">{loading || searchPending ? 'Updating records…' : `${pageData.total.toLocaleString()} matching ${view === 'live' ? 'queue' : 'history'} quotes`}</span>
          <button type="button" aria-label="Refresh estimating history" onClick={() => setRevision((current) => current + 1)}>
            <RefreshCw size={14} aria-hidden="true" /> Refresh
          </button>
        </div>

        {pageData.records.length === 0 && !loading ? <div className="history-empty">
          <FileSpreadsheet size={30} aria-hidden="true" />
          <strong>{view === 'live' ? 'No quotes in the live queue' : 'No matching quote history'}</strong>
          <span>{pageData.total === 0 && activeFilterCount === 0 ? (view === 'live' ? 'No synchronized quotes currently have Needs Approval status.' : 'Waiting for the scheduled Fulcrum sync or a controlled workbook import.') : 'Change or clear the current column filters.'}</span>
        </div> : <div className="history-table-scroll">
          <table className={`history-table ${view === 'live' ? 'live-queue-table' : ''}`}>
            {view === 'live' ? <>
              <thead><tr>
                <th><SortButton label="Quote" field="number" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="Customer" field="customer" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th>Customer contact</th>
                <th>Sales person</th>
                <th>Status</th>
                <th>RFQ / reference</th>
                <th><SortButton label="Estimator" field="estimator" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="RFQ due" field="due" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th>Issues</th>
                <th>On track?</th>
                <th>Complexity</th>
                <th>Parts</th>
                <th>Estimating status</th>
              </tr></thead>
              <tbody>{pageData.records.map((record) => <tr key={record.id}>
                <th scope="row">
                  <strong><HighlightText value={record.quoteNumber} query={appliedSearch} /></strong>
                </th>
                <td><HighlightText value={record.customer} query={appliedSearch} /></td>
                <td><HighlightText value={record.customerContact ?? '—'} query={appliedSearch} /></td>
                <td><HighlightText value={record.salesPerson} query={appliedSearch} /></td>
                <td><span className="history-status neutral"><HighlightText value={record.quoteStatus} query={appliedSearch} /></span></td>
                <td><HighlightText value={record.rfqReferenceNumber ?? '—'} query={appliedSearch} /></td>
                <td><HighlightText value={record.estimatingRep} query={appliedSearch} /></td>
                <td><HighlightText value={date(record.rfqDueDate)} query={appliedSearch} /></td>
                <td><HighlightText value={record.issues ?? '—'} query={appliedSearch} /></td>
                <td><span className={`history-status ${record.quoteOnTrack?.toLowerCase().replaceAll(' ', '') ?? 'neutral'}`}><HighlightText value={record.quoteOnTrack ?? '—'} query={appliedSearch} /></span></td>
                <td><HighlightText value={record.quoteComplexity ?? '—'} query={appliedSearch} /></td>
                <td><HighlightText value={record.numberOfParts} query={appliedSearch} /></td>
                <td><HighlightText value={record.estimatingStatus ?? '—'} query={appliedSearch} /></td>
              </tr>)}</tbody>
            </> : <>
              <thead><tr>
                <th><SortButton label="Quote" field="number" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="Customer" field="customer" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th>Sales person</th>
                <th><SortButton label="Estimator" field="estimator" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th>Quote status</th>
                <th>Estimating status</th>
                <th>Complexity / issue</th>
                <th>Parts</th>
                <th><SortButton label="Value" field="value" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="RFQ due" field="due" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="Assigned" field="assigned" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="Completed" field="completed" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th><SortButton label="Workdays" field="workdays" sort={sort} direction={direction} onSort={updateSort} /></th>
                <th>On time</th>
              </tr></thead>
              <tbody>{pageData.records.map((record) => <tr key={record.id}>
                <th scope="row">
                  <strong><HighlightText value={record.quoteNumber} query={appliedSearch} /></strong>
                  <small><HighlightText value={record.rfqReferenceNumber ?? record.sourceId} query={appliedSearch} /></small>
                </th>
                <td><HighlightText value={record.customer} query={appliedSearch} /><small><HighlightText value={record.customerContact ?? 'No contact data'} query={appliedSearch} /></small></td>
                <td><HighlightText value={record.salesPerson} query={appliedSearch} /></td>
                <td><HighlightText value={record.estimatingRep} query={appliedSearch} /></td>
                <td><span className="history-status neutral"><HighlightText value={record.quoteStatus} query={appliedSearch} /></span></td>
                <td><HighlightText value={record.estimatingStatus ?? '—'} query={appliedSearch} /></td>
                <td><strong><HighlightText value={record.quoteComplexity ?? '—'} query={appliedSearch} /></strong><small><HighlightText value={record.issues ?? 'No issue data'} query={appliedSearch} /></small></td>
                <td><HighlightText value={record.numberOfParts} query={appliedSearch} /></td>
                <td className="numeric">{currency(record.totalValue)}</td>
                <td><HighlightText value={date(record.rfqDueDate)} query={appliedSearch} /></td>
                <td><HighlightText value={date(record.dateToEstimating)} query={appliedSearch} /></td>
                <td>{date(record.estimatingCompletionDate)}</td>
                <td className="numeric"><HighlightText value={record.workdays == null ? '—' : record.workdays} query={appliedSearch} /></td>
                <td><span className={`history-status ${record.onTimeStatus.toLowerCase()}`}><HighlightText value={onTimeLabel(record)} query={appliedSearch} /></span></td>
              </tr>)}</tbody>
            </>}
          </table>
        </div>}

        <div className="history-pagination">
          <span>Page {pageData.page} of {totalPages}</span>
          <div>
            <button type="button" disabled={page <= 1} onClick={() => setPage((current) => Math.max(1, current - 1))}><ChevronLeft size={15} aria-hidden="true" /> Previous</button>
            <button type="button" disabled={page >= totalPages} onClick={() => setPage((current) => Math.min(totalPages, current + 1))}>Next <ChevronRight size={15} aria-hidden="true" /></button>
          </div>
        </div>
      </section>

      {canImport && importOpen && <div className="history-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target) closeImport() }}>
        <section className="history-modal" role="dialog" aria-modal="true" aria-labelledby="history-import-title">
          <header>
            <div><span className="section-kicker">Controlled workbook import</span><h2 id="history-import-title">Import Estimating History</h2></div>
            <button type="button" aria-label="Close import" disabled={importBusy} onClick={closeImport}><X size={18} aria-hidden="true" /></button>
          </header>
          <div className="history-import-steps">
            <section>
              <span className="step-number">1</span>
              <div><strong>Select the quote workbook</strong><p>Use the unmodified <b>Grid Results</b> export or the legacy <b>Daily Quote Log</b> workbook. The system joins its quote tables automatically.</p></div>
              <label className="history-file-control"><FileSpreadsheet size={18} aria-hidden="true" /><span>{importFile?.name ?? 'Choose Excel workbook'}</span><input type="file" accept=".xlsx" onChange={(event) => { setImportFile(event.currentTarget.files?.[0] ?? null); setValidation(null); setImportError(null) }} /></label>
            </section>
            <section>
              <span className="step-number">2</span>
              <div><strong>Validate and compare</strong><p>The system checks columns and values, then compares every included quote against the current history. Omitted quotes are never deleted.</p></div>
              <button type="button" className="history-validate-button" disabled={!importFile || importBusy} onClick={() => void validateImport()}>{importBusy ? 'Checking workbook…' : 'Validate workbook'}</button>
            </section>
          </div>
          {importError && <div className="history-import-error" role="alert"><AlertTriangle size={16} aria-hidden="true" /> {importError}</div>}
          {validation && <section className={`history-import-review ${validation.errorRows > 0 ? 'has-errors' : ''}`}>
            <div className="history-review-heading"><div><span className="section-kicker">Upload comparison</span><h3>{validation.errorRows > 0 ? 'Review Required' : 'Ready To Apply'}</h3></div><span>{validation.errorRows > 0 ? `${validation.errorRows} error rows` : 'No errors'}</span></div>
            <div className="history-review-metrics">
              <div><span>New quotes</span><strong>{validation.newRecords}</strong></div>
              <div><span>Updates</span><strong>{validation.updatedRecords}</strong></div>
              <div><span>Unchanged</span><strong>{validation.unchangedRecords}</strong></div>
              <div><span>Total rows</span><strong>{validation.totalRows}</strong></div>
            </div>
            {validation.errors.length > 0 && <div className="history-import-issues">
              {validation.errors.slice(0, 12).map((issue, index) => <div key={`${issue.row}-${issue.column}-${index}`}><strong>Row {issue.row}{issue.column ? ` · ${issue.column}` : ''}</strong><span>{issue.message}</span></div>)}
              {validation.errors.length > 12 && <p>Plus {validation.errors.length - 12} additional errors.</p>}
            </div>}
            {validation.changes.length > 0 && <div className="history-change-preview">
              {validation.changes.slice(0, 6).map((change) => <span key={`${change.row}-${change.sourceId}`}><b>{change.changeType}</b> #{change.quoteNumber} · {change.customer}</span>)}
              {validation.changes.length > 6 && <span>Plus {validation.newRecords + validation.updatedRecords - 6} more changes</span>}
            </div>}
            <div className="history-review-actions">
              {validation.errorRows === 0 ? <button type="button" disabled={!validation.canApply || importBusy} onClick={() => void applyImport(false)}>Apply reviewed import</button> : <button type="button" disabled={validation.newRecords + validation.updatedRecords === 0 || importBusy} onClick={() => setForceConfirm(true)}>Continue with valid rows</button>}
            </div>
          </section>}
          <footer><span>No database changes occur until the reviewed import is applied.</span><button type="button" disabled={importBusy} onClick={closeImport}>Close</button></footer>
        </section>
      </div>}

      {forceConfirm && validation && <div className="history-modal-backdrop warning-layer" role="presentation">
        <section className="history-warning-modal" role="alertdialog" aria-modal="true" aria-labelledby="history-warning-title">
          <span className="warning-icon"><AlertTriangle size={24} aria-hidden="true" /></span>
          <span className="section-kicker">Errors remain</span>
          <h2 id="history-warning-title">Continue Without Invalid Rows?</h2>
          <p>{validation.errorRows} rows contain errors and will be skipped. Valid new and changed records will be saved, and quotes omitted from the workbook will remain unchanged.</p>
          <div><button type="button" disabled={importBusy} onClick={() => setForceConfirm(false)}>Return to review</button><button type="button" className="confirm-warning" disabled={importBusy} onClick={() => void applyImport(true)}>Confirm and continue</button></div>
        </section>
      </div>}
    </div>
  )
}
