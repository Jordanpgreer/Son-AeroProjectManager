import { ArrowUpRight, CalendarDays, GitBranch, Mail, Plus } from 'lucide-react'
import { dateOnly, isOverdue, relativeTime } from './model'
import { StatusBadge } from './ActivityTimeline'
import type { VendorDetail } from './types'

export default function ThreadGroups({ threads, onSelect, onNew, canEdit }: {
  threads: VendorDetail[]; onSelect: (id: number) => void; onNew: () => void; canEdit: boolean
}) {
  const grouped = new Map<string, VendorDetail[]>()
  for (const thread of threads) {
    const key = thread.request.partNumber || 'General'
    grouped.set(key, [...(grouped.get(key) || []), thread])
  }
  if (!threads.length) return <div className="vq-empty vq-empty-small"><GitBranch size={30} /><h3>Give each moving part its own thread</h3><p>Track vendors and other follow-ups under a part number. Each thread has its own status, notes, and emails.</p>{canEdit && <button className="vq-button vq-button-primary" onClick={onNew}><Plus size={15} /> Add the first thread</button>}</div>
  return <div className="vq-thread-groups">{[...grouped].map(([part, items]) => <section key={part}><header><span><GitBranch size={15} />{part}</span><small>{items.length} thread{items.length === 1 ? '' : 's'}</small></header><div className="vq-thread-grid">{items.map(({ request }) => <button className="vq-thread-card" key={request.id} onClick={() => onSelect(request.id)}>
    <div className="vq-thread-card-top"><strong>{request.vendorName || request.title}</strong><ArrowUpRight size={15} /></div>
    {request.vendorName && <p>{request.title}</p>}
    <StatusBadge status={request.status} />
    <footer><span><Mail size={12} />{request.messageCount}</span>{request.followUpDate ? <span className={isOverdue(request.followUpDate, request.status) ? 'vq-overdue-text' : ''}><CalendarDays size={12} />{dateOnly(request.followUpDate)}</span> : <span>{relativeTime(request.updatedAt)}</span>}</footer>
  </button>)}</div></section>)}</div>
}
