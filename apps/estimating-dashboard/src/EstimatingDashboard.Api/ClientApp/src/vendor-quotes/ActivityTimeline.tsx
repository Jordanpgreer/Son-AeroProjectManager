import { ArrowRight, ChevronDown, ChevronUp, Clock3, Mail, MessageSquare, Paperclip, Pencil, Send, Tag, Trash2 } from 'lucide-react'
import { Fragment, useId, useState } from 'react'
import { dateTime, LEGACY_VENDOR_STATUSES, QUOTE_STATUSES, statusTone, VENDOR_STATUSES } from './model'
import type { QuoteActivity, VendorActivity, VendorDetail, VendorMessage } from './types'
import { activityDescription, editableNoteId } from './lifecycleModel'
import { buildActivityTimeline, isOutgoingEmail, scopeActivityTimeline, timelineCategory, timelineInlineContent, timelineSummary } from './activityTimelineModel'
import Modal from './Modal'
import EmailMessages from './EmailMessages'
import './activity-timeline.css'

export function StatusBadge({ status, label = status }: { status: string; label?: string }) {
  return <span className={`vq-status vq-status-${statusTone(status)}`}><i />{label}</span>
}

function NoteText({ text }: { text: string }) {
  return <>{text.split(/(https?:\/\/[^\s<>]+)/g).map((part, index) => {
    if (!/^https?:\/\//.test(part)) return <Fragment key={index}>{part}</Fragment>
    const url = part.replace(/[),.;!?]+$/, '')
    return <Fragment key={index}><a href={url} target="_blank" rel="noopener noreferrer">{url}</a>{part.slice(url.length)}</Fragment>
  })}</>
}

const RECENT_ENTRY_COUNT = 3
const activityDateFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' })

