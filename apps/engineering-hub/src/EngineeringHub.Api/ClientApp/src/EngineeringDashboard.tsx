import { useDeferredValue, useEffect, useState } from 'react'
import { AlertCircle, ArrowRight, Boxes, CheckCircle2, ClipboardCheck, FileSearch, Pencil, Search } from 'lucide-react'

interface SearchCategory { id: string; title: string; count: number }
export interface EngineeringSearchResult {
  id: string; category: string; categoryLabel: string; title: string; identifier: string; subtitle: string
  customer: string | null; specificationNumber: string | null; workOrder: string | null; reportNumber: string | null
  tags: string[]; note: string; drawingId: number | null
}
interface WorkItem { id: string; kind: string; title: string; detail: string; tone: string; drawingId: number | null }
interface DashboardData {
  searchHint: string
  categories: SearchCategory[]
  results: EngineeringSearchResult[]
  summary: { totalDrawings: number; draftDrawings: number; awaitingReview: number; approvedDrawings: number; checkedOutMylars: number }
  workItems: WorkItem[]
  customers: string[]
}

export default function EngineeringDashboard({
  onOpenDrawing,
  onOpenResult,
}: {
  onOpenDrawing: (drawingId: number) => void
  onOpenResult: (result: EngineeringSearchResult) => void
}) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [query, setQuery] = useState('')
  const deferredQuery = useDeferredValue(query)
  const [category, setCategory] = useState('')
  const [customer, setCustomer] = useState('')
  const [status, setStatus] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      setLoading(true)
      setError(null)
      const parameters = new URLSearchParams()
      if (deferredQuery.trim()) parameters.set('query', deferredQuery.trim())
      if (category) parameters.set('category', category)
      if (customer) parameters.set('customer', customer)
      if (status) parameters.set('status', status)
      try {
        const response = await fetch(`/api/dashboard?${parameters}`, { credentials: 'include', signal: controller.signal })
        if (!response.ok) throw new Error(`Dashboard responded ${response.status}.`)
        setData(await response.json() as DashboardData)
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to load the dashboard.')
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }, 160)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [deferredQuery, category, customer, status])

  const grouped = (data?.categories ?? [])
    .map(item => ({ category: item, results: (data?.results ?? []).filter(result => result.category === item.id) }))
    .filter(group => group.results.length > 0)
  const summary = data?.summary

  return <>
    <section className="panel dashboard-search-panel">
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Global engineering search</span><h2>Operational record lookup</h2><p>{data?.searchHint ?? 'Search every indexed engineering record.'}</p></div></div>
      <label className="topbar-search engineering-search"><Search size={15}/><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Part, tool, drawing, compound, customer, spec, work order, report, or note"/></label>
      <div className="dashboard-filters">
        <label>Category<select value={category} onChange={event => setCategory(event.target.value)}><option value="">All categories</option>{data?.categories.map(item => <option key={item.id} value={item.id}>{item.title} ({item.count})</option>)}</select></label>
        <label>Customer<select value={customer} onChange={event => setCustomer(event.target.value)}><option value="">All customers</option>{data?.customers.map(item => <option key={item}>{item}</option>)}</select></label>
        <label>Status<select value={status} onChange={event => setStatus(event.target.value)}><option value="">All statuses</option><option>Draft</option><option>UnderReview</option><option>Approved</option><option>Obsolete</option></select></label>
        {(query || category || customer || status) && <button className="button ghost" type="button" onClick={() => { setQuery(''); setCategory(''); setCustomer(''); setStatus('') }}>Clear filters</button>}
      </div>
    </section>

    {error && <section className="panel state-error" role="alert"><AlertCircle size={20}/><div><strong>Dashboard unavailable</strong><p>{error}</p></div></section>}

    <section className="operational-kpis">
      <article className="kpi tone-ink"><div className="kpi-top"><span className="kpi-label">Drawings</span><Boxes size={18}/></div><div className="kpi-value">{summary?.totalDrawings ?? '—'}</div><div className="kpi-hint">Controlled records</div></article>
      <article className="kpi tone-steel"><div className="kpi-top"><span className="kpi-label">Drafts</span><FileSearch size={18}/></div><div className="kpi-value">{summary?.draftDrawings ?? '—'}</div><div className="kpi-hint">In preparation</div></article>
      <article className="kpi tone-risk"><div className="kpi-top"><span className="kpi-label">Review queue</span><ClipboardCheck size={18}/></div><div className="kpi-value">{summary?.awaitingReview ?? '—'}</div><div className="kpi-hint">Awaiting approval</div></article>
      <article className="kpi tone-ok"><div className="kpi-top"><span className="kpi-label">Approved</span><CheckCircle2 size={18}/></div><div className="kpi-value">{summary?.approvedDrawings ?? '—'}</div><div className="kpi-hint">{summary?.checkedOutMylars ?? 0} Mylars checked out</div></article>
    </section>

    <section className="dashboard-operational-grid">
      <article className="panel">
        <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Daily work queue</span><h2>{data?.workItems.length ?? 0} item{data?.workItems.length === 1 ? '' : 's'} need attention</h2></div></div>
        <div className="work-item-list">{data?.workItems.length ? data.workItems.map(item => <button key={item.id} type="button" className="work-item" disabled={!item.drawingId} onClick={() => item.drawingId && onOpenDrawing(item.drawingId)}><span className={`work-item-rail tone-${item.tone}`}/><span><small>{item.kind}</small><strong>{item.title}</strong><p>{item.detail}</p></span></button>) : <p className="section-copy">No drawing-control exceptions need attention.</p>}</div>
      </article>
      <article className="panel category-index">
        <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Search index</span><h2>Grouped categories</h2></div></div>
        {data?.categories.map(item => <button key={item.id} type="button" className={category === item.id ? 'active' : ''} onClick={() => setCategory(category === item.id ? '' : item.id)}><span>{item.title}</span><strong>{item.count}</strong></button>)}
      </article>
    </section>

    <section className="dashboard-results">
      {loading ? <section className="panel skeleton-panel"><div className="skeleton-line lg"/><div className="skeleton-line"/><div className="skeleton-line" style={{ width: '72%' }}/></section> :
        grouped.length ? grouped.map(group => <article key={group.category.id} className="panel results-group"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">{group.category.title}</span><h2>{group.results.length} result{group.results.length === 1 ? '' : 's'}</h2></div></div><div className="results-list">{group.results.map(item => <button key={item.id} type="button" className="result-card clickable" onClick={() => onOpenResult(item)}><div className="result-head"><div><strong>{item.title}</strong><span className="result-id">{item.identifier}</span></div><span className="result-category">{item.categoryLabel}</span></div><p className="result-subtitle">{item.subtitle}</p><dl className="result-meta">{item.customer && <div><dt>Customer</dt><dd>{item.customer}</dd></div>}{item.specificationNumber && <div><dt>Spec</dt><dd className="technical-id">{item.specificationNumber}</dd></div>}{item.workOrder && <div><dt>Work order</dt><dd className="technical-id">{item.workOrder}</dd></div>}{item.reportNumber && <div><dt>Report</dt><dd className="technical-id">{item.reportNumber}</dd></div>}</dl><div className="token-list">{item.tags.map(tag => <span key={tag} className="token-chip">{tag}</span>)}</div><p className="result-note">{item.note}</p><span className="result-edit-action">{item.drawingId ? <Pencil size={13}/> : <ArrowRight size={13}/>} {item.drawingId ? 'Edit linked drawing' : 'Open owning module'}</span></button>)}</div></article>) :
        <section className="panel empty-search-state"><strong>No engineering records matched</strong><p>Adjust the filters or try another identifier, customer, or note keyword.</p></section>}
    </section>
  </>
}
