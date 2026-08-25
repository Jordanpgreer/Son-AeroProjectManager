import '../App.css'
import { useState } from 'react'
import {
  AlertTriangle,
  Archive,
  ArchiveRestore,
  ArrowRight,
  CheckCircle2,
  ChevronDown,
  ChevronsUpDown,
  Factory,
  Gauge,
  ChevronUp,
  Eye,
  EyeOff,
  LoaderCircle,
  RefreshCw,
  Search,
  Trash2,
  UserRoundCheck,
} from 'lucide-react'
import {
  api,
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
  ArchivedProject,
  ProjectSummary,
  User,
} from '../types'
import { buildPersonalPriorityRanks, isProjectAssignedToUser } from './dashboard-priority'
import { HighlightedText } from './highlighted-text'
import { PermanentDeleteProjectDialog } from './permanent-delete-project-dialog'
import {
  Kpi,
  StatusBar,
  ScheduleChip,
  Progress,
  StatusBadge,
  EmptyState,
} from '../components'

function formatArchivedDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? '—'
    : new Intl.DateTimeFormat(undefined, { month: 'short', day: '2-digit', year: 'numeric' }).format(date)
}

export function DashboardView({
  dashboard,
  search,
  currentUser,
  canReorderPriority,
  onOpenProject,
  onMovePriority,
}: {
  dashboard: Dashboard
  search: string
  currentUser: Pick<User, 'accountName' | 'displayName'> | null
  canReorderPriority: boolean
  onOpenProject: (projectId: number) => Promise<void>
  onMovePriority: (projectId: number, priorityRank: number) => Promise<void>
}) {
  // Completed programs live on the Past Projects page, not here.
  const [sort, setSort] = useState<DashboardSort>({ field: 'priority', dir: 'asc' })
  const [myProjectsOnly, setMyProjectsOnly] = useState(false)
  const active = dashboard.projects.filter((project) => project.status !== 'Complete')
  const myProjects = active.filter((project) => isProjectAssignedToUser(project, currentUser))
  const personalPriorityRanks = buildPersonalPriorityRanks(myProjects)
  const scopedProjects = myProjectsOnly ? myProjects : active
  const normalizedQuery = search.trim()
  const query = normalizedQuery.toLocaleLowerCase()
  const filtered = query
    ? scopedProjects.filter((project) =>
      project.programName.toLowerCase().includes(query) ||
      (project.customerName ?? '').toLowerCase().includes(query) ||
      (project.salesOrderNumber ?? '').toLowerCase().includes(query) ||
      (project.jobNumber ?? '').toLowerCase().includes(query) ||
      project.status.toLowerCase().includes(query))
    : scopedProjects

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
      <div className="kpi-row" data-guide-id="dashboard-summary">
        <Kpi label="Active Programs" value={total.toString()} hint="in the development queue" tone="ink" icon={<Factory size={17} />} />
        <Kpi label="On Track" value={onTrack.toString()} hint={behind > 0 ? 'some need attention' : 'all clear'} tone="ok" icon={<CheckCircle2 size={17} />} />
        <Kpi label="Behind Schedule" value={behind.toString()} hint={behind > 0 ? 'needs attention' : 'all clear'} tone="risk" icon={<AlertTriangle size={17} />} />
        {largestDelay ? (
          <button
            type="button"
            className="kpi-action"
            data-benny-target="largest-delay"
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

      <section className="panel table-panel" data-guide-id="dashboard-projects">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Portfolio Control Board</span>
            <h2>Development Queue</h2>
          </div>
          <div className="dashboard-head-tools">
            {normalizedQuery && (
              <p className="dashboard-filter-status" role="status" aria-live="polite">
                <Search size={13} aria-hidden="true" />
                <span>{total} matching project{total === 1 ? '' : 's'} for <mark>{normalizedQuery}</mark></span>
              </p>
            )}
            <button
              type="button"
              className={`my-projects-filter ${myProjectsOnly ? 'active' : ''}`}
              data-guide-id="dashboard-my-projects"
              data-benny-target="my-projects"
              aria-pressed={myProjectsOnly}
              onClick={() => setMyProjectsOnly((current) => !current)}
              title={myProjectsOnly ? 'Show all active projects' : 'Show projects where you are the engineer or project lead'}
            >
              <UserRoundCheck size={16} />
              <span>{myProjectsOnly ? 'Showing My Projects' : 'My Projects'}</span>
              <span className="my-projects-count">{myProjects.length}</span>
            </button>
            {total > 0 && (
              <StatusBar segments={[
                { key: 'behind', count: behind, label: 'Behind' },
                { key: 'on-track', count: onTrack, label: 'On track' },
                { key: 'not-started', count: notStarted, label: 'Not started' },
              ]} total={total} />
            )}
          </div>
        </header>
        {total === 0 ? (
          <EmptyState
            title={myProjectsOnly ? 'No assigned projects found' : query ? 'No matching programs' : 'No active programs'}
            body={myProjectsOnly
              ? (query ? 'No assigned projects match the current search.' : 'You are not listed as the engineer or project lead on an active project.')
              : query ? 'Try another part number, sales order number, job number, customer name, or status.' : 'Import or add programs to begin tracking schedule progress.'}
          />
        ) : (
          <PortfolioTable
            projects={visible}
            maxPriority={active.length}
            canReorderPriority={canReorderPriority && !myProjectsOnly}
            personalPriorityRanks={myProjectsOnly ? personalPriorityRanks : null}
            sort={sort}
            query={normalizedQuery}
            onSort={handleSort}
            onOpenProject={onOpenProject}
            onMovePriority={onMovePriority}
          />
        )}
      </section>
    </section>
  )
}

export function PastProjectsView({
  projects,
  search,
  canRestoreArchived,
  canDeleteArchived,
  onOpenProject,
  onProjectRestored,
}: {
  projects: ProjectSummary[]
  search: string
  canRestoreArchived: boolean
  canDeleteArchived: boolean
  onOpenProject: (projectId: number) => Promise<void>
  onProjectRestored: () => Promise<void>
}) {
  const [showArchived, setShowArchived] = useState(false)
  const [archivedProjects, setArchivedProjects] = useState<ArchivedProject[] | null>(null)
  const [archivedLoading, setArchivedLoading] = useState(false)
  const [archivedError, setArchivedError] = useState<string | null>(null)
  const [archivedMessage, setArchivedMessage] = useState<string | null>(null)
  const [restoringId, setRestoringId] = useState<number | null>(null)
  const [deletingProject, setDeletingProject] = useState<ArchivedProject | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const completed = projects.filter((project) => project.status === 'Complete')
  const query = search.trim().toLowerCase()
  const visible = query
    ? completed.filter((project) =>
      project.programName.toLowerCase().includes(query) ||
      (project.customerName ?? '').toLowerCase().includes(query) ||
      (project.salesOrderNumber ?? '').toLowerCase().includes(query) ||
      (project.jobNumber ?? '').toLowerCase().includes(query))
    : completed
  const dated = visible.filter((project) => project.targetDelivery && project.finalCompletionDate)
  const onTime = dated.filter((project) => dateToMs(project.finalCompletionDate as string) <= dateToMs(project.targetDelivery as string)).length
  const late = dated.length - onTime
  const onTimePercent = dated.length === 0 ? 0 : onTime / dated.length
  const avgCompletion = visible.length === 0 ? 0 : visible.reduce((sum, project) => sum + project.progress, 0) / visible.length
  const visibleArchived = (archivedProjects ?? []).filter((project) => !query
    || project.programName.toLowerCase().includes(query)
    || (project.customerName ?? '').toLowerCase().includes(query)
    || (project.salesOrderNumber ?? '').toLowerCase().includes(query))

  async function loadArchived() {
    setArchivedLoading(true)
    setArchivedError(null)
    try {
      setArchivedProjects(await api<ArchivedProject[]>('/api/archived-projects'))
    } catch (error) {
      setArchivedError(error instanceof Error ? error.message : 'Archived projects could not be loaded.')
    } finally {
      setArchivedLoading(false)
    }
  }

  async function toggleArchived() {
    if (showArchived) {
      setShowArchived(false)
      return
    }
    setShowArchived(true)
    if (archivedProjects === null) await loadArchived()
  }

  async function restoreArchived(project: ArchivedProject) {
    if (!canRestoreArchived || restoringId !== null) return
    setRestoringId(project.id)
    setArchivedError(null)
    setArchivedMessage(null)
    try {
      await api<void>(`/api/archived-projects/${project.id}/restore`, {
        method: 'POST',
        body: JSON.stringify({ version: project.version }),
      })
      setArchivedProjects((current) => current?.filter((candidate) => candidate.id !== project.id) ?? [])
      setArchivedMessage(`${project.programName} was restored to Project Tracker.`)
      await onProjectRestored()
    } catch (error) {
      setArchivedError(error instanceof Error ? error.message : 'The project could not be restored.')
    } finally {
      setRestoringId(null)
    }
  }

  async function permanentlyDeleteArchived(confirmation: string) {
    if (!canDeleteArchived || deletingProject === null || deleting) return
    setDeleting(true)
    setDeleteError(null)
    setArchivedError(null)
    setArchivedMessage(null)
    try {
      await api<void>(`/api/archived-projects/${deletingProject.id}`, {
        method: 'DELETE',
        body: JSON.stringify({ version: deletingProject.version, confirmation }),
      })
      const deletedName = deletingProject.programName
      setArchivedProjects((current) => current?.filter((candidate) => candidate.id !== deletingProject.id) ?? [])
      setDeletingProject(null)
      setArchivedMessage(`${deletedName} was permanently deleted.`)
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : 'The project could not be permanently deleted.')
    } finally {
      setDeleting(false)
    }
  }

  return (
    <section className="view dashboard-view">
      <div className="kpi-row">
        <Kpi label="Completed Projects" value={visible.length.toString()} hint="completed programs" tone="ink" icon={<Archive size={17} />} />
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
          <div className="past-project-head-tools">
            <button
              type="button"
              className={`archived-project-toggle ${showArchived ? 'active' : ''}`}
              aria-expanded={showArchived}
              aria-controls="archived-projects-panel"
              onClick={() => void toggleArchived()}
            >
              {showArchived ? <EyeOff size={16} /> : <Eye size={16} />}
              {showArchived ? 'Hide Archived' : 'Show Archived'}
              {archivedProjects !== null && <span>{archivedProjects.length}</span>}
            </button>
            {visible.length > 0 && (
              <StatusBar segments={[
                { key: 'on-track', count: onTime, label: 'On time' },
                { key: 'behind', count: late, label: 'Late' },
              ]} total={Math.max(dated.length, 1)} />
            )}
          </div>
        </header>
        {visible.length === 0 ? (
          <EmptyState
            title={query ? 'No matching completed programs' : 'No completed programs yet'}
            body={query ? 'Try another part number, sales order number, job number, or customer name.' : 'A project moves here after an authorized user confirms it is complete.'}
          />
        ) : (
          <PastProjectsTable projects={visible} onOpenProject={onOpenProject} />
        )}
      </section>
      {showArchived && (
        <section className="panel table-panel archived-projects-panel" id="archived-projects-panel" aria-labelledby="archived-projects-heading">
          <header className="panel-head">
            <div className="panel-head-text">
              <span className="kicker">Hidden Records</span>
              <h2 id="archived-projects-heading">Archived Projects · {visibleArchived.length}</h2>
              <p>Archived records stay hidden from normal project views. Restore access is permission controlled.</p>
            </div>
            <button className="icon-button" type="button" disabled={archivedLoading} onClick={() => void loadArchived()}>
              <RefreshCw size={14} className={archivedLoading ? 'spin' : ''} /> Refresh
            </button>
          </header>
          {archivedError && <p className="archived-project-notice error" role="alert"><AlertTriangle size={15} /> {archivedError}</p>}
          {archivedMessage && <p className="archived-project-notice success" role="status"><CheckCircle2 size={15} /> {archivedMessage}</p>}
          {archivedLoading && archivedProjects === null ? (
            <div className="archived-project-loading" role="status"><LoaderCircle size={18} className="spin" /> Loading archived projects...</div>
          ) : visibleArchived.length === 0 ? (
            <EmptyState
              title={query && (archivedProjects?.length ?? 0) > 0 ? 'No matching archived projects' : 'No archived projects'}
              body={query && (archivedProjects?.length ?? 0) > 0 ? 'The archived records do not match the current Past Projects search.' : 'Projects that are archived will be stored here.'}
            />
          ) : (
            <ArchivedProjectsTable
              projects={visibleArchived}
              canRestore={canRestoreArchived}
              canDelete={canDeleteArchived}
              restoringId={restoringId}
              deletingId={deletingProject?.id ?? null}
              onRestore={restoreArchived}
              onDelete={(project) => { setDeleteError(null); setDeletingProject(project) }}
            />
          )}
        </section>
      )}
      {deletingProject && <PermanentDeleteProjectDialog
        project={deletingProject}
        pending={deleting}
        error={deleteError}
        onCancel={() => { if (!deleting) { setDeletingProject(null); setDeleteError(null) } }}
        onConfirm={permanentlyDeleteArchived}
      />}
    </section>
  )
}

