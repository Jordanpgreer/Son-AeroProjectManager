import '../App.css'
import { useState } from 'react'
import {
  AlertTriangle,
  Archive,
  ArrowRight,
  CheckCircle2,
  ChevronDown,
  ChevronsUpDown,
  Factory,
  Gauge,
  ChevronUp,
} from 'lucide-react'
import {
  statusClass,
  formatPercent,
  compactDate,
  formatNoteTime,
  dateToMs,
} from '../lib'
import type {
  Dashboard,
  DashboardSortField,
  DashboardSort,
  ProjectSummary,
} from '../types'
import {
  Kpi,
  StatusBar,
  ScheduleChip,
  Progress,
  StatusBadge,
  EmptyState,
} from '../components'

export function DashboardView({
  dashboard,
  search,
  canReorderPriority,
  onOpenProject,
  onMovePriority,
}: {
  dashboard: Dashboard
  search: string
  canReorderPriority: boolean
  onOpenProject: (projectId: number) => Promise<void>
  onMovePriority: (projectId: number, priorityRank: number) => Promise<void>
}) {
  // Completed programs live on the Past Projects page, not here.
  const [sort, setSort] = useState<DashboardSort>({ field: 'priority', dir: 'asc' })
  const active = dashboard.projects.filter((project) => project.status !== 'Complete')
  const query = search.trim().toLowerCase()
  const filtered = query
    ? active.filter((project) =>
      project.programName.toLowerCase().includes(query) ||
      (project.customerName ?? '').toLowerCase().includes(query) ||
      (project.salesOrderNumber ?? '').toLowerCase().includes(query))
    : active

  const handleSort = (field: DashboardSortField) =>
    setSort((current) => current.field === field
      ? { field, dir: current.dir === 'asc' ? 'desc' : 'asc' }
      : { field, dir: field === 'notes' ? 'desc' : 'asc' })

  const sortValue = (project: ProjectSummary): number | null => {
    switch (sort.field) {
      case 'priority': return project.priorityRank
      case 'target': return project.targetDelivery ? Date.parse(project.targetDelivery) : null
      case 'schedule': return project.status === 'Behind' && project.daysBehind !== null
        ? -project.daysBehind
        : project.daysLeft
      case 'notes': return project.recentNote ? Date.parse(project.recentNote.at) : null
    }
  }

  const visible = [...filtered].sort((a, b) => {
    const aValue = sortValue(a)
    const bValue = sortValue(b)
    // Always keep empty values last, regardless of direction.
    if (aValue === null && bValue === null) return (a.priorityRank ?? Number.MAX_SAFE_INTEGER) - (b.priorityRank ?? Number.MAX_SAFE_INTEGER)
    if (aValue === null) return 1
    if (bValue === null) return -1
    return sort.dir === 'asc' ? aValue - bValue : bValue - aValue
  })
  const total = visible.length
  const onTrack = visible.filter((project) => project.status === 'OnTrack').length
  const behind = visible.filter((project) => project.status === 'Behind').length
  const notStarted = visible.filter((project) => project.status === 'NotStarted').length
  const largestDelay = visible
    .filter((project) => project.status === 'Behind' && (project.daysBehind ?? 0) > 0)
    .sort((a, b) => (b.daysBehind ?? 0) - (a.daysBehind ?? 0))[0]
  const largestDelayDays = largestDelay?.daysBehind ?? 0

  return (
    <section className="view dashboard-view">
      <div className="kpi-row">
        <Kpi label="Active Programs" value={total.toString()} hint="in the development queue" tone="ink" icon={<Factory size={17} />} />
        <Kpi label="On Track" value={onTrack.toString()} hint={behind > 0 ? 'some need attention' : 'all clear'} tone="ok" icon={<CheckCircle2 size={17} />} />
        <Kpi label="Behind Schedule" value={behind.toString()} hint={behind > 0 ? 'needs attention' : 'all clear'} tone="risk" icon={<AlertTriangle size={17} />} />
        {largestDelay ? (
          <button
            type="button"
            className="kpi-action"
            onClick={() => void onOpenProject(largestDelay.id)}
            aria-label={`Open ${largestDelay.programName}, the project with the largest delay`}
            title={`Open ${largestDelay.programName}`}
          >
            <Kpi
              label="Largest Delay"
              value={`${largestDelayDays} ${largestDelayDays === 1 ? 'day' : 'days'}`}
              hint={`${largestDelay.programName} is furthest behind`}
              tone="steel"
              icon={<Gauge size={17} />}
            />
          </button>
        ) : (
          <Kpi
            label="Largest Delay"
            value="0 days"
            hint="no delayed projects"
            tone="steel"
            icon={<Gauge size={17} />}
          />
        )}
      </div>

      <section className="panel table-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Portfolio Control Board</span>
            <h2>Development Queue</h2>
          </div>
          {total > 0 && (
            <StatusBar segments={[
              { key: 'behind', count: behind, label: 'Behind' },
              { key: 'on-track', count: onTrack, label: 'On track' },
              { key: 'not-started', count: notStarted, label: 'Not started' },
            ]} total={total} />
          )}
        </header>
        {total === 0 ? (
          <EmptyState
            title={query ? 'No matching programs' : 'No active programs'}
            body={query ? 'Try another part number, sales order number, or customer name.' : 'Import or add programs to begin tracking schedule progress.'}
          />
        ) : (
          <PortfolioTable projects={visible} maxPriority={active.length} canReorderPriority={canReorderPriority} sort={sort} onSort={handleSort} onOpenProject={onOpenProject} onMovePriority={onMovePriority} />
        )}
      </section>
    </section>
  )
}

