import { useEffect, useState, type ReactNode } from 'react'
import { AlertCircle, ArrowRight, Boxes, CheckCircle2, ClipboardCheck, FileSearch, Search } from 'lucide-react'

interface SearchCategory { id: string; title: string; count: number }
export interface EngineeringSearchResult {
  id: string; category: string; categoryLabel: string; title: string; identifier: string; subtitle: string
  customer: string | null; specificationNumber: string | null; workOrder: string | null; reportNumber: string | null
  tags: string[]; note: string; drawingId: number | null; attentionReasons?: string[] | null
}
interface DashboardData {
  searchHint: string
  categories: SearchCategory[]
  results: EngineeringSearchResult[]
  summary: { totalDrawings: number; draftDrawings: number; reviewQueue: number; approvedDrawings: number; checkedOutMylars: number }
  customers: string[]
}

type QuickFilter = 'drawings' | 'drafts' | 'review' | 'approved'

const quickFilters: Record<QuickFilter, { label: string; category: string; status: string; reviewQueue?: boolean }> = {
  drawings: { label: 'All drawings', category: 'drawings', status: '' },
  drafts: { label: 'Draft drawings', category: 'drawings', status: 'Draft' },
  review: { label: 'Review queue', category: '', status: '', reviewQueue: true },
  approved: { label: 'Approved drawings', category: 'drawings', status: 'Approved' },
}

function HighlightedText({ value, query }: { value: string; query: string }) {
  if (!query) return <>{value}</>
  const normalizedValue = value.toLocaleLowerCase()
  const normalizedQuery = query.toLocaleLowerCase()
  const pieces: ReactNode[] = []
  let cursor = 0
  let matchIndex = normalizedValue.indexOf(normalizedQuery)

  while (matchIndex >= 0) {
    if (matchIndex > cursor) pieces.push(<span key={`text-${cursor}`}>{value.slice(cursor, matchIndex)}</span>)
    pieces.push(<mark key={`match-${matchIndex}`}>{value.slice(matchIndex, matchIndex + query.length)}</mark>)
    cursor = matchIndex + query.length
    matchIndex = normalizedValue.indexOf(normalizedQuery, cursor)
  }
  if (cursor < value.length) pieces.push(<span key={`text-${cursor}`}>{value.slice(cursor)}</span>)
  return <>{pieces}</>
}

function references(item: EngineeringSearchResult) {
  return [
    item.specificationNumber && { label: 'Spec', value: item.specificationNumber },
    item.workOrder && { label: 'WO', value: item.workOrder },
    item.reportNumber && { label: 'Report', value: item.reportNumber },
  ].filter(Boolean) as { label: string; value: string }[]
}

function recordStatus(item: EngineeringSearchResult) {
  return item.tags.find(tag => ['Draft', 'UnderReview', 'Approved', 'Obsolete'].includes(tag)) ?? null
}

function statusDisplay(value: string) {
  if (value === 'Obsolete') return 'Archived'
  return value.replace(/([a-z])([A-Z])/g, '$1 $2')
}