function ArchivedProjectsTable({
  projects,
  canRestore,
  canDelete,
  restoringId,
  deletingId,
  onRestore,
  onDelete,
}: {
  projects: ArchivedProject[]
  canRestore: boolean
  canDelete: boolean
  restoringId: number | null
  deletingId: number | null
  onRestore: (project: ArchivedProject) => Promise<void>
  onDelete: (project: ArchivedProject) => void
}) {
  return (
    <div className="table-wrap">
      <table className="data-table archived-projects-table">
        <thead>
          <tr>
            <th>Part / Program</th>
            <th>Customer</th>
            <th>Sales Order</th>
            <th>Archived</th>
            <th>Archived By</th>
            {(canRestore || canDelete) && <th aria-label="Actions" />}
          </tr>
        </thead>
        <tbody>
          {projects.map((project) => (
            <tr key={project.id}>
              <td><span className="archived-project-name"><ArchiveRestore size={15} /><span className="mono-id">{project.programName}</span></span></td>
              <td className="cell-muted">{project.customerName ?? '—'}</td>
              <td className="cell-mono">{project.salesOrderNumber ?? '—'}</td>
              <td className="cell-mono">{formatArchivedDate(project.deletedAt)}</td>
              <td className="cell-muted">{project.deletedByDisplayName ?? '—'}</td>
              {(canRestore || canDelete) && <td className="archived-project-action">
                <div className="archived-project-actions">
                  {canRestore && <button className="icon-button restore" type="button" disabled={restoringId !== null || deletingId !== null} onClick={() => void onRestore(project)}>
                    {restoringId === project.id ? <LoaderCircle size={14} className="spin" /> : <ArchiveRestore size={14} />}
                    {restoringId === project.id ? 'Restoring...' : 'Restore'}
                  </button>}
                  {canDelete && <button className="icon-button danger" type="button" disabled={restoringId !== null || deletingId !== null} onClick={() => onDelete(project)}>
                    <Trash2 size={14} /> Delete
                  </button>}
                </div>
              </td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
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
  personalRank,
  maxPriority,
  canReorderPriority,
  programName,
  onMove,
}: {
  rank: number | null
  personalRank?: number
  maxPriority: number
  canReorderPriority: boolean
  programName: string
  onMove: (rank: number) => Promise<void>
}) {
  const tier = rank === null ? 'none' : rank === 1 ? 'top' : rank <= 3 ? 'high' : 'normal'
  if (personalRank !== undefined) {
    return (
      <div className="priority-pair" aria-label={`Personal priority ${personalRank}, overall priority ${rank ?? 'not set'}`}>
        <span className="priority-rank-block">
          <span className="priority-rank-label">Mine</span>
          <span className="priority-badge personal">{personalRank}</span>
        </span>
        <span className="priority-rank-block">
          <span className="priority-rank-label">Overall</span>
          <span className={`priority-badge tier-${tier}`}>{rank ?? '–'}</span>
        </span>
      </div>
    )
  }

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
  personalPriorityRanks,
  sort,
  query,
  onSort,
  onOpenProject,
  onMovePriority,
}: {
  projects: ProjectSummary[]
  maxPriority: number
  canReorderPriority: boolean
  personalPriorityRanks: Map<number, number> | null
  sort: DashboardSort
  query: string
  onSort: (field: DashboardSortField) => void
  onOpenProject: (projectId: number) => Promise<void>
  onMovePriority: (projectId: number, priorityRank: number) => Promise<void>
}) {
  return (
    <div className="table-wrap">
      <table className="data-table portfolio-table">
        <thead>
          <tr>
            <SortableHeader label={personalPriorityRanks ? 'Priority: Mine / Overall' : 'Priority'} field="priority" sort={sort} onSort={onSort} className={`col-priority ${personalPriorityRanks ? 'dual' : ''}`} />
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
            <tr key={project.id} data-guide-id={`project-row-${project.id}`} className={`clickable-row rail-${statusClass(project.status)}`} onClick={() => onOpenProject(project.id)}>
              <td className={`col-priority ${personalPriorityRanks ? 'dual' : ''}`} onClick={(event) => event.stopPropagation()}>
                <PriorityControl rank={project.priorityRank} personalRank={personalPriorityRanks?.get(project.id)} maxPriority={maxPriority} canReorderPriority={canReorderPriority} programName={project.programName} onMove={(rank) => onMovePriority(project.id, rank)} />
              </td>
              <td>
                <span className="mono-id"><HighlightedText value={project.programName} query={query} /></span>
                {project.customerName && (
                  <span className="dashboard-project-customer">
                    <small className="dashboard-project-customer-label">Customer</small>
                    <span><HighlightedText value={project.customerName} query={query} /></span>
                  </span>
                )}
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