export function PastProjectsView({ projects, search, onOpenProject }: { projects: ProjectSummary[]; search: string; onOpenProject: (projectId: number) => Promise<void> }) {
  const completed = projects.filter((project) => project.status === 'Complete')
  const query = search.trim().toLowerCase()
  const visible = query
    ? completed.filter((project) =>
      project.programName.toLowerCase().includes(query) ||
      (project.customerName ?? '').toLowerCase().includes(query) ||
      (project.salesOrderNumber ?? '').toLowerCase().includes(query))
    : completed
  const dated = visible.filter((project) => project.targetDelivery && project.finalCompletionDate)
  const onTime = dated.filter((project) => dateToMs(project.finalCompletionDate as string) <= dateToMs(project.targetDelivery as string)).length
  const late = dated.length - onTime
  const onTimePercent = dated.length === 0 ? 0 : onTime / dated.length
  const avgCompletion = visible.length === 0 ? 0 : visible.reduce((sum, project) => sum + project.progress, 0) / visible.length
  return (
    <section className="view dashboard-view">
      <div className="kpi-row">
        <Kpi label="Completed Projects" value={visible.length.toString()} hint="archived programs" tone="ink" icon={<Archive size={17} />} />
        <Kpi label="On Time Percentage" value={formatPercent(onTimePercent)} hint={dated.length === 0 ? 'needs target and completion dates' : `${onTime} on time - ${late} late`} tone="ok" icon={<CheckCircle2 size={17} />} bar={onTimePercent} />
        <Kpi label="Late Projects" value={late.toString()} hint={late > 0 ? 'finished after target' : 'none in filtered set'} tone="risk" icon={<AlertTriangle size={17} />} />
        <Kpi label="Avg Completion" value={formatPercent(avgCompletion)} tone="steel" icon={<Gauge size={17} />} bar={avgCompletion} />
      </div>
      <section className="panel table-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Archive Performance</span>
            <h2>Past Projects · {visible.length}</h2>
            <p>Completed programs with target versus final completion dates.</p>
          </div>
          {visible.length > 0 && (
            <StatusBar segments={[
              { key: 'on-track', count: onTime, label: 'On time' },
              { key: 'behind', count: late, label: 'Late' },
            ]} total={Math.max(dated.length, 1)} />
          )}
        </header>
        {visible.length === 0 ? (
          <EmptyState
            title={query ? 'No matching completed programs' : 'No completed programs yet'}
            body={query ? 'Try another part number, sales order number, or customer name.' : 'A project moves here after an authorized user confirms it is complete.'}
          />
        ) : (
          <PastProjectsTable projects={visible} onOpenProject={onOpenProject} />
        )}
      </section>
    </section>
  )
}