export default function EngineeringDashboard({
  onOpenResult,
}: {
  onOpenResult: (result: EngineeringSearchResult) => void
}) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState('')
  const [customer, setCustomer] = useState('')
  const [status, setStatus] = useState('')
  const [quickFilter, setQuickFilter] = useState<QuickFilter | null>(null)
  const [loading, setLoading] = useState(true)
  const [updating, setUpdating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setUpdating(true)
    setError(null)
    const parameters = new URLSearchParams()
    const preset = quickFilter ? quickFilters[quickFilter] : null
    const effectiveCategory = preset?.category ?? category
    const effectiveStatus = preset?.status ?? status
    if (query.trim()) parameters.set('query', query.trim())
    if (effectiveCategory) parameters.set('category', effectiveCategory)
    if (customer) parameters.set('customer', customer)
    if (effectiveStatus) parameters.set('status', effectiveStatus)
    if (preset?.reviewQueue) parameters.set('reviewQueue', 'true')

    async function loadDashboard() {
      try {
        const response = await fetch(`/api/dashboard?${parameters}`, { credentials: 'include', signal: controller.signal })
        if (!response.ok) throw new Error(`Dashboard responded ${response.status}.`)
        setData(await response.json() as DashboardData)
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to load the dashboard.')
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false)
          setUpdating(false)
        }
      }
    }

    void loadDashboard()
    return () => controller.abort()
  }, [query, category, customer, status, quickFilter])

  const summary = data?.summary
  const results = data?.results ?? []
  const normalizedQuery = query.trim()

  function toggleQuickFilter(filter: QuickFilter) {
    setQuickFilter(current => current === filter ? null : filter)
    setCategory('')
    setStatus('')
  }

  function clearFilters() {
    setQuery('')
    setCategory('')
    setCustomer('')
    setStatus('')
    setQuickFilter(null)
  }

  return <>
    <section className="operational-kpis" aria-label="Engineering record filters">
      <button className={`kpi kpi-filter tone-ink ${quickFilter === 'drawings' ? 'is-active' : ''}`} type="button" aria-pressed={quickFilter === 'drawings'} onClick={() => toggleQuickFilter('drawings')}>
        <div className="kpi-top"><span className="kpi-label">Drawings</span><Boxes size={18}/></div>
        <div className="kpi-value">{summary?.totalDrawings ?? '-'}</div>
        <div className="kpi-filter-footer"><span>Controlled records</span><strong>{quickFilter === 'drawings' ? 'Viewing' : 'Filter'}<ArrowRight size={12}/></strong></div>
      </button>
      <button className={`kpi kpi-filter tone-steel ${quickFilter === 'drafts' ? 'is-active' : ''}`} type="button" aria-pressed={quickFilter === 'drafts'} onClick={() => toggleQuickFilter('drafts')}>
        <div className="kpi-top"><span className="kpi-label">Drafts</span><FileSearch size={18}/></div>
        <div className="kpi-value">{summary?.draftDrawings ?? '-'}</div>
        <div className="kpi-filter-footer"><span>In preparation</span><strong>{quickFilter === 'drafts' ? 'Viewing' : 'Filter'}<ArrowRight size={12}/></strong></div>
      </button>
      <button className={`kpi kpi-filter tone-risk ${quickFilter === 'review' ? 'is-active' : ''}`} type="button" aria-pressed={quickFilter === 'review'} onClick={() => toggleQuickFilter('review')}>
        <div className="kpi-top"><span className="kpi-label">Review queue</span><ClipboardCheck size={18}/></div>
        <div className="kpi-value">{summary?.reviewQueue ?? '-'}</div>
        <div className="kpi-filter-footer"><span>Needs attention</span><strong>{quickFilter === 'review' ? 'Viewing' : 'Filter'}<ArrowRight size={12}/></strong></div>
      </button>
      <button className={`kpi kpi-filter tone-ok ${quickFilter === 'approved' ? 'is-active' : ''}`} type="button" aria-pressed={quickFilter === 'approved'} onClick={() => toggleQuickFilter('approved')}>
        <div className="kpi-top"><span className="kpi-label">Approved</span><CheckCircle2 size={18}/></div>
        <div className="kpi-value">{summary?.approvedDrawings ?? '-'}</div>
        <div className="kpi-filter-footer"><span>{summary?.checkedOutMylars ?? 0} Mylars checked out</span><strong>{quickFilter === 'approved' ? 'Viewing' : 'Filter'}<ArrowRight size={12}/></strong></div>
      </button>
    </section>

    {error && <section className="panel state-error" role="alert"><AlertCircle size={20}/><div><strong>Dashboard unavailable</strong><p>{error}</p></div></section>}

    <section className="panel dashboard-search-panel" aria-busy={updating}>
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Global engineering search</span><h2>Operational record lookup</h2><p>{data?.searchHint ?? 'Search every indexed engineering record.'}</p></div></div>
      <label className="topbar-search engineering-search"><Search size={15}/><input value={query} onChange={event => setQuery(event.target.value)} aria-label="Filter engineering records" placeholder="Part, tool, drawing, compound, customer, spec, work order, report, or note"/></label>
      <div className="dashboard-filters">
        <label>Category<select value={category} onChange={event => { setQuickFilter(null); setCategory(event.target.value) }}><option value="">All categories</option>{data?.categories.map(item => <option key={item.id} value={item.id}>{item.title} ({item.count})</option>)}</select></label>
        <label>Customer<select value={customer} onChange={event => setCustomer(event.target.value)}><option value="">All customers</option>{data?.customers.map(item => <option key={item}>{item}</option>)}</select></label>
        <label>Status<select value={status} onChange={event => { setQuickFilter(null); setStatus(event.target.value) }}><option value="">All statuses</option><option>Draft</option><option value="UnderReview">Under review</option><option>Approved</option><option value="Obsolete">Archived</option></select></label>
        {(query || category || customer || status || quickFilter) && <button className="button ghost" type="button" onClick={clearFilters}>Clear filters</button>}
      </div>
      <div className="dashboard-filter-status" role="status" aria-live="polite">
        <span>{updating ? 'Updating results...' : `${results.length} matching record${results.length === 1 ? '' : 's'}`}</span>
        {quickFilter && <strong>{quickFilters[quickFilter].label}</strong>}
      </div>
    </section>

    <section className="dashboard-results">
      {loading ? <section className="panel skeleton-panel"><div className="skeleton-line lg"/><div className="skeleton-line"/><div className="skeleton-line" style={{ width: '72%' }}/></section> :
        results.length ? <section className="panel engineering-results-panel">
          <header className="engineering-results-header">
            <div><span className="eyebrow">Engineering register</span><h2>Matching records</h2></div>
            <span>{results.length} shown</span>
          </header>
          <div className="engineering-results-table-wrap">
            <table className="engineering-results-table">
              <thead><tr><th>Record</th><th>Type</th><th>Customer</th><th>References</th><th>Status / attention</th><th aria-label="Open record"/></tr></thead>
              <tbody>{results.map(item => {
                const itemReferences = references(item)
                const statusLabel = recordStatus(item)
                const attentionReasons = item.attentionReasons ?? []
                return <tr
                  key={item.id}
                  className="engineering-result-row"
                  onClick={() => onOpenResult(item)}
                >
                  <td>
                    <button
                      className="engineering-record-link"
                      type="button"
                      aria-label={`Open ${item.categoryLabel} record ${item.identifier}: ${item.title}`}
                      onClick={event => {
                        event.stopPropagation()
                        onOpenResult(item)
                      }}
                    >
                      <strong className="engineering-record-id"><HighlightedText value={item.identifier} query={normalizedQuery}/></strong>
                      <span className="engineering-record-title"><HighlightedText value={item.title} query={normalizedQuery}/></span>
                      <small><HighlightedText value={item.subtitle} query={normalizedQuery}/></small>
                    </button>
                  </td>
                  <td><span className="engineering-record-type"><HighlightedText value={item.categoryLabel} query={normalizedQuery}/></span></td>
                  <td>{item.customer ? <HighlightedText value={item.customer} query={normalizedQuery}/> : <span className="table-empty">Not recorded</span>}</td>
                  <td>{itemReferences.length ? <div className="engineering-reference-list">{itemReferences.map(reference => <span key={`${reference.label}-${reference.value}`}><small>{reference.label}</small><strong><HighlightedText value={reference.value} query={normalizedQuery}/></strong></span>)}</div> : <span className="table-empty">No references</span>}</td>
                  <td>{attentionReasons.length ? <div className="engineering-attention-list">{attentionReasons.slice(0, 2).map(reason => <span key={reason}><HighlightedText value={reason} query={normalizedQuery}/></span>)}{attentionReasons.length > 2 && <em>+{attentionReasons.length - 2} more</em>}</div> : statusLabel ? <span className={`status-pill status-${statusLabel.toLowerCase()}`}>{statusDisplay(statusLabel)}</span> : <span className="table-empty">No action needed</span>}</td>
                  <td><ArrowRight className="engineering-row-arrow" size={16}/></td>
                </tr>
              })}</tbody>
            </table>
          </div>
        </section> :
        <section className="panel empty-search-state"><strong>No engineering records matched</strong><p>Adjust the filters or try another identifier, customer, or note keyword.</p></section>}
    </section>
  </>
}
