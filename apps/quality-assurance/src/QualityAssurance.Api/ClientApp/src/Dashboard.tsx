import { useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  Download,
  PackageCheck,
  UserRoundCheck,
} from 'lucide-react'
import { qualityApi } from './api'
import DashboardShipmentQuickView from './DashboardShipmentQuickView'
import { ageInDays, formatCurrency, formatDate, formatDuration } from './format'
import type { DashboardData, PersonQueue, Shipment } from './types'
import './Dashboard.css'

type TeamSelection = number | 'group' | 'unassigned' | null

export default function Dashboard({ reloadKey }: { reloadKey: number }) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const [selectedShipment, setSelectedShipment] = useState<Shipment | null>(null)
  const [selectedTeam, setSelectedTeam] = useState<TeamSelection>(null)

  useEffect(() => {
    let active = true
    setError(null)
    void qualityApi<DashboardData>('/api/dashboard')
      .then((next) => {
        if (!active) return
        setData(next)
        setSelectedTeam((current) => current ?? (
          next.unassignedQueue.open > 0
            ? 'unassigned'
            : next.groupQueue.open > 0
              ? 'group'
              : next.teamQueues[0]?.userId ?? null
        ))
      })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Dashboard unavailable.') })
    return () => { active = false }
  }, [reloadKey, refreshKey])

  const teamTotals = useMemo(() => {
    if (!data) return { open: 0, overdue: 0, openDollarValue: null as number | null }
    const metrics = [
      ...data.teamQueues.map((person) => person.metrics),
      data.groupQueue,
      data.unassignedQueue,
    ]
    const visibleValues = metrics.map((item) => item.openDollarValue).filter((value): value is number => value != null)
    return {
      open: metrics.reduce((total, item) => total + item.open, 0),
      overdue: metrics.reduce((total, item) => total + item.overdue, 0),
      openDollarValue: visibleValues.length ? visibleValues.reduce((total, value) => total + value, 0) : null,
    }
  }, [data])

  if (error) return <section className="panel error-panel"><AlertTriangle size={20} /><div><h2>Dashboard unavailable</h2><p>{error}</p></div></section>
  if (!data) return <div className="loading-panel" role="status">Loading your shipping queue...</div>
  const includesUnassigned = data.unassignedQueue.open > 0
  const selectedPerson = typeof selectedTeam === 'number'
    ? data.teamQueues.find((person) => person.userId === selectedTeam) ?? null
    : null
  const selectedLabel = selectedTeam === 'unassigned'
    ? 'Needs assignment'
    : selectedTeam === 'group'
      ? 'Group queue'
      : selectedPerson?.displayName ?? 'Select a team member'
  const selectedMetrics = selectedTeam === 'unassigned'
    ? data.unassignedQueue
    : selectedTeam === 'group'
      ? data.groupQueue
      : selectedPerson?.metrics ?? null
  const selectedShipments = selectedTeam === 'unassigned'
    ? data.unassignedShipments
    : selectedTeam === 'group'
      ? data.groupShipments
      : selectedPerson?.openShipments ?? []
  const maxTeamOpen = Math.max(
    1,
    data.unassignedQueue.open,
    data.groupQueue.open,
    ...data.teamQueues.map((person) => person.metrics.open),
  )

  function accepted(shipment: Shipment) {
    setSelectedShipment(shipment)
    if (shipment.assignedUserId) setSelectedTeam(shipment.assignedUserId)
    else if (shipment.assignedGroupId) setSelectedTeam('group')
    else setSelectedTeam('unassigned')
    setRefreshKey((value) => value + 1)
  }

  return (
    <div className="view dashboard-view">
      <section className="metric-grid" aria-label="My shipping queue statistics">
        <article className="metric-card accent"><span className="metric-icon"><PackageCheck size={18} /></span><div><span>Open in my queue</span><strong>{data.myQueue.open}</strong><small>{includesUnassigned ? 'Includes unassigned manager review' : 'Oldest work appears first'}</small></div></article>
        <article className={`metric-card ${data.myQueue.overdue ? 'risk' : ''}`}><span className="metric-icon"><AlertTriangle size={18} /></span><div><span>Past due</span><strong>{data.myQueue.overdue}</strong><small>Based on Ship By</small></div></article>
        <article className="metric-card"><span className="metric-icon"><CheckCircle2 size={18} /></span><div><span>Completed</span><strong>{data.myQueue.completed}</strong><small>All shipped assignments</small></div></article>
        <article className="metric-card"><span className="metric-icon"><Clock3 size={18} /></span><div><span>Average completion</span><strong>{formatDuration(data.myQueue.averageCompletionHours)}</strong><small>Arrival in queue to shipped</small></div></article>
      </section>

      <section className="dashboard-layout">
        <article className="panel queue-panel">
          <header className="panel-head">
            <div><span className="eyebrow">{includesUnassigned ? 'My work + manager review' : 'My work'}</span><h2>Queue priority</h2><p>{includesUnassigned ? 'Unassigned records are included so managers can assign them without leaving the dashboard.' : 'Ordered oldest first, with Ship By risk visible alongside age.'}</p></div>
            <span className="dashboard-hint">Select a row for quick view</span>
          </header>
          {data.queue.length ? (
            <div className="queue-list">
              {data.queue.map((shipment, index) => (
                <button className="queue-item" type="button" onClick={() => setSelectedShipment(shipment)} key={shipment.id}>
                  <span className="queue-rank">{String(index + 1).padStart(2, '0')}</span>
                  <span className="queue-primary"><strong>{shipment.salesOrderNumber || `Shipment ${shipment.id}`}</strong><small>{shipment.customer || 'Customer hidden'} · {shipment.partNumber || 'Part hidden'}</small></span>
                  <span className={`queue-action ${!shipment.assignedGroupId && !shipment.assignedUserId ? 'unassigned' : ''}`}>{shipment.assignedDisplayName ?? (shipment.assignedGroupName ? `${shipment.assignedGroupName} queue` : shipment.nextAction || 'Needs assignment')}</span>
                  <span className="queue-age"><strong>{ageInDays(shipment.createdAt)}d</strong><small>in queue</small></span>
                  <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState === 'Hidden' ? 'Due date hidden' : `${shipment.dueState} · ${formatDate(shipment.shipDate)}`}</span>
                </button>
              ))}
            </div>
          ) : <div className="empty-state"><CheckCircle2 size={25} /><h3>Your queue is clear</h3><p>New assignments will appear here automatically.</p></div>}
        </article>

        {data.canViewTeam && (
          <article className="panel team-panel manager-statistics-hub">
            <header className="panel-head">
              <div><span className="eyebrow">Management view</span><h2>Team workload</h2><p>Select a person to review their open work, schedule exposure, and assignment balance.</p></div>
              <a className="button primary" href="/api/dashboard/report" download><Download size={15} /> Download team report</a>
            </header>

            <div className="team-summary-strip" aria-label="Team summary">
              <div><span>Team open</span><strong>{teamTotals.open.toLocaleString()}</strong></div>
              <div className={teamTotals.overdue ? 'is-risk' : ''}><span>Team past due</span><strong>{teamTotals.overdue.toLocaleString()}</strong></div>
              <div><span>Open dollar value</span><strong>{teamTotals.openDollarValue == null ? 'Restricted' : formatCurrency(teamTotals.openDollarValue)}</strong></div>
              <div><span>Group queue</span><strong>{data.groupQueue.open.toLocaleString()}</strong></div>
              <div><span>Needs assignment</span><strong>{data.unassignedQueue.open.toLocaleString()}</strong></div>
            </div>

            <div className="team-stat-grid" aria-label="Team queue statistics">
              {data.unassignedQueue.open > 0 && (
                <TeamStatCard label="Needs assignment" account="Manager review" metrics={data.unassignedQueue} selected={selectedTeam === 'unassigned'} maxOpen={maxTeamOpen} onClick={() => setSelectedTeam('unassigned')} unassigned />
              )}
              {data.groupQueue.open > 0 && (
                <TeamStatCard label="Group queue" account="Awaiting an individual owner" metrics={data.groupQueue} selected={selectedTeam === 'group'} maxOpen={maxTeamOpen} onClick={() => setSelectedTeam('group')} groupQueue />
              )}
              {data.teamQueues.map((person) => (
                <TeamStatCard key={person.userId} label={person.displayName} account={person.accountName} metrics={person.metrics} selected={selectedTeam === person.userId} maxOpen={maxTeamOpen} onClick={() => setSelectedTeam(person.userId)} />
              ))}
            </div>

            {selectedMetrics && (
              <section className="team-drilldown" aria-label={`${selectedLabel} open workload`}>
                <header>
                  <div><span className="eyebrow">Selected workload</span><h3>{selectedLabel}</h3></div>
                  <div className="team-drilldown-summary"><span><b>{selectedMetrics.open}</b> open</span><span className={selectedMetrics.overdue ? 'risk-number' : ''}><b>{selectedMetrics.overdue}</b> past due</span><span><b>{formatMetricCurrency(selectedMetrics.openDollarValue)}</b> open value</span></div>
                </header>
                {selectedShipments.length ? (
                  <div className="team-work-list">
                    {selectedShipments.map((shipment) => (
                      <button type="button" key={shipment.id} onClick={() => setSelectedShipment(shipment)}>
                        <span><strong>{shipment.salesOrderNumber ?? `Shipment ${shipment.id}`}</strong><small>{shipment.customer ?? 'Customer hidden'} · {shipment.partNumber ?? 'Part hidden'}</small></span>
                        <span><strong>{formatDate(shipment.shipDate)}</strong><small>Ship by</small></span>
                        <span><strong>{formatCurrency(shipment.dollarValue)}</strong><small>Open value</small></span>
                        <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><i />{shipment.dueState}</span>
                        <UserRoundCheck size={16} aria-hidden="true" />
                      </button>
                    ))}
                  </div>
                ) : <div className="empty-state compact"><CheckCircle2 size={21} /><h3>No open work</h3><p>This team member's queue is clear.</p></div>}
              </section>
            )}
          </article>
        )}
      </section>
      {selectedShipment && <DashboardShipmentQuickView shipment={selectedShipment} fields={data.fields} canViewAssignment={data.canViewAssignment} canAssign={data.canAssign} canAssignGroup={data.canAssignGroup} canAssignUser={data.canAssignUser} onClose={() => setSelectedShipment(null)} onSaved={accepted} />}
    </div>
  )
}