export default function ActivityTimeline({ activity, onThread, canEdit = false, canRemove = false, busy = false, onEdit, onRemove,
  messages = [], threads = [], requestId = null, unassignedIds = new Set<number>(), onAssign, onMoveEmail, onRemoveEmail, onUntrackEmail, onRateRequest }: {
  activity: (QuoteActivity | VendorActivity)[]; onThread?: (id: number) => void
  canEdit?: boolean; canRemove?: boolean; busy?: boolean
  requestId?: number | null
  onEdit?: (item: QuoteActivity | VendorActivity) => void; onRemove?: (item: QuoteActivity | VendorActivity) => void
  messages?: VendorMessage[]; threads?: VendorDetail[]; unassignedIds?: Set<number>
  onAssign?: (messageId: number, requestId: number) => Promise<void>
  onMoveEmail?: (message: VendorMessage) => void; onRemoveEmail?: (message: VendorMessage) => void
  onUntrackEmail?: (message: VendorMessage) => void
  onRateRequest?: (message: VendorMessage, value: boolean) => Promise<void>
}) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [expanded, setExpanded] = useState(false)
  const olderActivityId = useId()
  const entries = scopeActivityTimeline(buildActivityTimeline(activity, messages, threads), requestId)
  const visibleEntries = expanded ? entries : entries.slice(0, RECENT_ENTRY_COUNT)
  const selected = entries.find(entry => entry.key === selectedKey)
  if (!entries.length) return <div className="qs-timeline-empty"><Clock3 size={24} /><div><strong>{requestId === null ? 'No activity yet' : 'No activity in this RFQ yet'}</strong><p>{requestId === null ? 'Use Add Entry to start the record.' : 'Updates and emails assigned to this RFQ will appear here.'}</p></div></div>
  const rows = visibleEntries.map(entry => {
      const item = entry.type === 'activity' ? entry.activity : null
      const email = entry.type === 'email' ? entry.message : null
      const outgoing = email && isOutgoingEmail(email)
      const Icon = email ? outgoing ? Send : Mail : item?.kind.includes('note') ? MessageSquare : Clock3
      const author = email ? email.fromName || email.fromAddress : item!.displayName || item!.accountName || 'Arda'
      const summary = email ? email.subject || 'No subject' : timelineInlineContent(item!) || timelineSummary(item!)
      const timestamp = Date.parse(entry.occurredAt)
      const editable = !email && canEdit && !!onEdit && !!editableNoteId(item!)
      const removable = email ? canRemove && !!onRemoveEmail : canRemove && !!onRemove && !!editableNoteId(item!)
      return <li key={entry.key} className="qs-timeline-entry">
        <button type="button" className="qs-timeline-summary" onClick={() => setSelectedKey(entry.key)} aria-haspopup="dialog">
          <Icon size={15} aria-hidden="true" />
          <span className="qs-timeline-category">{timelineCategory(entry)}</span>
          <span className="qs-timeline-description"><strong>{summary}</strong>{author && <span> · {author}</span>}{entry.threadName && <span> · {entry.partNumber ? `${entry.partNumber} · ` : ''}{entry.threadName}</span>}</span>
          {outgoing && email?.isRateRequest && <span className="qs-timeline-indicator" title="Rates requested"><Tag size={12} aria-hidden="true" /><span className="qs-timeline-sr-only">Rates requested</span></span>}
          {!!email?.attachments.length && <span className="qs-timeline-indicator" title={`${email.attachments.length} attachment${email.attachments.length === 1 ? '' : 's'}`}><Paperclip size={12} aria-hidden="true" />{email.attachments.length}<span className="qs-timeline-sr-only"> attachment{email.attachments.length === 1 ? '' : 's'}</span></span>}
        </button>
        <time className="qs-timeline-date" dateTime={entry.occurredAt} title={dateTime(entry.occurredAt)}>{Number.isFinite(timestamp) ? activityDateFormat.format(timestamp) : 'Not recorded'}</time>
        <div className="qs-timeline-actions">
          {editable && <button type="button" className="vq-icon-button qs-timeline-edit" aria-label={`Edit note by ${author}`} title="Edit note" disabled={busy} onClick={() => onEdit?.(item!)}><Pencil size={15} /></button>}
          {removable && <button type="button" className="vq-icon-button qs-timeline-delete" aria-label={`Remove ${email ? 'email' : 'note'}: ${email?.subject || author}`} title={`Remove ${email ? 'email' : 'note'}`} disabled={busy} onClick={() => email ? onRemoveEmail?.(email) : onRemove?.(item!)}><Trash2 size={15} /></button>}
        </div>
      </li>
  })
  return <>
    <ol className="qs-activity-timeline" aria-label="Recent activity">{rows.slice(0, RECENT_ENTRY_COUNT)}</ol>
    {entries.length > RECENT_ENTRY_COUNT && <>
      <div className="qs-timeline-pagination"><button type="button" className="qs-text-button" aria-expanded={expanded} aria-controls={olderActivityId} onClick={() => setExpanded(value => !value)}>{expanded ? 'Show recent activity' : 'Show older activity'}{expanded ? <ChevronUp size={14} aria-hidden="true" /> : <ChevronDown size={14} aria-hidden="true" />}</button></div>
      <ol id={olderActivityId} className="qs-activity-timeline" aria-label="Older activity" start={RECENT_ENTRY_COUNT + 1} hidden={!expanded}>{rows.slice(RECENT_ENTRY_COUNT)}</ol>
    </>}
    {selected && <Modal title="Entry Details" subtitle={dateTime(selected.occurredAt)} onClose={() => setSelectedKey(null)} wide={selected.type === 'email'}>
      <div className="qs-entry-details">
        {selected.requestId && onThread && <button type="button" className="qs-entry-thread" onClick={() => { setSelectedKey(null); onThread(selected.requestId!) }}>{selected.partNumber ? `${selected.partNumber} · ` : ''}{selected.threadName || 'Open RFQ'}<ArrowRight size={14} /></button>}
        {selected.type === 'email' ? <EmailMessages messages={[selected.message]} unassignedIds={unassignedIds} threads={threads} canEdit={canEdit} onAssign={onAssign} onRateRequest={onRateRequest} canRemove={canRemove} busy={busy} initiallyExpanded
          onMove={onMoveEmail ? message => { setSelectedKey(null); onMoveEmail(message) } : undefined}
          onUntrack={onUntrackEmail ? message => { setSelectedKey(null); onUntrackEmail(message) } : undefined}
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
