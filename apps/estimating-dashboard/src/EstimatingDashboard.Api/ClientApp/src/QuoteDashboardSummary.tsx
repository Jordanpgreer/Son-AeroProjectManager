import { AlertTriangle, CheckCircle2, Clock3, FileText, Timer, type LucideIcon } from 'lucide-react'
import { currency } from './quoteDashboardFormatting'
import type { DashboardScope, DashboardStats, DashboardStatus } from './quoteDashboardApi'

export default function QuoteDashboardSummary({ scope, period, stats, selected, onSelect }: {
  scope: DashboardScope; period: string; stats: DashboardStats | null
  selected: DashboardStatus; onSelect: (status: DashboardStatus) => void
}) {
  const missingTiming = stats ? Math.max(0, stats.completedCount - stats.completionSampleSize) : 0
  const average = stats && missingTiming === 0 ? stats.averageCompletionWorkdays : null
  const averageTitle = missingTiming > 0
    ? `${missingTiming} completed quote${missingTiming === 1 ? ' is' : 's are'} missing verified estimating timing. The period average is unavailable.`
    : 'Verified estimating start to completion in business days.'
  const onTimeTitle = stats
    ? `${stats.onTimeCount} of ${stats.onTimeSampleSize} completed quotes finished on or before their RFQ due date. ${stats.onTimeMissingCount} completed quote${stats.onTimeMissingCount === 1 ? ' is' : 's are'} excluded because completion or RFQ due dates are missing or invalid.`
    : 'On-time completion is measured against the RFQ due date.'
  return <section className="qd-performance" aria-label={`${scope === 'mine' ? 'My' : 'All'} performance · ${period}`} aria-busy={!stats}>
    <h2>{scope === 'mine' ? 'My' : 'All'} performance <span>· {period}</span></h2>
    <div className="qd-metrics">
      <Metric label="Active" icon={FileText} value={stats?.activeCount} selected={selected === 'active'} onSelect={() => onSelect(selected === 'active' ? 'all' : 'active')} />
      <Metric label="Overdue" icon={AlertTriangle} value={stats?.overdueCount} tone="overdue" title="Active quotes past their RFQ due date" selected={selected === 'overdue'} onSelect={() => onSelect(selected === 'overdue' ? 'all' : 'overdue')} />
      <Metric label="Completed" icon={CheckCircle2} value={stats?.completedCount} selected={selected === 'completed'} onSelect={() => onSelect(selected === 'completed' ? 'all' : 'completed')} />
      <Metric label="Avg. completion" icon={Clock3} value={average === null ? undefined : `${average.toLocaleString(undefined, { maximumFractionDigits: 1 })}d`} title={averageTitle} />
      <Metric label="On-time" icon={Timer} value={stats?.onTimePercentage == null ? undefined : `${stats.onTimePercentage.toLocaleString(undefined, { maximumFractionDigits: 1 })}%`}
        title={onTimeTitle} note={stats && stats.onTimeMissingCount > 0 ? `${stats.onTimeMissingCount} excluded` : undefined} />
      <Metric label="Won" icon={CheckCircle2} tone="won" value={stats ? `${stats.wonCount} · ${currency(stats.wonTotalValue)}` : undefined} />
      <Metric label="Lost" icon={AlertTriangle} tone="lost" value={stats ? `${stats.lostCount} · ${currency(stats.lostTotalValue)}` : undefined} />
    </div>
  </section>
}

function Metric({ label, icon: Icon, value, title, tone, note, selected, onSelect }: {
  label: string; icon: LucideIcon; value?: number | string; title?: string; tone?: string; note?: string
  selected?: boolean; onSelect?: () => void
}) {
  const content = <><Icon size={17} aria-hidden="true" /><span>{label}</span><strong>{typeof value === 'number' ? value.toLocaleString() : value ?? '—'}</strong>{note && <small>{note}</small>}</>
  return onSelect
    ? <button className="qd-metric" data-tone={tone} title={title} aria-pressed={selected} aria-controls="quote-dashboard-results" type="button" onClick={onSelect}>{content}</button>
    : <span className="qd-metric" data-tone={tone} title={title} tabIndex={title ? 0 : undefined} aria-label={title ? `${label}: ${value ?? 'unavailable'}. ${title}` : undefined}>{content}</span>
}