function TeamStatCard({ label, account, metrics, selected, maxOpen, onClick, unassigned = false, groupQueue = false }: {
  label: string
  account: string
  metrics: PersonQueue['metrics']
  selected: boolean
  maxOpen: number
  onClick: () => void
  unassigned?: boolean
  groupQueue?: boolean
}) {
  return (
    <button className={`team-stat-card ${selected ? 'is-selected' : ''} ${unassigned ? 'is-unassigned' : ''} ${groupQueue ? 'is-group-queue' : ''}`.trim()} type="button" aria-pressed={selected} onClick={onClick}>
      <span className="team-stat-card-head"><span className="team-avatar">{unassigned ? '?' : groupQueue ? 'GQ' : label.split(/\s+/).map((part) => part[0]).join('').slice(0, 2).toUpperCase()}</span><span><strong>{label}</strong><small>{account}</small></span></span>
      <span className="team-stat-values"><span><small>Open</small><b>{metrics.open}</b></span><span className={metrics.overdue ? 'is-risk' : ''}><small>Past due</small><b>{metrics.overdue}</b></span><span><small>Open value</small><b>{formatMetricCurrency(metrics.openDollarValue)}</b></span><span><small>Avg. time</small><b>{formatDuration(metrics.averageCompletionHours)}</b></span></span>
      <span className="team-load-track" aria-hidden="true"><i style={{ width: `${Math.max(metrics.open ? 8 : 0, metrics.open / maxOpen * 100)}%` }} /></span>
    </button>
  )
}

function formatMetricCurrency(value: number | null) {
  return value == null ? 'Restricted' : formatCurrency(value)
}
