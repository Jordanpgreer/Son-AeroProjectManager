import { Mail, MessageSquare, RotateCcw, Trash2 } from 'lucide-react'
import { dateTime } from './model'
import type { QuoteStatusDetail } from './types'

export default function RemovedItems({ detail, canRestore, busy, onRestoreEmail, onRestoreNote }: {
  detail: QuoteStatusDetail; canRestore: boolean; busy: boolean
  onRestoreEmail: (id: number) => void; onRestoreNote: (id: string) => void
}) {
  const messages = detail.removedMessages || []
  const notes = detail.removedNotes || []
  if (!messages.length && !notes.length) return <div className="vq-empty vq-empty-small"><Trash2 size={30} /><h3>No removed items</h3><p>Emails and internal notes removed from this quote appear here, so they can be restored later.</p></div>
  return <div className="qs-removed-items"><p className="qs-removed-intro">Removed items stay available here. Emails remain in Outlook and will not be re-added by sync.</p>
    {messages.map(message => <article className="qs-removed-item" key={`email-${message.id}`}><span className="qs-removed-icon"><Mail size={18} /></span><div><h4>{message.subject || '(No subject)'}</h4><p>{message.fromName || message.fromAddress}</p>{message.partNumber || message.vendorName ? <small>{message.partNumber ? `${message.partNumber} · ` : ''}{message.vendorName || 'Thread'}</small> : <small>Quote record</small>}<span>Removed {dateTime(message.removedAt)}{message.removedBy ? ` by ${message.removedBy}` : ''}</span></div>{canRestore && <button className="vq-button" disabled={busy} onClick={() => onRestoreEmail(message.id)}><RotateCcw size={14} />Restore email</button>}</article>)}
    {notes.map(note => <article className="qs-removed-item" key={note.id}><span className="qs-removed-icon"><MessageSquare size={18} /></span><div><h4>Internal note</h4><p className="qs-removed-note">{note.text}</p><small>{note.displayName || note.accountName} · {dateTime(note.occurredAt)}</small>{note.partNumber || note.vendorName ? <small>{note.partNumber ? `${note.partNumber} · ` : ''}{note.vendorName || 'Thread'}</small> : null}<span>Removed {dateTime(note.removedAt || null)}{note.removedBy ? ` by ${note.removedBy}` : ''}</span></div>{canRestore && <button className="vq-button" disabled={busy} onClick={() => onRestoreNote(note.id)}><RotateCcw size={14} />Restore note</button>}</article>)}
  </div>
}
