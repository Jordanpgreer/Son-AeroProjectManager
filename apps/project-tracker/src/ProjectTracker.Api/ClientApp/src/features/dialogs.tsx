import '../App.css'
import { useState, useEffect, useRef } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronRight,
  History,
  MessageSquare,
  RefreshCw,
  Save,
  Send,
  Trash2,
  X,
  AtSign,
} from 'lucide-react'
import {
  api,
  userInitials,
  formatChatTime,
  formatActivityTime,
  activityActionClass,
  activityActionIcon,
  renderChatMessage,
} from '../lib'
import type {
  User,
  ProjectDetail,
  ProjectConfirmation,
  ConcurrencyConflict,
  ProjectAuditEntry,
  ProjectMessage,
  MentionableUser,
} from '../types'
import {
  SkeletonLine,
} from '../components'

export function ProjectConfirmationDialog({
  action,
  projectName,
  pending,
  onCancel,
  onConfirm,
}: {
  action: ProjectConfirmation
  projectName: string
  pending: boolean
  onCancel: () => void
  onConfirm: () => Promise<void>
}) {
  const deleting = action === 'delete'
  const reopening = action === 'reopen'

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !pending) onCancel()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onCancel, pending])

  return (
    <div className="modal-backdrop" onClick={() => !pending && onCancel()}>
      <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="project-confirmation-title" onClick={(event) => event.stopPropagation()}>
        <div className={`confirmation-icon ${deleting ? 'danger' : reopening ? 'reopen' : 'complete'}`}>
          {deleting ? <AlertTriangle size={22} /> : reopening ? <RefreshCw size={22} /> : <CheckCircle2 size={22} />}
        </div>
        <div className="confirmation-copy">
          <span className="kicker">{deleting ? 'Record Retention' : 'Project Status'}</span>
          <h2 id="project-confirmation-title">{deleting ? 'Archive this project?' : reopening ? 'Make this project active?' : 'Complete this project?'}</h2>
          <p>
            {deleting
              ? <><strong>{projectName}</strong> will be removed from project views but retained with all operations and activity history. An administrator can restore it from Settings.</>
              : reopening
                ? <><strong>{projectName}</strong> will return to the active project queue. Its final operation will reopen at 0% so scheduling work can continue.</>
              : <><strong>{projectName}</strong> will move to Past Projects and every operation will be marked 100% complete.</>}
          </p>
        </div>
        <div className="modal-actions confirmation-actions">
          <button className="button ghost" type="button" onClick={onCancel} disabled={pending}>Cancel</button>
          <button className={`button ${deleting ? 'danger-solid' : reopening ? 'primary' : 'complete-solid'}`} type="button" onClick={onConfirm} disabled={pending} autoFocus>
            {deleting ? <Trash2 size={15} /> : reopening ? <RefreshCw size={15} /> : <CheckCircle2 size={15} />}
            {pending ? 'Working...' : deleting ? 'Archive Project' : reopening ? 'Make Active' : 'Complete Project'}
          </button>
        </div>
      </section>
    </div>
  )
}

export function UnsavedProjectDetailsDialog({
  projectName,
  saving,
  onContinueEditing,
  onDiscard,
  onSave,
}: {
  projectName: string
  saving: boolean
  onContinueEditing: () => void
  onDiscard: () => void
  onSave: () => void
}) {
  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !saving) onContinueEditing()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onContinueEditing, saving])

  return (
    <div className="modal-backdrop" onClick={() => !saving && onContinueEditing()}>
      <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="unsaved-project-details-title" onClick={(event) => event.stopPropagation()}>
        <div className="confirmation-icon unsaved"><AlertTriangle size={22} /></div>
        <div className="confirmation-copy">
          <span className="kicker">Unsaved Project Details</span>
          <h2 id="unsaved-project-details-title">Save before leaving edit mode?</h2>
          <p>
            The contact lead, engineer, customer, or sales order for <strong>{projectName}</strong> has changed. Operation-grid edits save automatically, but these project details still need to be saved.
          </p>
        </div>
        <div className="modal-actions confirmation-actions unsaved-detail-actions">
          <button className="button ghost" type="button" onClick={onContinueEditing} disabled={saving}>Continue Editing</button>
          <button className="button danger" type="button" onClick={onDiscard} disabled={saving}>Discard &amp; Done</button>
          <button className="button primary" type="button" onClick={onSave} disabled={saving} autoFocus>
            <Save size={15} /> {saving ? 'Saving...' : 'Save & Done'}
          </button>
        </div>
      </section>
    </div>
  )
}

