import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Bell, X } from 'lucide-react'
import './arda-interactions.css'

interface ArdaInboxItem {
  id: string
  sourceModule: string
  title: string
  body: string
  createdAt: string
  readAt: string | null
}

function parseInbox(value: unknown): { items: ArdaInboxItem[]; unreadCount: number } {
  if (!value || typeof value !== 'object') throw new Error('Invalid notification response')
  const payload = value as { items?: unknown; unreadCount?: unknown }
  if (!Array.isArray(payload.items) || typeof payload.unreadCount !== 'number'
    || !Number.isFinite(payload.unreadCount))
    throw new Error('Invalid notification response')
  const items = payload.items.flatMap((item: unknown) => {
    if (!item || typeof item !== 'object') return []
    const row = item as Partial<ArdaInboxItem>
    if (typeof row.id !== 'string' || typeof row.sourceModule !== 'string'
      || typeof row.title !== 'string' || typeof row.body !== 'string'
      || typeof row.createdAt !== 'string'
      || (row.readAt !== null && typeof row.readAt !== 'string')) return []
    return [row as ArdaInboxItem]
  })
  return { items, unreadCount: Math.max(0, Math.trunc(payload.unreadCount)) }
}

function inboxItemUrl(hubUrl: string, itemId: string) {
  return new URL(`/api/push/inbox/${encodeURIComponent(itemId)}/open`, hubUrl).href
}

function sourceLabel(sourceModule: string) {
  return moduleNames[sourceModule] ?? sourceModule.replaceAll('-', ' ')
}

function createdLabel(createdAt: string) {
  const date = new Date(createdAt)
  return Number.isNaN(date.valueOf()) ? '' : date.toLocaleString()
}

const moduleNames: Record<string, string> = {
  'project-tracker': 'Project Tracker', 'engineering-hub': 'Engineering Hub',
  'estimating-dashboard': 'Estimating', 'quality-assurance': 'Quality Assurance',
  'small-business-subcontracting': 'Small Business Subcontracting',
  'admin-console': 'Administration',
}

