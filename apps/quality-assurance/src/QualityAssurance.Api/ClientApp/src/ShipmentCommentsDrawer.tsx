import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, ArrowLeft, AtSign, MessageSquare, Send } from 'lucide-react'
import { qualityApi } from './api'
import type {
  MentionableUser,
  QualityAssuranceUser,
  Shipment,
  ShipmentComment,
} from './types'
import './shipment-comments.css'

function initials(name: string) {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (!parts.length) return 'QA'
  return parts.length === 1
    ? parts[0].slice(0, 2).toUpperCase()
    : `${parts[0][0]}${parts.at(-1)?.[0] ?? ''}`.toUpperCase()
}

function messageTime(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return 'Unknown time'
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(date)
}

function renderMessage(body: string) {
  return body.split(/(@[A-Za-z0-9._-]+)/g).map((part, index) =>
    part.startsWith('@')
      ? <span className="quality-chat-mention" key={`${part}-${index}`}>{part}</span>
      : part,
  )
}

export default function ShipmentCommentsDrawer({
  shipment,
  currentUser,
  canPost,
  onClose,
  onMessageSent,
}: {
  shipment: Shipment
  currentUser: QualityAssuranceUser
  canPost: boolean
  onClose: () => void
  onMessageSent?: (comment: ShipmentComment) => void
}) {
  const [comments, setComments] = useState<ShipmentComment[]>([])
  const [users, setUsers] = useState<MentionableUser[]>([])
  const [draft, setDraft] = useState('')
  const [loading, setLoading] = useState(true)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const latestId = useRef(0)
  const messageList = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let mounted = true
    const merge = (incoming: ShipmentComment[], replace = false) => {
      if (!mounted) return
      setComments((current) => {
        const next = replace
          ? incoming
          : [...current, ...incoming.filter((comment) => !current.some((item) => item.id === comment.id))]
        latestId.current = next.at(-1)?.id ?? 0
        return next
      })
    }

    const load = async () => {
      setLoading(true)
      setError(null)
      try {
        const [thread, mentionable] = await Promise.all([
          qualityApi<ShipmentComment[]>(`/api/shipments/${shipment.id}/comments`),
          qualityApi<MentionableUser[]>(`/api/shipments/${shipment.id}/comment-mentions`),
        ])
        merge(thread, true)
        if (mounted) setUsers(mentionable)
      } catch (cause) {
        if (mounted) setError(cause instanceof Error ? cause.message : 'Unable to load shipment comments.')
      } finally {
        if (mounted) setLoading(false)
      }
    }

    const poll = async () => {
      try {
        const incoming = await qualityApi<ShipmentComment[]>(
          `/api/shipments/${shipment.id}/comments?afterId=${latestId.current}`,
        )
        merge(incoming)
      } catch {
        // Quietly retry on the next interval. Sending errors remain visible.
      }
    }

    void load()
    const interval = window.setInterval(() => void poll(), 8000)
    return () => {
      mounted = false
      window.clearInterval(interval)
    }
  }, [shipment.id])

  useEffect(() => {
    messageList.current?.scrollTo({
      top: messageList.current.scrollHeight,
      behavior: loading ? 'auto' : 'smooth',
    })
  }, [comments, loading])

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  const mentionMatch = draft.match(/(^|\s)@([A-Za-z0-9._-]*)$/)
  const mentionQuery = mentionMatch?.[2].toLowerCase() ?? ''
  const suggestions = mentionMatch
    ? users
      .filter((candidate) =>
        candidate.mentionHandle.toLowerCase().includes(mentionQuery)
        || candidate.displayName.toLowerCase().includes(mentionQuery))
      .slice(0, 5)
    : []

  function insertMention(user: MentionableUser) {
    if (!mentionMatch) return
    const before = draft.slice(0, draft.length - mentionMatch[0].length)
    setDraft(`${before}${mentionMatch[1]}@${user.mentionHandle} `)
  }

  async function send() {
    const body = draft.trim()
    if (!body || sending || !canPost) return
    setSending(true)
    setError(null)
    try {
      const comment = await qualityApi<ShipmentComment>(`/api/shipments/${shipment.id}/comments`, {
        method: 'POST',
        body: JSON.stringify({ body }),
      })
      setComments((current) => current.some((item) => item.id === comment.id)
        ? current
        : [...current, comment])
      latestId.current = comment.id
      setDraft('')
      onMessageSent?.(comment)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to send the comment.')
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="quality-chat-layer" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose()
    }}>
      <aside className="quality-chat-drawer" role="dialog" aria-modal="true" aria-labelledby="quality-chat-title" aria-describedby="quality-chat-context">
        <header className="quality-chat-head">
          <div>
            <span className="eyebrow">Shipment Communication</span>
            <h2 id="quality-chat-title">Comments</h2>
            <p id="quality-chat-context">{shipment.salesOrderNumber ?? `Shipment ${shipment.id}`} · {shipment.partNumber ?? 'Part number hidden'}</p>
          </div>
          <button className="quality-chat-back" type="button" onClick={onClose} autoFocus><ArrowLeft size={16} /><span>Back to shipment</span></button>
        </header>

        <div className="quality-chat-messages" ref={messageList} aria-live="polite">
          {loading ? (
            <div className="quality-chat-state">Loading conversation…</div>
          ) : comments.length === 0 ? (
            <div className="quality-chat-empty">
              <MessageSquare size={25} />
              <strong>No Comments Yet</strong>
              <span>Start the shipment conversation below.</span>
            </div>
          ) : comments.map((comment) => {
            const own = comment.authorAccountName.toLowerCase() === currentUser.accountName.toLowerCase()
            return (
              <article className={`quality-chat-message ${own ? 'own' : ''}`} key={comment.id}>
                <span className="quality-chat-avatar" aria-hidden="true">{initials(comment.authorDisplayName)}</span>
                <div className="quality-chat-bubble">
                  <header>
                    <strong>{comment.authorDisplayName}</strong>
                    <time dateTime={comment.createdAt}>{messageTime(comment.createdAt)}</time>
                  </header>
                  <p>{renderMessage(comment.body)}</p>
                  {comment.isLegacyImport && <small className="quality-chat-legacy">Imported from the original Comments field</small>}
                </div>
              </article>
            )
          })}
        </div>

        <footer className="quality-chat-composer">
          {error && <div className="quality-chat-error" role="alert"><AlertTriangle size={14} /><span>{error}</span></div>}
          {canPost ? <>
            <div className="quality-chat-input-wrap">
              {suggestions.length > 0 && (
                <div className="quality-mention-menu" role="listbox" aria-label="Mention a Quality user">
                  {suggestions.map((candidate) => (
                    <button type="button" role="option" aria-selected="false" key={candidate.userId} onClick={() => insertMention(candidate)}>
                      <span className="quality-chat-avatar small">{initials(candidate.displayName)}</span>
                      <span><strong>{candidate.displayName}</strong><small>@{candidate.mentionHandle}</small></span>
                    </button>
                  ))}
                </div>
              )}
              <textarea
                value={draft}
                onChange={(event) => setDraft(event.target.value.slice(0, 2000))}
                onKeyDown={(event) => {
                  if (event.key !== 'Enter' || event.shiftKey) return
                  event.preventDefault()
                  if (suggestions.length > 0) insertMention(suggestions[0])
                  else void send()
                }}
                placeholder="Write a comment… Use @ to tag someone"
                aria-label="Shipment comment"
                rows={3}
              />
              <div className="quality-chat-composer-meta">
                <span><AtSign size={13} /> Tag people with @</span>
                <span>{draft.length}/2000</span>
              </div>
            </div>
            <button className="button primary quality-chat-send" type="button" onClick={() => void send()} disabled={!draft.trim() || sending}>
              <Send size={15} /> {sending ? 'Sending' : 'Send'}
            </button>
          </> : <p className="quality-chat-readonly">You can read this conversation, but your role cannot add comments.</p>}
        </footer>
      </aside>
    </div>
  )
}