export function PastProjectsTable({ projects, onOpenProject }: { projects: ProjectSummary[]; onOpenProject: (projectId: number) => Promise<void> }) {
  return (
    <div className="table-wrap">
      <table className="data-table portfolio-table past-projects-table">
        <thead>
          <tr>
            <th>Part / Program</th>
            <th>Customer</th>
            <th>Contact Lead</th>
            <th>Engineer</th>
            <th>Target</th>
            <th>Final Completion</th>
            <th className="col-progress">Progress</th>
            <th className="col-status">Result</th>
            <th aria-label="Open" />
          </tr>
        </thead>
        <tbody>
          {projects.map((project) => {
            const hasPerformanceDates = project.targetDelivery !== null && project.finalCompletionDate !== null
            const isLate = project.targetDelivery && project.finalCompletionDate
              ? dateToMs(project.finalCompletionDate) > dateToMs(project.targetDelivery)
              : false
            const resultLabel = hasPerformanceDates ? (isLate ? 'Late' : 'On Time') : 'Completed'
            return (
              <tr key={project.id} className={`clickable-row rail-${isLate ? 'behind' : 'complete'}`} onClick={() => onOpenProject(project.id)}>
                <td><span className="mono-id">{project.programName}</span></td>
                <td className="cell-muted">{project.customerName ?? '—'}</td>
                <td className="cell-muted">{project.programManager ?? '—'}</td>
                <td className="cell-muted">{project.engineer ?? '—'}</td>
                <td className="cell-mono">{compactDate(project.targetDelivery)}</td>
                <td className="cell-mono">{compactDate(project.finalCompletionDate)}</td>
                <td className="col-progress"><Progress value={project.progress} status={project.status} /></td>
                <td className="col-status"><span className={`sched-chip ${isLate ? 'late' : 'done'}`}>{resultLabel}</span></td>
                <td className="cell-go"><ArrowRight size={16} /></td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

export function PriorityControl({
  rank,
  maxPriority,
  canReorderPriority,
  programName,
  onMove,
}: {
  rank: number | null
  maxPriority: number
  canReorderPriority: boolean
  programName: string
  onMove: (rank: number) => Promise<void>
}) {
  const tier = rank === null ? 'none' : rank === 1 ? 'top' : rank <= 3 ? 'high' : 'normal'
  return (
    <div className="priority-cell">
      <span className={`priority-badge tier-${tier}`} title={rank ? `Priority ${rank}` : 'No priority'}>{rank ?? '–'}</span>
      {canReorderPriority && rank && (
        <span className="priority-move">
          <button type="button" onClick={() => void onMove(rank - 1)} disabled={rank <= 1} aria-label={`Raise priority of ${programName}`} title="Higher priority"><ChevronUp size={13} /></button>
          <button type="button" onClick={() => void onMove(rank + 1)} disabled={rank >= maxPriority} aria-label={`Lower priority of ${programName}`} title="Lower priority"><ChevronDown size={13} /></button>
        </span>
      )}
    </div>
  )
}

export function SortableHeader({
  label,
  field,
  sort,
  onSort,
  className = '',
}: {
  label: string
  field: DashboardSortField
  sort: DashboardSort
  onSort: (field: DashboardSortField) => void
  className?: string
}) {
  const activeSort = sort.field === field
  return (
    <th
      className={`sortable ${activeSort ? 'sorted' : ''} ${className}`.trim()}
      onClick={() => onSort(field)}
      aria-sort={activeSort ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none'}
      title={`Sort by ${label.toLowerCase()}`}
    >
      <span className="sortable-label">
        {label}
        {activeSort
          ? (sort.dir === 'asc' ? <ChevronUp size={13} /> : <ChevronDown size={13} />)
          : <ChevronsUpDown size={12} className="sort-hint" />}
      </span>
    </th>
  )
}

export function PortfolioTable({
  projects,
  maxPriority,
  canReorderPriority,
  sort,
  onSort,
  onOpenProject,
  onMovePriority,
}: {
  projects: ProjectSummary[]
  maxPriority: number
  canReorderPriority: boolean
  sort: DashboardSort
  onSort: (field: DashboardSortField) => void
  onOpenProject: (projectId: number) => Promise<void>
  onMovePriority: (projectId: number, priorityRank: number) => Promise<void>
}) {
  return (
    <div className="table-wrap">
      <table className="data-table portfolio-table">
        <thead>
          <tr>
            <SortableHeader label="Priority" field="priority" sort={sort} onSort={onSort} className="col-priority" />
            <th>Part / Program</th>
            <th>Current Operation</th>
            <th>Contact Lead</th>
            <th>Engineer</th>
            <SortableHeader label="Recent Notes" field="notes" sort={sort} onSort={onSort} className="col-notes" />
            <th className="col-progress">Progress</th>
            <SortableHeader label="Target" field="target" sort={sort} onSort={onSort} />
            <SortableHeader label="Schedule" field="schedule" sort={sort} onSort={onSort} />
            <th className="col-status">Status</th>
            <th aria-label="Open" />
          </tr>
        </thead>
        <tbody>
          {projects.map((project) => (
            <tr key={project.id} className={`clickable-row rail-${statusClass(project.status)}`} onClick={() => onOpenProject(project.id)}>
              <td className="col-priority" onClick={(event) => event.stopPropagation()}>
                <PriorityControl rank={project.priorityRank} maxPriority={maxPriority} canReorderPriority={canReorderPriority} programName={project.programName} onMove={(rank) => onMovePriority(project.id, rank)} />
              </td>
              <td>
                <span className="mono-id">{project.programName}</span>
              </td>
              <td className="cell-op">{project.currentTask ?? '—'}</td>
              <td className="cell-muted">{project.programManager ?? '—'}</td>
              <td className="cell-muted">{project.engineer ?? '—'}</td>
              <td className="col-notes">
                {project.recentNote ? (
                  <div className="recent-note" title={`${project.recentNote.step} · ${formatNoteTime(project.recentNote.at)}\n\n${project.recentNote.note}`}>
                    <span className="recent-note-text">{project.recentNote.note}</span>
                    <span className="recent-note-meta">{project.recentNote.step} · {formatNoteTime(project.recentNote.at)}</span>
                  </div>
                ) : <span className="cell-muted">—</span>}
              </td>
              <td className="col-progress"><Progress value={project.progress} status={project.status} /></td>
              <td className="cell-mono">{compactDate(project.targetDelivery)}</td>
              <td><ScheduleChip daysLeft={project.daysLeft} daysBehind={project.daysBehind} status={project.status} /></td>
              <td className="col-status"><StatusBadge status={project.status} /></td>
              <td className="cell-go"><ArrowRight size={16} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/* ---------------------------------------------------------------------- */
/* Program detail                                                         */
/* ---------------------------------------------------------------------- */
