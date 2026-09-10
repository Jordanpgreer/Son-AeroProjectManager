import { ArrowRight, Clock3, Mail, MessageSquare, UserRound } from 'lucide-react'
import { dateTime, QUOTE_STATUSES, relativeTime, statusTone, VENDOR_STATUSES } from './model'
import type { QuoteActivity, VendorActivity } from './types'

export function StatusBadge({ status }: { status: string }) {
  return <span className={`vq-status vq-status-${statusTone(status)}`}><i />{status}</span>
}

export default function ActivityTimeline({ activity, onThread }: {
  activity: (QuoteActivity | VendorActivity)[]; onThread?: (id: number) => void
}) {
  const sorted = [...activity].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt))
  if (!sorted.length) return <div className="vq-empty vq-empty-small"><Clock3 size={28} /><h3>A clear record starts here</h3><p>Add an update to capture the current status, decisions, and what happens next.</p></div>
  return <ol className="vq-timeline" aria-label="Activity history">{sorted.map(item => {
    const kind = item.kind.toLowerCase()
    const Icon = kind.includes('note') ? MessageSquare : kind.includes('email') || kind.includes('message') ? Mail : Clock3
    const scope = 'requestId' in item && item.requestId ? item : null
    return <li key={item.id} className={`vq-event${kind.includes('note') ? ' vq-event-note' : ''}`}>
      <span className="vq-event-icon"><Icon size={15} /></span>
      <article><header><strong>{item.displayName || item.accountName || 'Arda'}</strong><time dateTime={item.occurredAt} title={dateTime(item.occurredAt)}>{relativeTime(item.occurredAt)}</time></header>
        <div className="vq-event-meta"><span>{dateTime(item.occurredAt)}</span>{scope && <button type="button" onClick={() => onThread?.(scope.requestId!)}>{scope.partNumber ? `${scope.partNumber} · ` : ''}{scope.vendorName || 'Thread'}</button>}</div>
        <p className="vq-note-text">{item.text}</p>
        {item.newValue && <div className="vq-event-change">{item.oldValue && <><span>{item.oldValue}</span><ArrowRight size={13} /></>}{QUOTE_STATUSES.includes(item.newValue) || VENDOR_STATUSES.includes(item.newValue) ? <StatusBadge status={item.newValue} /> : <strong>{item.newValue}</strong>}</div>}
        {kind.includes('note') && <span className="vq-private-label"><UserRound size={12} /> Internal note</span>}
      </article>
    </li>
  })}</ol>
}
