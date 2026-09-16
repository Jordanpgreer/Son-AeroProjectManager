import { ArrowRight, ChevronDown, ChevronUp, Clock3, Mail, MessageSquare, MousePointerClick, Paperclip, Pencil, Send, Tag, Trash2 } from 'lucide-react'
import { Fragment, useId, useState } from 'react'
import { dateTime, LEGACY_VENDOR_STATUSES, QUOTE_STATUSES, relativeTime, statusTone, VENDOR_STATUSES } from './model'
import type { QuoteActivity, VendorActivity, VendorDetail, VendorMessage } from './types'
import { activityDescription, editableNoteId } from './lifecycleModel'
import { buildActivityTimeline, isOutgoingEmail, timelineSummary } from './activityTimelineModel'
import Modal from './Modal'
import EmailMessages from './EmailMessages'
import './activity-timeline.css'

export function StatusBadge({ status }: { status: string }) {
  return <span className={`vq-status vq-status-${statusTone(status)}`}><i />{status}</span>
}

function NoteText({ text }: { text: string }) {
  return <>{text.split(/(https?:\/\/[^\s<>]+)/g).map((part, index) => {
    if (!/^https?:\/\//.test(part)) return <Fragment key={index}>{part}</Fragment>
    const url = part.replace(/[),.;!?]+$/, '')
    return <Fragment key={index}><a href={url} target="_blank" rel="noopener noreferrer">{url}</a>{part.slice(url.length)}</Fragment>
  })}</>
}

