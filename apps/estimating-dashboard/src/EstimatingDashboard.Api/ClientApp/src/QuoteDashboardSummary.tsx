import { AlertTriangle, CheckCircle2, Clock3, FileText, type LucideIcon } from 'lucide-react'
import type { PersonalQuoteView } from './quoteDashboardModel'
import { currency } from './quoteDashboardFormatting'
import type { PersonalQuoteStats } from './quoteWorkflowApi'
import './quote-dashboard-summary.css'

interface QuoteDashboardSummaryProps {
  activeCount: number
  overdueCount: number
  completedCount: number
  average: number | null
  averageUnavailable: string
  stats: PersonalQuoteStats | null
  loading: boolean
  selected: PersonalQuoteView
  onSelect: (view: PersonalQuoteView) => void
}

export default function QuoteDashboardSummary({
  activeCount, overdueCount, completedCount, average, averageUnavailable, stats, loading, selected, onSelect,
}: QuoteDashboardSummaryProps) {
  return (
    <section className="quote-kpi-summary" aria-label="My quote workload summary" aria-busy={loading}>
      <div className="quote-kpi-grid">
        <section className="quote-kpi-group" aria-labelledby="quote-kpi-queue">
          <h2 id="quote-kpi-queue">Current queue</h2>
          <div className="quote-kpi-pair">
            <QuoteViewMetric label="Active quotes" icon={FileText} count={loading ? null : activeCount}
              selected={selected === 'active'} onSelect={() => onSelect('active')} />
            <QuoteViewMetric label="Overdue quotes" icon={AlertTriangle} count={loading ? null : overdueCount}
              tone="overdue" selected={selected === 'overdue'} onSelect={() => onSelect('overdue')} />
          </div>
        </section>
        <section className="quote-kpi-group" aria-labelledby="quote-kpi-estimating">
          <h2 id="quote-kpi-estimating">Estimating performance</h2>
          <div className="quote-kpi-pair">
            <QuoteViewMetric label="Completed quotes" icon={CheckCircle2} count={loading ? null : completedCount}
              selected={selected === 'completed'} onSelect={() => onSelect('completed')} />
            <div className="quote-kpi-cell">
              <div className="quote-kpi-metric" title={average === null ? averageUnavailable : 'Assignment to estimating completion in business days'}>
                <span className="quote-kpi-label"><Clock3 aria-hidden="true" />Avg. completion</span>
                {average === null ? <>
                  <strong aria-hidden="true">—</strong>
                  <span className="sr-only">{averageUnavailable}</span>
                </> : <strong>{average.toLocaleString(undefined, { maximumFractionDigits: 1 })} {average === 1 ? 'day' : 'days'}</strong>}
              </div>
            </div>
          </div>
        </section>
        <section className="quote-kpi-group" aria-labelledby="quote-kpi-outcomes">
          <h2 id="quote-kpi-outcomes">Fulcrum outcomes</h2>
          <div className="quote-kpi-pair">
            <OutcomeMetric label="Won" icon={CheckCircle2} tone="won"
              count={loading ? null : stats?.wonCount ?? null} total={loading ? null : stats?.wonTotalValue ?? null} />
            <OutcomeMetric label="Lost" icon={AlertTriangle} tone="lost"
              count={loading ? null : stats?.lostCount ?? null} total={loading ? null : stats?.lostTotalValue ?? null} />
          </div>
        </section>
      </div>
    </section>
  )
}

function QuoteViewMetric({ label, icon: Icon, count, tone, selected, onSelect }: {
  label: string; icon: LucideIcon; count: number | null; tone?: 'overdue'; selected: boolean; onSelect: () => void
}) {
  return (
    <div className="quote-kpi-cell">
      <button className="quote-kpi-metric" data-tone={tone} type="button" aria-pressed={selected}
        aria-controls="my-assigned-quotes" onClick={onSelect}>
        <span className="quote-kpi-label"><Icon aria-hidden="true" />{label}</span>
        <strong>{count?.toLocaleString() ?? '—'}</strong>
      </button>
    </div>
  )
}

function OutcomeMetric({ label, icon: Icon, count, total, tone }: {
  label: string; icon: LucideIcon; count: number | null; total: number | null; tone: 'won' | 'lost'
}) {
  return (
    <div className="quote-kpi-cell">
      <div className="quote-kpi-metric quote-kpi-outcome" data-tone={tone}>
        <span className="quote-kpi-label"><Icon aria-hidden="true" />{label}</span>
        <strong>{count?.toLocaleString() ?? '—'}</strong>
        <span className="quote-kpi-total" title="Total quoted value">{total === null ? '—' : currency(total)}</span>
      </div>
    </div>
  )
}
