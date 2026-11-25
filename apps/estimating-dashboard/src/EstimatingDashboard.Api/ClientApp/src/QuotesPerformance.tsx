import { CalendarDays, ChevronDown } from 'lucide-react'
import { useEffect, useId, useRef, useState, type CSSProperties } from 'react'
import { currency } from './quoteDashboardFormatting'
import type { DashboardStats } from './quoteDashboardApi'
import './quotes-performance.css'

interface Estimator {
  id: string
  name: string
}

interface PerformanceResponse {
  stats: DashboardStats
  estimators: Estimator[]
}

interface PerformanceResult {
  key: string
  stats?: DashboardStats
  error?: string
}

function performanceQuery(selection: string, month: string) {
  const params = new URLSearchParams({ scope: selection === 'mine' ? 'mine' : 'all' })
  if (selection.startsWith('estimator:')) params.set('estimator', selection.slice('estimator:'.length))
  if (month) params.set('month', month)
  return params.toString()
}

async function loadPerformance(query: string, signal: AbortSignal): Promise<PerformanceResponse> {
  const response = await fetch(`/api/quote-workflow/performance?${query}`, {
    credentials: 'include', signal,
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.detail ?? body?.message ?? `Performance could not be loaded (${response.status}).`)
  }
  return response.json()
}

export default function QuotesPerformance({ ownerAccountName, canViewAllQuotes }: {
  ownerAccountName: string
  canViewAllQuotes: boolean
}) {
  return <PerformanceView key={JSON.stringify([ownerAccountName, canViewAllQuotes])} canViewAllQuotes={canViewAllQuotes} />
}

function PerformanceView({ canViewAllQuotes }: { canViewAllQuotes: boolean }) {
  const [selection, setSelection] = useState('mine')
  const [month, setMonth] = useState('')
  const [revision, setRevision] = useState(0)
  const [estimators, setEstimators] = useState<Estimator[]>([])
  const [result, setResult] = useState<PerformanceResult>()
  const query = performanceQuery(selection, month)
  const key = JSON.stringify([query, revision])
  const current = result?.key === key ? result : undefined
  const estimator = estimators.find(({ id }) => selection === `estimator:${id}`)
  const heading = selection === 'mine' ? 'My performance' : selection === 'all' ? 'All estimators' : estimator?.name ?? 'Estimator performance'

  useEffect(() => {
    const controller = new AbortController()
    void loadPerformance(query, controller.signal).then(data => {
      if (controller.signal.aborted) return
      if (canViewAllQuotes) setEstimators(data.estimators)
      setResult({ key, stats: data.stats })
    }, error => {
      if (controller.signal.aborted) return
      setResult({ key, error: error instanceof Error ? error.message : 'Performance could not be loaded.' })
    })
    return () => controller.abort()
  }, [canViewAllQuotes, key, query])

  function changeMonth(value: string) {
    if (month === value) return
    setMonth(value)
    setRevision(previous => previous + 1)
  }

  return <div className="qp-view">
    <header className="qp-header">
      {canViewAllQuotes ? <div className="qp-estimator">
          <h2>{heading}</h2>
          <ChevronDown size={20} aria-hidden="true" />
          <select aria-label="Performance estimator" value={selection} onChange={event => {
            setSelection(event.target.value)
            setRevision(previous => previous + 1)
          }}>
            <option value="mine">My performance</option>
            <option value="all">All estimators</option>
            {!estimator && selection.startsWith('estimator:') && <option value={selection}>{heading}</option>}
            {estimators.length > 0 && <optgroup label="Estimators">
              {estimators.map(({ id, name }) => <option key={id} value={`estimator:${id}`}>{name}</option>)}
            </optgroup>}
          </select>
        </div> : <h2>{heading}</h2>}
      <PerformancePeriod month={month} onChange={changeMonth} />
    </header>
    {!current && <p className="qp-state" role="status">Loading performance…</p>}
    {current?.error && <div className="qp-state">
      <p role="alert">{current.error}</p>
      <button type="button" className="qd-secondary-button" onClick={() => setRevision(previous => previous + 1)}>Retry</button>
    </div>}
    {current?.stats && <PerformanceCharts stats={current.stats} />}
  </div>
}

const monthLabel = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' })
const monthName = new Intl.DateTimeFormat(undefined, { month: 'long', timeZone: 'UTC' })
const monthOptions = Array.from({ length: 12 }, (_, index) => ({
  value: String(index + 1).padStart(2, '0'),
  label: monthName.format(new Date(Date.UTC(2000, index, 1))),
}))