export function ConcurrencyConflictDialog({
  conflict,
  onCancel,
  onReload,
}: {
  conflict: ConcurrencyConflict
  onCancel: () => void
  onReload: () => Promise<void>
}) {
  const [reloading, setReloading] = useState(false)

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !reloading) onCancel()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onCancel, reloading])

  const reload = async () => {
    setReloading(true)
    try {
      await onReload()
    } finally {
      setReloading(false)
    }
  }

  return (
    <div className="modal-backdrop" onClick={() => !reloading && onCancel()}>
      <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="concurrency-conflict-title" onClick={(event) => event.stopPropagation()}>
        <div className="confirmation-icon conflict"><RefreshCw size={22} /></div>
        <div className="confirmation-copy">
          <span className="kicker">Newer Version Available</span>
          <h2 id="concurrency-conflict-title">Review the latest changes</h2>
          <p>{conflict.message} Your attempted change was not saved, so no one else's work was overwritten.</p>
        </div>
        <div className="modal-actions confirmation-actions">
          <button className="button ghost" type="button" onClick={onCancel} disabled={reloading}>Cancel</button>
          <button className="button primary" type="button" onClick={() => void reload()} disabled={reloading} autoFocus>
            <RefreshCw size={15} /> {reloading ? 'Reloading...' : 'Reload Latest'}
          </button>
        </div>
      </section>
    </div>
  )
}