export default function ActivityTimeline({ activity, onThread, canEdit = false, canRemove = false, busy = false, onEdit, onRemove,
  messages = [], threads = [], unassignedIds = new Set<number>(), onAssign, onMoveEmail, onRemoveEmail, onRateRequest }: {
  activity: (QuoteActivity | VendorActivity)[]; onThread?: (id: number) => void
  canEdit?: boolean; canRemove?: boolean; busy?: boolean
  onEdit?: (item: QuoteActivity | VendorActivity) => void; onRemove?: (item: QuoteActivity | VendorActivity) => void
  messages?: VendorMessage[]; threads?: VendorDetail[]; unassignedIds?: Set<number>
  onAssign?: (messageId: number, requestId: number) => Promise<void>
  onMoveEmail?: (message: VendorMessage) => void; onRemoveEmail?: (message: VendorMessage) => void
  onRateRequest?: (message: VendorMessage, value: boolean) => Promise<void>
}) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [expanded, setExpanded] = useState(false)
  const timelineId = useId()
  const entries = buildActivityTimeline(activity, messages, threads)
  const visibleEntries = expanded ? entries : entries.slice(0, 5)
  const selected = entries.find(entry => entry.key === selectedKey)
  if (!entries.length) return <div className="qs-timeline-empty"><Clock3 size={24} /><div><strong>No activity yet</strong><p>Use Add Entry to start the record.</p></div></div>
  return <>
    <ol id={timelineId} className="qs-activity-timeline" aria-label="Activity timeline">{visibleEntries.map(entry => {
      const item = entry.type === 'activity' ? entry.activity : null
      const email = entry.type === 'email' ? entry.message : null
      const outgoing = email && isOutgoingEmail(email)
      const Icon = email ? outgoing ? Send : Mail : item?.kind.includes('note') ? MessageSquare : Clock3
      const author = email ? email.fromName || email.fromAddress : item!.displayName || item!.accountName || 'Arda'
      const summary = email ? `${outgoing ? 'Sent' : 'Received'} an email${email.subject ? `: ${email.subject}` : '.'}` : timelineSummary(item!)
      const removable = email ? canRemove && !!onRemoveEmail : canRemove && !!onRemove && !!editableNoteId(item!)
      return <li key={entry.key} className="qs-timeline-entry">
        <span className={`qs-timeline-icon${email ? ' is-email' : ''}`}><Icon size={15} /></span>
        <button type="button" className="qs-timeline-open" onClick={() => setSelectedKey(entry.key)} aria-label={`View entry: ${author} — ${summary}`}>
          <span className="qs-timeline-summary"><strong>{author}</strong><span> — {summary}</span></span>
          <span className="qs-timeline-meta"><time dateTime={entry.occurredAt} title={dateTime(entry.occurredAt)}>{relativeTime(entry.occurredAt)}</time>{entry.threadName && <span>{entry.partNumber ? `${entry.partNumber} · ` : ''}{entry.threadName}</span>}{email && email.attachments.length > 0 && <span><Paperclip size={11} />{email.attachments.length}</span>}{outgoing && email?.isRateRequest && <span className="qs-rate-request-badge"><Tag size={11} />Rates requested</span>}</span>
          <MousePointerClick className="qs-timeline-pointer" size={15} aria-hidden="true" />
        </button>
        {removable && <button type="button" className="vq-icon-button qs-timeline-delete" aria-label={`Remove ${email ? 'email' : 'note'}: ${email?.subject || author}`} title={`Remove ${email ? 'email' : 'note'}`} disabled={busy} onClick={() => email ? onRemoveEmail?.(email) : onRemove?.(item!)}><Trash2 size={15} /></button>}
      </li>
    })}</ol>
    {entries.length > 5 && <footer className="qs-timeline-pagination"><span aria-live="polite">Showing {visibleEntries.length} of {entries.length} entries</span><button type="button" className="qs-text-button" aria-expanded={expanded} aria-controls={timelineId} onClick={() => setExpanded(value => !value)}>{expanded ? <ChevronUp size={15} /> : <ChevronDown size={15} />}{expanded ? 'Show less' : `Show all ${entries.length} entries`}</button></footer>}
    {selected && <Modal title="Entry Details" subtitle={dateTime(selected.occurredAt)} onClose={() => setSelectedKey(null)} wide={selected.type === 'email'}>
      <div className="qs-entry-details">
        {selected.requestId && onThread && <button type="button" className="qs-entry-thread" onClick={() => { setSelectedKey(null); onThread(selected.requestId!) }}>{selected.partNumber ? `${selected.partNumber} · ` : ''}{selected.threadName || 'Open RFQ'}<ArrowRight size={14} /></button>}
        {selected.type === 'email' ? <EmailMessages messages={[selected.message]} unassignedIds={unassignedIds} threads={threads} canEdit={canEdit} onAssign={onAssign} onRateRequest={onRateRequest} canRemove={canRemove} busy={busy} initiallyExpanded
          onMove={onMoveEmail ? message => { setSelectedKey(null); onMoveEmail(message) } : undefined}
          onRemove={onRemoveEmail ? message => { setSelectedKey(null); onRemoveEmail(message) } : undefined} /> : <>
          <div className="qs-entry-author"><strong>{selected.activity.displayName || selected.activity.accountName || 'Arda'}</strong><span>{selected.activity.kind === 'note' ? 'Internal note' : 'Activity entry'}</span></div>
          <p className="qs-entry-note"><NoteText text={activityDescription(selected.activity)} /></p>
          {selected.activity.kind === 'note-edited' ? <div className="qs-note-history">{selected.activity.oldValue && <div><span>Previous note</span><p><NoteText text={selected.activity.oldValue} /></p></div>}{selected.activity.newValue && <div><span>Updated note</span><p><NoteText text={selected.activity.newValue} /></p></div>}</div> : selected.activity.newValue && <div className="vq-event-change">{selected.activity.oldValue && <><span>{selected.activity.oldValue}</span><ArrowRight size={13} /></>}{QUOTE_STATUSES.includes(selected.activity.newValue) || VENDOR_STATUSES.includes(selected.activity.newValue) || LEGACY_VENDOR_STATUSES.includes(selected.activity.newValue) ? <StatusBadge status={selected.activity.newValue} /> : <strong>{selected.activity.newValue}</strong>}</div>}
          {selected.activity.editedAt && <span className="qs-edited-label">Edited {dateTime(selected.activity.editedAt)}{selected.activity.editedBy ? ` by ${selected.activity.editedBy}` : ''}</span>}
          {editableNoteId(selected.activity) && <footer className="vq-modal-footer">{canRemove && onRemove && <button type="button" className="vq-button qs-remove-button" disabled={busy} onClick={() => { setSelectedKey(null); onRemove(selected.activity) }}><Trash2 size={15} />Remove note</button>}{canEdit && onEdit && <button type="button" className="vq-button" disabled={busy} onClick={() => { setSelectedKey(null); onEdit(selected.activity) }}><Pencil size={15} />Edit note</button>}</footer>}
        </>}
      </div>
    </Modal>}
  </>
}
