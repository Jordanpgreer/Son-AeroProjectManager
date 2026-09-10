import { ArrowRightLeft, Download, Inbox, Mail, Paperclip, Send, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { attachmentSize, canAssignMessageToContact, dateTime } from './model'
import type { VendorDetail, VendorMessage } from './types'
import RecordMenu from './RecordMenu'

export default function EmailMessages({ messages, unassignedIds, threads, canEdit, onAssign, canRemove = false, busy = false, onMove, onRemove }: {
  messages: VendorMessage[]; unassignedIds: Set<number>; threads: VendorDetail[]; canEdit: boolean
  onAssign: (messageId: number, requestId: number) => Promise<void>
  canRemove?: boolean; busy?: boolean; onMove?: (message: VendorMessage) => void; onRemove?: (message: VendorMessage) => void
}) {
  const [assigning, setAssigning] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  if (!messages.length) return <div className="vq-empty vq-empty-small"><Inbox size={32} /><h3>No emails here yet</h3><p>Connect Outlook to bring in messages with this quote's subject line. You can keep adding internal updates in the meantime.</p></div>
  return <div className="vq-emails">{error && <p className="vq-error" role="alert">{error}</p>}{[...messages].sort((a, b) => b.sentAt.localeCompare(a.sentAt)).map(message => {
    const outgoing = ['outbound', 'outgoing', 'sent'].includes(message.direction.toLowerCase())
    const Icon = outgoing ? Send : Mail
    const availableThreads = threads.filter(thread => canAssignMessageToContact(message, thread.request.vendorEmail))
    const actions = [...(canEdit && onMove ? [{ label: 'Move email', icon: <ArrowRightLeft size={15} />, onSelect: () => onMove(message) }] : []), ...(canRemove && onRemove ? [{ label: 'Remove from Arda', icon: <Trash2 size={15} />, destructive: true, onSelect: () => onRemove(message) }] : [])]
    return <article className={`vq-email${actions.length ? ' qs-email-has-actions' : ''}`} key={message.id}>
      {actions.length > 0 && <div className="qs-email-actions"><RecordMenu label={`Actions for ${message.subject || 'email'}`} items={actions} disabled={busy || assigning !== null} /></div>}
      {unassignedIds.has(message.id) && <div className="vq-unassigned"><span>Quote-level email · choose a thread when ready</span>
        {canEdit && availableThreads.length > 0 && <select aria-label={`Assign ${message.subject} to a thread`} value="" disabled={assigning !== null || busy} onChange={async event => {
          const id = Number(event.target.value)
          if (!id) return
          setError(null); setAssigning(message.id)
          try { await onAssign(message.id, id) } catch (reason) { setError(reason instanceof Error ? reason.message : 'The email could not be assigned.') } finally { setAssigning(null) }
        }}><option value="">{assigning === message.id ? 'Assigning…' : 'Assign to thread…'}</option>{availableThreads.map(thread => <option key={thread.request.id} value={thread.request.id}>{thread.request.partNumber ? `${thread.request.partNumber} · ` : ''}{thread.request.vendorName || thread.request.title}</option>)}</select>}
        {canEdit && availableThreads.length === 0 && <span>Create a thread for this contact under Parts & threads to organize this email.</span>}
      </div>}
      <details><summary><span className="vq-email-icon"><Icon size={17} /></span><span className="vq-email-summary"><strong>{message.subject}</strong><span>{outgoing ? `To ${message.toAddresses.join(', ')}` : message.fromName || message.fromAddress}</span><small>{outgoing ? 'Sent' : 'Received'} · {dateTime(outgoing ? message.sentAt : message.receivedAt || message.sentAt)}</small></span>{message.attachments.length > 0 && <span className="vq-attachment-count"><Paperclip size={14} />{message.attachments.length}</span>}</summary>
        <div className="vq-email-content"><dl><dt>From</dt><dd>{message.fromName ? `${message.fromName} <${message.fromAddress}>` : message.fromAddress}</dd><dt>To</dt><dd>{message.toAddresses.join(', ')}</dd><dt>Added to Arda</dt><dd>{dateTime(message.importedAt)}</dd></dl>
          <div className="vq-email-body">{message.bodyText || 'This message has no text body.'}</div>
          {message.attachments.length > 0 && <div className="vq-attachments" aria-label="Email attachments">{message.attachments.map(file => <a key={file.id} href={`/api/vendor-quotes/attachments/${file.id}`} download><Paperclip size={16} /><span><strong>{file.fileName}</strong><small>{attachmentSize(file.sizeBytes)}</small></span><Download size={15} /></a>)}</div>}
        </div>
      </details>
    </article>
  })}</div>
}