export function ProjectChatDrawer({
  project,
  currentUser,
  onClose,
}: {
  project: ProjectDetail
  currentUser: User
  onClose: () => void
}) {
  const [messages, setMessages] = useState<ProjectMessage[]>([])
  const [users, setUsers] = useState<MentionableUser[]>([])
  const [draft, setDraft] = useState('')
  const [loading, setLoading] = useState(true)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const latestMessageIdRef = useRef(0)
  const messageListRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let mounted = true

    const mergeMessages = (incoming: ProjectMessage[], replace = false) => {
      if (!mounted) return
      setMessages((current) => {
        const next = replace ? incoming : [...current, ...incoming.filter((message) => !current.some((item) => item.id === message.id))]
        latestMessageIdRef.current = next.at(-1)?.id ?? 0
        return next
      })
    }

    const loadInitial = async () => {
      setLoading(true)
      setError(null)
      try {
        const [initialMessages, mentionUsers] = await Promise.all([
          api<ProjectMessage[]>(`/api/projects/${project.id}/messages`),
          api<MentionableUser[]>('/api/users/mentions'),
        ])
        mergeMessages(initialMessages, true)
        if (mounted) setUsers(mentionUsers)
      } catch (err) {
        if (mounted) setError(err instanceof Error ? err.message : 'Unable to load project chat.')
      } finally {
        if (mounted) setLoading(false)
      }
    }

    const poll = async () => {
      try {
        const incoming = await api<ProjectMessage[]>(`/api/projects/${project.id}/messages?afterId=${latestMessageIdRef.current}`)
        mergeMessages(incoming)
      } catch {
        // The next poll retries quietly; sending errors remain explicit.
      }
    }

    void loadInitial()
    const interval = window.setInterval(() => void poll(), 8000)
    return () => {
      mounted = false
      window.clearInterval(interval)
    }
  }, [project.id])

  useEffect(() => {
    messageListRef.current?.scrollTo({ top: messageListRef.current.scrollHeight, behavior: loading ? 'auto' : 'smooth' })
  }, [loading, messages])

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  const mentionMatch = draft.match(/(^|\s)@([A-Za-z0-9._-]*)$/)
  const mentionQuery = mentionMatch?.[2].toLowerCase() ?? ''
  const mentionSuggestions = mentionMatch
    ? users
      .filter((user) =>
        user.mentionHandle.toLowerCase().includes(mentionQuery) ||
        user.displayName.toLowerCase().includes(mentionQuery))
      .slice(0, 5)
    : []

  const insertMention = (user: MentionableUser) => {
    if (!mentionMatch) return
    const beforeMention = draft.slice(0, draft.length - mentionMatch[0].length)
    setDraft(`${beforeMention}${mentionMatch[1]}@${user.mentionHandle} `)
  }

  const sendMessage = async () => {
    const body = draft.trim()
    if (!body || sending) return
    setSending(true)
    setError(null)
    try {
      const message = await api<ProjectMessage>(`/api/projects/${project.id}/messages`, {
        method: 'POST',
        body: JSON.stringify({ body }),
      })
      setMessages((current) => current.some((item) => item.id === message.id) ? current : [...current, message])
      latestMessageIdRef.current = message.id
      setDraft('')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to send the message.')
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="chat-backdrop" onClick={onClose}>
      <aside className="project-chat" role="dialog" aria-modal="true" aria-labelledby="project-chat-title" onClick={(event) => event.stopPropagation()}>
        <header className="chat-head">
          <div>
            <span className="kicker">Project Communication</span>
            <h2 id="project-chat-title">Project Chat</h2>
            <p>{project.programName}</p>
          </div>
          <button className="icon-button" type="button" onClick={onClose} aria-label="Close project chat"><X size={17} /></button>
        </header>

        <div className="chat-messages" ref={messageListRef} aria-live="polite">
          {loading ? (
            <div className="chat-loading" aria-label="Loading messages">
              <SkeletonLine width="58%" /><SkeletonLine width="76%" /><SkeletonLine width="48%" />
            </div>
          ) : messages.length === 0 ? (
            <div className="chat-empty">
              <MessageSquare size={21} />
              <strong>No messages yet</strong>
              <span>Start the project conversation below.</span>
            </div>
          ) : messages.map((message) => {
            const own = message.authorAccountName.toLowerCase() === currentUser.accountName.toLowerCase()
            return (
              <article className={`chat-message ${own ? 'own' : ''}`} key={message.id}>
                <div className="chat-avatar" aria-hidden="true">{userInitials(message.authorDisplayName)}</div>
                <div className="chat-message-content">
                  <header><strong>{message.authorDisplayName}</strong><time dateTime={message.createdAt}>{formatChatTime(message.createdAt)}</time></header>
                  <p>{renderChatMessage(message.body)}</p>
                </div>
              </article>
            )
          })}
        </div>

        <footer className="chat-composer">
          {error && <div className="chat-error" role="alert"><AlertTriangle size={14} /><span>{error}</span></div>}
          <div className="chat-input-wrap">
            {mentionSuggestions.length > 0 && (
              <div className="mention-menu" role="listbox" aria-label="Mention a user">
                {mentionSuggestions.map((mentionUser) => (
                  <button type="button" role="option" aria-selected="false" key={mentionUser.accountName} onClick={() => insertMention(mentionUser)}>
                    <span className="chat-avatar small">{userInitials(mentionUser.displayName)}</span>
                    <span><strong>{mentionUser.displayName}</strong><small>@{mentionUser.mentionHandle}</small></span>
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
                if (mentionSuggestions.length > 0) insertMention(mentionSuggestions[0])
                else void sendMessage()
              }}
              placeholder="Write a message... Use @ to tag someone"
              aria-label="Project message"
              rows={3}
            />
            <div className="chat-composer-meta">
              <span><AtSign size={13} /> Tag users with @</span>
              <span>{draft.length}/2000</span>
            </div>
          </div>
          <button className="button primary chat-send" type="button" onClick={() => void sendMessage()} disabled={!draft.trim() || sending}>
            <Send size={15} /> {sending ? 'Sending' : 'Send'}
          </button>
        </footer>
      </aside>
    </div>
  )
}

export function ProjectActivityDrawer({ project, onClose }: { project: ProjectDetail; onClose: () => void }) {
  const [entries, setEntries] = useState<ProjectAuditEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  async function loadActivity() {
    setLoading(true)
    setError(null)
    try {
      setEntries(await api<ProjectAuditEntry[]>(`/api/projects/${project.id}/activity`))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load project activity.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadActivity()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  return (
    <div className="chat-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose()
    }}>
      <aside className="project-chat activity-drawer" role="dialog" aria-modal="true" aria-label="Project Activity Log">
        <header className="chat-head">
          <div>
            <span className="kicker">Project History</span>
            <h2>Activity Log</h2>
            <p>{project.programName}</p>
          </div>
          <div className="drawer-head-actions">
            <button className="icon-button" type="button" onClick={() => void loadActivity()} aria-label="Refresh activity log" title="Refresh activity log"><RefreshCw size={16} /></button>
            <button className="icon-button" type="button" onClick={onClose} aria-label="Close activity log"><X size={17} /></button>
          </div>
        </header>

        <div className="activity-list" aria-live="polite">
          {loading ? (
            <div className="chat-loading" aria-label="Loading activity log">
              {[0, 1, 2, 3].map((item) => <div className="skeleton-line" style={{ height: 92 }} key={item} />)}
            </div>
          ) : error ? (
            <div className="chat-empty">
              <AlertTriangle size={22} />
              <strong>Activity unavailable</strong>
              <span>{error}</span>
              <button className="button ghost" type="button" onClick={() => void loadActivity()}><RefreshCw size={14} /> Retry</button>
            </div>
          ) : entries.length === 0 ? (
            <div className="chat-empty">
              <History size={23} />
              <strong>No recorded activity yet</strong>
              <span>Future project and operation changes will appear here.</span>
            </div>
          ) : entries.map((entry) => (
            <article className="activity-entry" key={entry.id}>
              <div className={`activity-marker action-${activityActionClass(entry.action)}`} aria-hidden="true">
                {activityActionIcon(entry.action)}
              </div>
              <div className="activity-entry-body">
                <header>
                  <strong>{entry.summary}</strong>
                  <time dateTime={entry.changedAt}>{formatActivityTime(entry.changedAt)}</time>
                </header>
                <p className="activity-actor">{entry.changedByDisplayName}</p>
                {entry.changes.length > 0 && (
                  <div className="activity-changes">
                    {entry.changes.map((change, index) => (
                      <div className="activity-change" key={`${entry.id}-${change.field}-${index}`}>
                        <span>{change.field}</span>
                        <div>
                          <del>{change.oldValue || '—'}</del>
                          <ChevronRight size={11} />
                          <ins>{change.newValue || '—'}</ins>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </article>
          ))}
        </div>
      </aside>
    </div>
  )
}