export default function ArdaNotificationInbox({ currentApplicationId, safeHubUrl, disabled,
  renderItemActions,
}: { currentApplicationId: string; safeHubUrl: string | null; disabled: boolean; renderItemActions?: (id: string) => ReactNode }) {
  const [inbox, setInbox] = useState<ArdaInboxItem[]>([])
  const [unreadCount, setUnreadCount] = useState(0)
  const [inboxLoading, setInboxLoading] = useState(true)
  const [inboxError, setInboxError] = useState(false)
  const [inboxOpen, setInboxOpen] = useState(false)
  const [inboxPosition, setInboxPosition] = useState({ top: 8, left: 8 })
  const [readPendingId, setReadPendingId] = useState<string | null>(null)
  const [toasts, setToasts] = useState<ArdaInboxItem[]>([])
  const [inboxHost, setInboxHost] = useState<HTMLElement | null>(null)
  const inboxButtonRef = useRef<HTMLButtonElement>(null)
  const inboxPanelRef = useRef<HTMLElement>(null)
  const knownInboxIdsRef = useRef(new Set<string>())
  const hasLoadedInboxRef = useRef(false)
  const requestVersionRef = useRef(0)
  const mutationVersionRef = useRef<number | null>(null)
  const loadInbox = useCallback(async (active: () => boolean = () => true) => {
    if (!safeHubUrl || disabled || mutationVersionRef.current !== null) return
    const version = ++requestVersionRef.current
    try {
      const response = await fetch(new URL('/api/push/inbox', safeHubUrl).href, {
        credentials: 'include',
      })
      if (!response.ok) throw new Error(`Arda inbox responded ${response.status}`)
      const next = parseInbox(await response.json() as unknown)
      if (!active() || version !== requestVersionRef.current) return
      if (hasLoadedInboxRef.current) {
        const fresh = next.items.filter((item) => item.id.startsWith('project-tracker:')
          && !item.readAt && !knownInboxIdsRef.current.has(item.id))
        if (fresh.length) setToasts((existing) => [
          ...fresh.filter((item) => !existing.some((toast) => toast.id === item.id)),
          ...existing,
        ].slice(0, 3))
      }
      next.items.forEach((item) => knownInboxIdsRef.current.add(item.id))
      hasLoadedInboxRef.current = true
      setInbox(next.items)
      setUnreadCount(next.unreadCount)
      setInboxLoading(false)
      setInboxError(false)
    } catch {
      if (!active() || version !== requestVersionRef.current) return
      setInboxLoading(false)
      setInboxError(true)
    }
  }, [currentApplicationId, disabled, safeHubUrl])

  async function dismiss(item?: ArdaInboxItem) {
    if (!safeHubUrl || disabled || mutationVersionRef.current !== null) return
    const version = ++requestVersionRef.current
    mutationVersionRef.current = version
    setReadPendingId(item?.id ?? '*')
    try {
      const path = item ? `/api/push/inbox/${encodeURIComponent(item.id)}/dismiss` : '/api/push/inbox/clear'
      const response = await fetch(new URL(path, safeHubUrl).href, {
        method: 'POST', credentials: 'include',
      })
      if (!response.ok) throw new Error(`Arda inbox responded ${response.status}`)
      if (mutationVersionRef.current !== version || requestVersionRef.current !== version) return
      setInbox((current) => item ? current.filter((row) => row.id !== item.id) : [])
      setToasts((current) => item ? current.filter((row) => row.id !== item.id) : [])
      setUnreadCount((current) => item ? Math.max(0, current - (item.readAt ? 0 : 1)) : 0)
      mutationVersionRef.current = null
      setReadPendingId(null)
      await loadInbox()
    } catch {
      if (mutationVersionRef.current === version) setInboxError(true)
    } finally {
      if (mutationVersionRef.current === version) {
        mutationVersionRef.current = null
        setReadPendingId(null)
      }
    }
  }

  useEffect(() => {
    setReadPendingId(null)
    if (!safeHubUrl || disabled) {
      setInboxOpen(false)
      setToasts([])
      setInbox([])
      setUnreadCount(0)
      return
    }
    let active = true
    knownInboxIdsRef.current.clear()
    hasLoadedInboxRef.current = false
    setInboxLoading(true)
    void loadInbox(() => active)
    const interval = window.setInterval(() => {
      if (document.visibilityState === 'visible') void loadInbox(() => active)
    }, 15_000)
    const resume = () => {
      if (document.visibilityState === 'visible') void loadInbox(() => active)
    }
    document.addEventListener('visibilitychange', resume)
    window.addEventListener('arda:inbox-updated', resume)
    return () => {
      active = false
      requestVersionRef.current++
      mutationVersionRef.current = null
      window.clearInterval(interval)
      document.removeEventListener('visibilitychange', resume)
      window.removeEventListener('arda:inbox-updated', resume)
    }
  }, [currentApplicationId, disabled, loadInbox, safeHubUrl])

  useEffect(() => {
    if (!safeHubUrl || disabled) return
    let host: HTMLElement | null = null
    const attachToThemeSwitch = () => {
      const themeSwitch = document.querySelector<HTMLElement>('.portal-top .theme-switch, .topbar .theme-switch')
      if (!themeSwitch?.parentElement) return
      if (!host) {
        host = document.createElement('span')
        host.className = 'arda-global-navigation__inbox-slot'
        setInboxHost(host)
      }
      if (host.parentElement !== themeSwitch.parentElement || host.nextElementSibling !== themeSwitch) {
        themeSwitch.parentElement.insertBefore(host, themeSwitch)
      }
    }
    attachToThemeSwitch()
    const observer = new MutationObserver(attachToThemeSwitch)
    observer.observe(document.body, { childList: true, subtree: true })
    return () => {
      observer.disconnect()
      host?.remove()
      setInboxHost(null)
    }
  }, [disabled, safeHubUrl])

  useEffect(() => {
    if (!inboxOpen) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setInboxOpen(false)
        inboxButtonRef.current?.focus()
      }
    }
    const closeOutside = (event: PointerEvent) => {
      if (event.target instanceof Node
        && !inboxButtonRef.current?.contains(event.target)
        && !inboxPanelRef.current?.contains(event.target)) setInboxOpen(false)
    }
    document.addEventListener('keydown', closeOnEscape)
    document.addEventListener('pointerdown', closeOutside)
    return () => {
      document.removeEventListener('keydown', closeOnEscape)
      document.removeEventListener('pointerdown', closeOutside)
    }
  }, [inboxOpen])

  function toggleInbox() {
    if (!inboxOpen && inboxButtonRef.current) {
      const box = inboxButtonRef.current.getBoundingClientRect()
      setInboxPosition({
        top: Math.max(8, Math.min(box.bottom + 8, window.innerHeight - 520)),
        left: Math.max(8, Math.min(box.left, window.innerWidth - 368)),
      })
      if (!inboxLoading) void loadInbox()
    }
    setInboxOpen(!inboxOpen)
  }

  return <>
    {inboxHost && !disabled && safeHubUrl && createPortal(
      <button ref={inboxButtonRef} type="button"
        className={`arda-global-navigation__inbox-trigger${unreadCount ? ' has-unread' : ''}`} data-benny-target="notifications" aria-label={`Arda notifications${unreadCount ? `, ${unreadCount} unread` : ''}`}
        aria-expanded={inboxOpen} aria-controls={inboxOpen ? 'arda-global-inbox' : undefined} title="Arda notifications"
        onClick={toggleInbox}>
        <Bell size={17} aria-hidden="true" />
        {unreadCount > 0 && <span className="arda-global-navigation__inbox-count">{unreadCount > 99 ? '99+' : unreadCount}</span>}
      </button>, inboxHost,
    )}
    {inboxOpen && safeHubUrl && typeof document !== 'undefined' && createPortal(
      <section ref={inboxPanelRef} className="arda-global-inbox" id="arda-global-inbox"
        role="dialog" aria-label="Arda notifications"
        style={{ top: inboxPosition.top, left: inboxPosition.left }}>
        <header className="arda-global-inbox__header">
          <div><strong>Notifications</strong><span>Across Arda</span></div>
          {inbox.length > 0 && <button type="button" className="arda-global-inbox__clear"
            disabled={readPendingId !== null} onClick={() => void dismiss()}>Clear all</button>}
          <button type="button" aria-label="Close notifications" onClick={() => {
            setInboxOpen(false)
            inboxButtonRef.current?.focus()
          }}><X size={18} aria-hidden="true" /></button>
        </header>
        {inboxError && <div className="arda-global-inbox__error" role="status">
          Notifications could not be updated. Please try again.
          <button type="button" onClick={() => void loadInbox()}>Retry</button>
        </div>}
        {inboxLoading ? <p className="arda-global-inbox__empty" role="status">Loading notifications…</p>
          : inbox.length === 0 ? (inboxError ? null : <p className="arda-global-inbox__empty">You’re all caught up.</p>)
            : <ol className="arda-global-inbox__list">
              {inbox.map((item) => <li key={item.id} className={item.readAt ? '' : 'is-unread'}>
                <a href={inboxItemUrl(safeHubUrl, item.id)} target="_top">
                  <span className="arda-global-inbox__item-top">
                    <strong>{item.title}</strong>
                    {!item.readAt && <span className="arda-global-inbox__unread-dot" aria-label="Unread" />}
                  </span>
                  <span className="arda-global-inbox__item-body">{item.body}</span>
                  <span className="arda-global-inbox__item-meta">
                    {sourceLabel(item.sourceModule)}
                    <time dateTime={item.createdAt}>{createdLabel(item.createdAt)}</time>
                  </span>
                </a>
                <button type="button" className="arda-global-inbox__dismiss"
                  aria-label={`Remove ${item.title}`} title="Remove notification" disabled={readPendingId !== null}
                  onClick={() => void dismiss(item)}><X size={16} aria-hidden="true" /></button>
                {renderItemActions?.(item.id)}
              </li>)}
            </ol>}
      </section>, document.body,
    )}
    {toasts.length > 0 && safeHubUrl && typeof document !== 'undefined' && createPortal(
      <aside className="arda-global-inbox-toasts" aria-label="New Arda notifications" aria-live="polite">
        {toasts.map((item) => <article key={item.id} className="arda-global-inbox-toast">
          <a href={inboxItemUrl(safeHubUrl, item.id)} target="_top">
            <Bell size={17} aria-hidden="true" />
            <span><strong>{item.title}</strong><small>{item.body}</small><em>{sourceLabel(item.sourceModule)}</em></span>
          </a>
          <button type="button" aria-label={`Dismiss ${item.title}`}
            onClick={() => setToasts((current) => current.filter((row) => row.id !== item.id))}>
            <X size={16} aria-hidden="true" />
          </button>
        </article>)}
      </aside>, document.body,
    )}
  </>
}