function PerformancePeriod({ month, onChange }: { month: string; onChange: (month: string) => void }) {
  const [open, setOpen] = useState(false)
  const [draftYear, setDraftYear] = useState('')
  const [draftMonth, setDraftMonth] = useState('')
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const monthSelect = useRef<HTMLSelectElement>(null)
  const picker = useId()
  const label = month ? monthLabel.format(new Date(`${month}-01T00:00:00Z`)) : 'All time'

  useEffect(() => {
    if (!open) return
    monthSelect.current?.focus()
    function handleOutside(event: PointerEvent) {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    document.addEventListener('pointerdown', handleOutside)
    return () => document.removeEventListener('pointerdown', handleOutside)
  }, [open])

  function toggle() {
    if (open) {
      setOpen(false)
      return
    }
    const now = new Date()
    setDraftYear(month ? String(Number(month.slice(0, 4))) : String(now.getFullYear()))
    setDraftMonth(month ? month.slice(5) : String(now.getMonth() + 1).padStart(2, '0'))
    setOpen(true)
  }

  function close() {
    setOpen(false)
    trigger.current?.focus()
  }

  return <div ref={root} className="qp-period" onKeyDown={event => {
    if (!open || event.key !== 'Escape') return
    event.stopPropagation()
    event.preventDefault()
    close()
  }} onBlur={event => {
    if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget)) setOpen(false)
  }}>
    <button type="button" ref={trigger} className="qp-period-trigger" aria-label={`Performance period: ${label}`}
      aria-expanded={open} aria-controls={picker} onClick={toggle}>
      <CalendarDays size={17} aria-hidden="true" />
      <span>{label}</span>
      <ChevronDown size={16} aria-hidden="true" />
    </button>
    {month && <p className="qp-period-description">Quotes with an RFQ due date in this month.</p>}
    <form className="qp-period-picker" id={picker} hidden={!open} onSubmit={event => {
      event.preventDefault()
      onChange(`${String(Number(draftYear)).padStart(4, '0')}-${draftMonth}`)
      close()
    }}>
      <div className="qp-period-fields">
        <label>
          Month
          <select ref={monthSelect} value={draftMonth} onChange={event => setDraftMonth(event.target.value)} required>
            {monthOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
          </select>
        </label>
        <label>
          Year
          <input type="number" inputMode="numeric" min={1} max={9999} step={1} value={draftYear}
            onChange={event => setDraftYear(event.target.value)} required />
        </label>
      </div>
      <div className="qp-period-actions">
        <button type="button" className="qd-secondary-button" onClick={() => {
          onChange('')
          close()
        }}>All time</button>
        <button type="submit" className="qd-secondary-button qp-period-apply">Apply</button>
      </div>
    </form>
  </div>
}

function PerformanceCharts({ stats }: { stats: DashboardStats }) {
  const missingTiming = Math.max(0, stats.completedCount - stats.completionSampleSize)
  const average = missingTiming === 0 ? stats.averageCompletionWorkdays : null
  const percentage = stats.onTimeSampleSize > 0 ? stats.onTimePercentage : null
  const timely = Math.max(0, Math.min(100, percentage ?? 0))
  const chartMaximum = Math.max(stats.wonCount, stats.lostCount, 1)

  return <>
    <dl className="qp-metrics">
      <div>
        <dt>Active quotes</dt>
        <dd>
          <strong>{stats.activeCount.toLocaleString()}</strong>
          <span className={stats.overdueCount > 0 ? 'qp-overdue' : 'qp-unit'}>{stats.overdueCount.toLocaleString()} overdue</span>
        </dd>
      </div>
      <div>
        <dt>Completed quotes</dt>
        <dd><strong>{stats.completedCount.toLocaleString()}</strong></dd>
      </div>
      <div>
        <dt>Avg. completion</dt>
        {average === null
          ? <dd><strong aria-hidden="true">—</strong><span className="qp-unit">{missingTiming > 0 ? 'Incomplete timing data' : 'No timing data'}</span></dd>
          : <dd><strong>{average.toLocaleString(undefined, { maximumFractionDigits: 1 })}</strong><span className="qp-workdays">workdays</span></dd>}
      </div>
    </dl>
    <div className="qp-charts">
      <section className="qp-card">
        <h3>On-time completion</h3>
        <div className="qp-timing">
          <div className={`qp-donut${percentage === null ? ' qp-donut-empty' : ''}`}
            style={{ '--qp-on-time': `${timely}%` } as CSSProperties}>
            {percentage === null
              ? <span>No timed quotes</span>
              : <><strong>{percentage.toLocaleString(undefined, { maximumFractionDigits: 1 })}%</strong><span>On time</span></>}
          </div>
          <dl className="qp-legend">
            <div data-series="on-time"><dt>On time</dt><dd>{stats.onTimeCount.toLocaleString()}</dd></div>
            <div data-series="late"><dt>Late</dt><dd>{Math.max(0, stats.onTimeSampleSize - stats.onTimeCount).toLocaleString()}</dd></div>
          </dl>
        </div>
        {stats.onTimeMissingCount > 0 && <p className="qp-note">
          {stats.onTimeMissingCount.toLocaleString()} completed {stats.onTimeMissingCount === 1 ? 'quote excluded due to a missing or invalid date.' : 'quotes excluded due to missing or invalid dates.'}
        </p>}
      </section>
      <section className="qp-card qp-outcomes">
        <h3>Quote outcomes</h3>
        <p className="qp-chart-subtitle">Decided quotes</p>
        <dl className="qp-bars" style={{ '--qp-count-width': `${chartMaximum.toLocaleString().length + 1}ch` } as CSSProperties}>
          <Outcome label="Won" count={stats.wonCount} maximum={chartMaximum} />
          <Outcome label="Lost" count={stats.lostCount} maximum={chartMaximum} />
        </dl>
        <dl className="qp-values">
          <div><dt>Won value</dt><dd>{currency(stats.wonTotalValue)}</dd></div>
          <div><dt>Lost value</dt><dd>{currency(stats.lostTotalValue)}</dd></div>
        </dl>
      </section>
    </div>
  </>
}

function Outcome({ label, count, maximum }: { label: 'Won' | 'Lost'; count: number; maximum: number }) {
  return <div className="qp-outcome" data-series={label.toLowerCase()}>
    <dt>{label}</dt>
    <dd style={{ '--qp-count-share': count / maximum } as CSSProperties}>
      <span aria-hidden="true" /><strong>{count.toLocaleString()}</strong>
    </dd>
  </div>
}
