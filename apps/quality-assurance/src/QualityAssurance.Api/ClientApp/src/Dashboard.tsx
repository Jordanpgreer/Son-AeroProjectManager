import { useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Download,
  DollarSign,
  PackageCheck,
} from 'lucide-react'
import { qualityApi } from './api'
import DashboardShipmentQuickView from './DashboardShipmentQuickView'
import { ageInDays, formatCurrency, formatDate, formatDuration } from './format'
import { canRunQualityAction } from './workflowPermissions'
import type { DashboardData, PersonQueue, QualityAssuranceUser, Shipment } from './types'
import './Dashboard.css'

type TeamSelection = number | 'group' | 'unassigned' | null
type QueueFilter = 'open' | 'overdue' | 'completed' | 'average' | 'unassigned'

const queuePageSize = 12

export default function Dashboard({ user, reloadKey, onOpenShipment }: { user: QualityAssuranceUser; reloadKey: number; onOpenShipment?: (shipment: Shipment) => void }) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const [selectedShipment, setSelectedShipment] = useState<Shipment | null>(null)
  const [selectedTeam, setSelectedTeam] = useState<TeamSelection>(null)
  const [queueFilter, setQueueFilter] = useState<QueueFilter>('open')
  const [requestedPage, setRequestedPage] = useState(1)

  useEffect(() => {
    let active = true
    setError(null)
    const query = new URLSearchParams({ page: String(requestedPage), pageSize: String(queuePageSize) })
    void qualityApi<DashboardData>(`/api/dashboard?${query}`)
      .then((next) => {
        if (!active) return
        setData(next)
      })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Dashboard unavailable.') })
    return () => { active = false }
  }, [reloadKey, refreshKey, requestedPage, user.permissions, user.workflowRestrictedActions])

  const teamTotals = useMemo(() => {
    if (!data) return { open: 0, overdue: 0, openDollarValue: null as number | null }
    const metrics = [
      ...data.teamQueues.map((person) => person.metrics),
      data.groupQueue,
      ...(data.canReviewUnassigned ? [data.unassignedQueue] : []),
    ]
    const visibleValues = metrics.map((item) => item.openDollarValue).filter((value): value is number => value != null)
    return {
      open: metrics.reduce((total, item) => total + item.open, 0),
      overdue: metrics.reduce((total, item) => total + item.overdue, 0),
      openDollarValue: visibleValues.length ? visibleValues.reduce((total, value) => total + value, 0) : null,
    }
  }, [data])

  function selectQueue(filter: QueueFilter, team: TeamSelection = null) {
    setSelectedTeam(team)
    setQueueFilter(filter)
    setRequestedPage(1)
  }

  if (error) return <section className="panel error-panel"><AlertTriangle size={20} /><div><h2>Dashboard Unavailable</h2><p>{error}</p></div></section>
  if (!data) return <div className="loading-panel" role="status">Loading your shipping queue...</div>
  const includesUnassigned = data.canReviewUnassigned && data.unassignedQueue.open > 0
  const selectedPerson = typeof selectedTeam === 'number'
    ? data.teamQueues.find((person) => person.userId === selectedTeam) ?? null
    : null
  const selectedLabel = selectedTeam === 'unassigned'
    ? 'Needs Assignment'
    : selectedTeam === 'group'
      ? 'Group Queue'
      : selectedPerson?.displayName ?? 'Select a Team Member'
  const selectedShipments = selectedTeam === 'unassigned'
    ? data.unassignedShipments
    : selectedTeam === 'group'
      ? data.groupShipments
      : selectedPerson?.openShipments ?? data.queue
  const visibleQueue = queueFilter === 'completed' || queueFilter === 'average'
    ? selectedTeam === null ? data.completedShipments : []
    : queueFilter === 'unassigned'
      ? data.unassignedShipments
    : queueFilter === 'overdue'
      ? selectedShipments.filter((shipment) => shipment.dueState === 'Past due')
      : selectedShipments
  const queueTitle = queueFilter === 'unassigned'
    ? 'Needs Assignment'
    : queueFilter === 'overdue'
    ? 'Past Due Queue'
    : queueFilter === 'completed' || queueFilter === 'average'
      ? 'Completed Shipments'
      : 'Queue Priority'
  const selectedMetrics = selectedTeam === 'unassigned'
    ? data.unassignedQueue
    : selectedTeam === 'group'
      ? data.groupQueue
      : selectedPerson?.metrics ?? data.myQueue
  const activeQueueTotal = queueFilter === 'completed' || queueFilter === 'average'
    ? selectedTeam === null ? data.myQueue.completed : 0
    : queueFilter === 'overdue'
      ? selectedMetrics.overdue
      : queueFilter === 'unassigned'
        ? data.unassignedQueue.open
        : selectedTeam === null ? data.queueTotal ?? data.myQueue.open : selectedMetrics.open
  const activePage = data.queuePage ?? requestedPage
  const activePageSize = data.queuePageSize ?? queuePageSize
  const totalPages = Math.max(1, Math.ceil(activeQueueTotal / activePageSize))
  const firstVisibleRecord = activeQueueTotal === 0 ? 0 : (activePage - 1) * activePageSize + 1
  const lastVisibleRecord = visibleQueue.length === 0
    ? 0
    : Math.min(activeQueueTotal, firstVisibleRecord + visibleQueue.length - 1)
  const maxTeamOpen = Math.max(
    1,
    data.canReviewUnassigned ? data.unassignedQueue.open : 0,
    data.groupQueue.open,
    ...data.teamQueues.map((person) => person.metrics.open),
  )

  function accepted(shipment: Shipment) {
    setSelectedShipment(shipment)
    setQueueFilter('open')
    if (shipment.assignedUserId) setSelectedTeam(shipment.assignedUserId)
    else if (shipment.assignedGroupId) setSelectedTeam('group')
    else setSelectedTeam('unassigned')
    setRequestedPage(1)
    setRefreshKey((value) => value + 1)
  }

  return (
    <div className="view dashboard-view">
      <section className="metric-grid" aria-label="My shipping queue statistics">
        <button type="button" aria-pressed={queueFilter === 'open' && selectedTeam === null} className={`metric-card accent metric-filter ${queueFilter === 'open' && selectedTeam === null ? 'is-selected' : ''}`} onClick={() => selectQueue('open')}><span className="metric-icon"><PackageCheck size={18} /></span><div><span>Open In My Queue</span><strong>{data.myQueue.open}</strong><small>{includesUnassigned ? 'Includes Unassigned Manager Review' : 'Oldest Work Appears First'}</small></div></button>
        <button type="button" aria-pressed={queueFilter === 'overdue' && selectedTeam === null} className={`metric-card metric-filter ${data.myQueue.overdue ? 'risk' : ''} ${queueFilter === 'overdue' && selectedTeam === null ? 'is-selected' : ''}`} onClick={() => selectQueue('overdue')}><span className="metric-icon"><AlertTriangle size={18} /></span><div><span>Past Due</span><strong>{data.myQueue.overdue}</strong><small>Based On Ship By</small></div></button>
        <button type="button" aria-pressed={queueFilter === 'completed' && selectedTeam === null} className={`metric-card metric-filter ${queueFilter === 'completed' && selectedTeam === null ? 'is-selected' : ''}`} onClick={() => selectQueue('completed')}><span className="metric-icon"><CheckCircle2 size={18} /></span><div><span>Completed</span><strong>{data.myQueue.completed}</strong><small>All Shipped Assignments</small></div></button>
        <button type="button" aria-pressed={queueFilter === 'average' && selectedTeam === null} className={`metric-card metric-filter ${queueFilter === 'average' && selectedTeam === null ? 'is-selected' : ''}`} onClick={() => selectQueue('average')}><span className="metric-icon"><Clock3 size={18} /></span><div><span>Average Completion</span><strong>{formatDuration(data.myQueue.averageCompletionHours)}</strong><small>Shows The Completed Records Used</small></div></button>
        {data.myQueue.openDollarValue != null && <article className="metric-card" aria-label={`Open dollar value ${formatCurrency(data.myQueue.openDollarValue)}`}><span className="metric-icon"><DollarSign size={18} /></span><div><span>Open Dollar Value</span><strong>{formatCurrency(data.myQueue.openDollarValue)}</strong><small>Current Assigned Work</small></div></article>}
        {data.canReviewUnassigned && <button type="button" aria-pressed={queueFilter === 'unassigned'} className={`metric-card metric-filter ${data.unassignedQueue.open ? 'risk' : ''} ${queueFilter === 'unassigned' ? 'is-selected' : ''}`} onClick={() => selectQueue('unassigned', 'unassigned')}><span className="metric-icon"><AlertTriangle size={18} /></span><div><span>Needs Assignment</span><strong>{data.unassignedQueue.open}</strong><small>Unassigned Manager Review</small></div></button>}
      </section>

      <section className="dashboard-layout">
        <article className="panel queue-panel">
          <header className="panel-head">
            <div><span className="eyebrow">{selectedTeam === null ? includesUnassigned ? 'My Work + Manager Review' : 'My Work' : selectedLabel}</span><h2>{queueTitle}</h2><p>Ordered oldest first. Select the summary boxes above or a team box below to filter this queue.</p></div>
            <span className="dashboard-hint">Select A Row For Quick View</span>
          </header>
          {visibleQueue.length ? (
            <div className="queue-list">
              {visibleQueue.map((shipment, index) => (
                <button className="queue-item" type="button" onClick={() => setSelectedShipment(shipment)} key={shipment.id}>
                  <span className="queue-rank">{String((activePage - 1) * activePageSize + index + 1).padStart(2, '0')}</span>
                  <span className="queue-primary"><strong>{shipment.salesOrderNumber || `Shipment ${shipment.id}`}</strong><small>{shipment.customer || 'Customer hidden'} · {shipment.partNumber || 'Part hidden'}</small></span>
                  <span className={`queue-action ${!shipment.assignedGroupId && !shipment.assignedUserId ? 'unassigned' : ''}`}>{shipment.assignedDisplayName ?? (shipment.assignedGroupName ? `${shipment.assignedGroupName} Queue` : 'Needs Assignment')}</span>
                  <span className="queue-age"><strong>{ageInDays(shipment.createdAt)}d</strong><small>In Queue</small></span>
                  <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState === 'Hidden' ? 'Due Date Hidden' : `${shipment.dueState} · ${formatDate(shipment.shipDate)}`}</span>
                </button>
              ))}
            </div>
          ) : <div className="empty-state"><CheckCircle2 size={25} /><h3>No Matching Shipments</h3><p>Choose another summary or team box to change the queue filter.</p></div>}
          {(activeQueueTotal > activePageSize || activePage > 1) && <footer className="dashboard-pagination" aria-label="Shipment queue pages">
            <button className="button ghost compact" type="button" disabled={activePage <= 1} onClick={() => setRequestedPage(Math.max(1, activePage - 1))}><ChevronLeft size={14} /> Previous</button>
            <span><strong>Page {activePage} of {totalPages}</strong><small>Showing {firstVisibleRecord}-{lastVisibleRecord} of {activeQueueTotal}</small></span>
            <button className="button ghost compact" type="button" disabled={activePage >= totalPages} onClick={() => setRequestedPage(Math.min(totalPages, activePage + 1))}>Next <ChevronRight size={14} /></button>
          </footer>}
        </article>

        {data.canViewTeam && (
          <article className="panel team-panel manager-statistics-hub">
            <header className="panel-head">
              <div><span className="eyebrow">Management View</span><h2>Team Workload</h2><p>Select a person to review their open work, schedule exposure, and assignment balance.</p></div>
              <a className="button primary" href="/api/dashboard/report" download><Download size={15} /> Download Team Report</a>
            </header>

            <div className="team-summary-strip" aria-label="Team summary">
              <div><span>Team Open</span><strong>{teamTotals.open.toLocaleString()}</strong></div>
              <div className={teamTotals.overdue ? 'is-risk' : ''}><span>Team Past Due</span><strong>{teamTotals.overdue.toLocaleString()}</strong></div>
              <div><span>Open Dollar Value</span><strong>{teamTotals.openDollarValue == null ? 'Restricted' : formatCurrency(teamTotals.openDollarValue)}</strong></div>
              <div><span>Group Queue</span><strong>{data.groupQueue.open.toLocaleString()}</strong></div>
              {data.canReviewUnassigned && <div><span>Needs Assignment</span><strong>{data.unassignedQueue.open.toLocaleString()}</strong></div>}
            </div>

            <div className="team-stat-grid" aria-label="Team queue statistics">
              {data.canReviewUnassigned && data.unassignedQueue.open > 0 && (
                <TeamStatCard label="Needs Assignment" description="Manager Review" metrics={data.unassignedQueue} selected={selectedTeam === 'unassigned'} maxOpen={maxTeamOpen} onClick={() => selectedTeam === 'unassigned' ? selectQueue('open') : selectQueue('unassigned', 'unassigned')} unassigned />
              )}
              {data.groupQueue.open > 0 && (
                <TeamStatCard label="Team Queue" description="Awaiting An Individual Owner" metrics={data.groupQueue} selected={selectedTeam === 'group'} maxOpen={maxTeamOpen} onClick={() => selectQueue('open', selectedTeam === 'group' ? null : 'group')} groupQueue />
              )}
              {data.teamQueues.map((person) => (
                <TeamStatCard key={person.userId} label={person.displayName} description="Quality Team Member" metrics={person.metrics} selected={selectedTeam === person.userId} maxOpen={maxTeamOpen} onClick={() => selectQueue('open', selectedTeam === person.userId ? null : person.userId)} />
              ))}
            </div>
          </article>
        )}
      </section>
      {selectedShipment && <DashboardShipmentQuickView shipment={selectedShipment} fields={data.fields} canViewAssignment={data.canViewAssignment} canAssign={canRunQualityAction(user, 'assignment-changed', data.canAssign)} canAssignGroup={canRunQualityAction(user, 'assignment-changed', data.canAssignGroup)} canAssignUser={canRunQualityAction(user, 'assignment-changed', data.canAssignUser)} onClose={() => setSelectedShipment(null)} onOpen={onOpenShipment ? () => onOpenShipment(selectedShipment) : undefined} onSaved={accepted} />}
    </div>
  )
}

