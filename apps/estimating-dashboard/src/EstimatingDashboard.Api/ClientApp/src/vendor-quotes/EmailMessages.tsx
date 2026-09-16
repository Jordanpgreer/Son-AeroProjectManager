import { ArrowRightLeft, Download, Inbox, Mail, Paperclip, Send, Tag, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { attachmentSize, canAssignMessageToContact, dateTime } from './model'
import type { VendorDetail, VendorMessage } from './types'
import RecordMenu from './RecordMenu'
import { rateRequestThread } from './rateRequestModel'

export default function EmailMessages({ messages, unassignedIds, threads, canEdit, onAssign, canRemove = false, busy = false, onMove, onRemove, initiallyExpanded = false, onRateRequest }: {
  messages: VendorMessage[]; unassignedIds: Set<number>; threads: VendorDetail[]; canEdit: boolean
  onAssign?: (messageId: number, requestId: number) => Promise<void>; initiallyExpanded?: boolean
  canRemove?: boolean; busy?: boolean; onMove?: (message: VendorMessage) => void; onRemove?: (message: VendorMessage) => void
  onRateRequest?: (message: VendorMessage, value: boolean) => Promise<void>
}) {
  const [assigning, setAssigning] = useState<number | null>(null)
  const [labelling, setLabelling] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const pending = busy || assigning !== null || labelling !== null
  async function changeRateRequest(message: VendorMessage) {
    if (!onRateRequest || pending) return
    setError(null); setLabelling(message.id)
    try { await onRateRequest(message, !message.isRateRequest) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'The email label could not be saved.') }
    finally { setLabelling(null) }
  }
  if (!messages.length) return <div className="vq-empty vq-empty-small"><Inbox size={32} /><h3>No emails here yet</h3><p>Connect Outlook to bring in messages with this quote's subject line. You can keep adding internal updates in the meantime.</p></div>
  return <div className="vq-emails">{error && <p className="vq-error" role="alert">{error}</p>}{[...messages].sort((a, b) => b.sentAt.localeCompare(a.sentAt)).map(message => {
    const outgoing = ['outbound', 'outgoing', 'sent'].includes(message.direction.toLowerCase())
    const Icon = outgoing ? Send : Mail
    const availableThreads = threads.filter(thread => canAssignMessageToContact(message, thread.request.vendorEmail))
    const rateThread = rateRequestThread(message, threads)
    const canLabel = canEdit && !!onRateRequest && !!rateThread?.canEdit && !unassignedIds.has(message.id)
    const labelAction = message.isRateRequest ? 'Remove Rates requested label' : 'Mark as Rates requested'
    const actions = [...(canLabel ? [{ label: labelAction, icon: <Tag size={15} />, onSelect: () => { void changeRateRequest(message) } }] : []), ...(canEdit && onMove ? [{ label: 'Move email', icon: <ArrowRightLeft size={15} />, onSelect: () => onMove(message) }] : []), ...(canRemove && onRemove ? [{ label: 'Remove from Arda', icon: <Trash2 size={15} />, destructive: true, onSelect: () => onRemove(message) }] : [])]
    return <article className={`vq-email${actions.length ? ' qs-email-has-actions' : ''}`} key={message.id}>
      {actions.length > 0 && <div className="qs-email-actions"><RecordMenu label={`Actions for ${message.subject || 'email'}`} items={actions} disabled={pending} /></div>}
      {unassignedIds.has(message.id) && <div className="vq-unassigned"><span>Quote-level email · choose a thread when ready</span>
        {canEdit && onAssign && availableThreads.length > 0 && <select aria-label={`Assign ${message.subject} to a thread`} value="" disabled={pending} onChange={async event => {
          const id = Number(event.target.value)
          if (!id) return
          setError(null); setAssigning(message.id)
          try { await onAssign(message.id, id) } catch (reason) { setError(reason instanceof Error ? reason.message : 'The email could not be assigned.') } finally { setAssigning(null) }
        }}><option value="">{assigning === message.id ? 'Assigning…' : 'Assign to thread…'}</option>{availableThreads.map(thread => <option key={thread.request.id} value={thread.request.id}>{thread.request.partNumber ? `${thread.request.partNumber} · ` : ''}{thread.request.vendorName || thread.request.title}</option>)}</select>}
        {canEdit && availableThreads.length === 0 && <span>Add an RFQ in Outside Processing and Materials to organize this email.</span>}
      </div>}
      <details open={initiallyExpanded || undefined}><summary><span className="vq-email-icon"><Icon size={17} /></span><span className="vq-email-summary"><strong>{message.subject}</strong><span>{outgoing ? `To ${message.toAddresses.join(', ')}` : message.fromName || message.fromAddress}</span><small>{outgoing ? 'Sent' : 'Received'} · {dateTime(outgoing ? message.sentAt : message.receivedAt || message.sentAt)}</small>{outgoing && message.isRateRequest && <span className="qs-rate-request-badge"><Tag size={11} />Rates requested</span>}</span>{message.attachments.length > 0 && <span className="vq-attachment-count"><Paperclip size={14} />{message.attachments.length}</span>}</summary>
        <div className="vq-email-content"><dl><dt>From</dt><dd>{message.fromName ? `${message.fromName} <${message.fromAddress}>` : message.fromAddress}</dd><dt>To</dt><dd>{message.toAddresses.join(', ')}</dd><dt>Added to Arda</dt><dd>{dateTime(message.importedAt)}</dd></dl>
          {canLabel && <div className="qs-email-rate-request"><p>{message.isRateRequest ? 'Removing this label leaves the RFQ status unchanged.' : `Sets ${rateThread!.title} to Waiting on vendor.`}</p><button type="button" className="vq-button" disabled={pending} onClick={() => { void changeRateRequest(message) }}><Tag size={14} />{labelling === message.id ? 'Saving label…' : labelAction}</button></div>}
          {outgoing && canEdit && onRateRequest && !rateThread && <p className="qs-rate-request-hint">Assign this sent email to an RFQ to mark it Rates requested.</p>}
          <div className="vq-email-body">{message.bodyText || 'This message has no text body.'}</div>
          {message.attachments.length > 0 && <div className="vq-attachments" aria-label="Email attachments">{message.attachments.map(file => <a key={file.id} href={`/api/vendor-quotes/attachments/${file.id}`} download><Paperclip size={16} /><span><strong>{file.fileName}</strong><small>{attachmentSize(file.sizeBytes)}</small></span><Download size={15} /></a>)}</div>}
        </div>
      </details>
    </article>
  })}</div>
}
