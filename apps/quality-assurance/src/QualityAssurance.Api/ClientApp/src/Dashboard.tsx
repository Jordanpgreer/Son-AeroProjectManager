import { useEffect, useState } from 'react'
import { AlertTriangle, ArrowRight, CheckCircle2, Clock3, PackageCheck, UsersRound } from 'lucide-react'
import { qualityApi } from './api'
import { ageInDays, formatDate, formatDuration } from './format'
import type { DashboardData } from './types'

export default function Dashboard({ reloadKey }: { reloadKey: number }) {
  const [data, setData] = useState<DashboardData | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    setError(null)
    void qualityApi<DashboardData>('/api/dashboard')
      .then((next) => { if (active) setData(next) })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Dashboard unavailable.') })
    return () => { active = false }
  }, [reloadKey])

  if (error) return <section className="panel error-panel"><AlertTriangle size={20} /><div><h2>Dashboard unavailable</h2><p>{error}</p></div></section>
  if (!data) return <div className="loading-panel" role="status">Loading your shipping queue...</div>

  return (
    <div className="view dashboard-view">
      <section className="metric-grid" aria-label="My shipping queue statistics">
        <article className="metric-card accent"><span className="metric-icon"><PackageCheck size={18} /></span><div><span>Open in my queue</span><strong>{data.myQueue.open}</strong><small>Oldest work appears first</small></div></article>
        <article className={`metric-card ${data.myQueue.overdue ? 'risk' : ''}`}><span className="metric-icon"><AlertTriangle size={18} /></span><div><span>Past due</span><strong>{data.myQueue.overdue}</strong><small>Based on required ship date</small></div></article>
        <article className="metric-card"><span className="metric-icon"><CheckCircle2 size={18} /></span><div><span>Completed</span><strong>{data.myQueue.completed}</strong><small>All shipped assignments</small></div></article>
        <article className="metric-card"><span className="metric-icon"><Clock3 size={18} /></span><div><span>Average completion</span><strong>{formatDuration(data.myQueue.averageCompletionHours)}</strong><small>Arrival in queue to shipped</small></div></article>
      </section>

      <section className="dashboard-layout">
        <article className="panel queue-panel">
          <header className="panel-head">
            <div><span className="eyebrow">My work</span><h2>Queue priority</h2><p>Ordered oldest first, with ship-date risk visible alongside age.</p></div>
            <a className="button ghost" href="#/shipping-status">Open Shipping Status <ArrowRight size={15} /></a>
          </header>
          {data.queue.length ? (
            <div className="queue-list">
              {data.queue.map((shipment, index) => (
                <a className="queue-item" href={`#/shipping-status?shipment=${shipment.id}`} key={shipment.id}>
                  <span className="queue-rank">{String(index + 1).padStart(2, '0')}</span>
                  <span className="queue-primary"><strong>{shipment.salesOrderNumber || `Shipment ${shipment.id}`}</strong><small>{shipment.customer || 'Customer hidden'} · {shipment.partNumber || 'Part hidden'}</small></span>
                  <span className="queue-type">{shipment.taskType || 'Task type hidden'}</span>
                  <span className="queue-age"><strong>{ageInDays(shipment.createdAt)}d</strong><small>in queue</small></span>
                  <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState === 'Hidden' ? 'Due date hidden' : `${shipment.dueState} · ${formatDate(shipment.shipDate)}`}</span>
                </a>
              ))}
            </div>
          ) : <div className="empty-state"><CheckCircle2 size={25} /><h3>Your queue is clear</h3><p>New assignments will appear here automatically.</p></div>}
        </article>

        {data.canViewTeam && (
          <article className="panel team-panel">
            <header className="panel-head"><div><span className="eyebrow">Management view</span><h2>Queue load by person</h2><p>Open work, risk, and throughput across permitted team members.</p></div><UsersRound size={22} /></header>
            <div className="team-table-wrap">
              <table className="team-table">
                <thead><tr><th>Person</th><th>Open</th><th>Past due</th><th>Completed</th><th>Avg. time</th></tr></thead>
                <tbody>{data.teamQueues.map((person) => (
                  <tr key={person.userId}>
                    <td><strong>{person.displayName}</strong><small>{person.accountName}</small></td>
                    <td>{person.metrics.open}</td>
                    <td className={person.metrics.overdue ? 'risk-number' : ''}>{person.metrics.overdue}</td>
                    <td>{person.metrics.completed}</td>
                    <td>{formatDuration(person.metrics.averageCompletionHours)}</td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
          </article>
        )}
      </section>
    </div>
  )
}