function TeamStatCard({ label, description, metrics, selected, maxOpen, onClick, unassigned = false, groupQueue = false }: {
  label: string
  description: string
  metrics: PersonQueue['metrics']
  selected: boolean
  maxOpen: number
  onClick: () => void
  unassigned?: boolean
  groupQueue?: boolean
}) {
  return (
    <button className={`team-stat-card ${selected ? 'is-selected' : ''} ${unassigned ? 'is-unassigned' : ''} ${groupQueue ? 'is-group-queue' : ''}`.trim()} type="button" aria-pressed={selected} onClick={onClick}>
      <span className="team-stat-card-head"><span className="team-avatar">{unassigned ? '?' : groupQueue ? 'GQ' : label.split(/\s+/).map((part) => part[0]).join('').slice(0, 2).toUpperCase()}</span><span><strong>{label}</strong><small>{description}</small></span></span>
      <span className="team-stat-values"><span><small>Open</small><b>{metrics.open}</b></span><span className={metrics.overdue ? 'is-risk' : ''}><small>Past Due</small><b>{metrics.overdue}</b></span><span><small>Open Value</small><b>{formatMetricCurrency(metrics.openDollarValue)}</b></span><span><small>Avg. Time</small><b>{formatDuration(metrics.averageCompletionHours)}</b></span></span>
      <span className="team-load-track" aria-hidden="true"><i style={{ width: `${Math.max(metrics.open ? 8 : 0, metrics.open / maxOpen * 100)}%` }} /></span>
    </button>
  )
}

function formatMetricCurrency(value: number | null) {
  return value == null ? 'Restricted' : formatCurrency(value)
}
