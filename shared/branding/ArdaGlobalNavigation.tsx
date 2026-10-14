import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Bell, Calculator, ClipboardCheck, DraftingCompass, FolderKanban, House, ShieldCheck, X } from 'lucide-react'

/** Entries returned by the permission-filtered Portal /api/apps endpoint. */
export interface ArdaNavigationApplication {
  id: string
  name: string
  url: string
  status: string
}

interface ArdaGlobalNavigationProps {
  currentApplicationId: string
  hubUrl: string
  /** Supply an already permission-filtered catalog, or omit to load it from Portal. */
  applications?: ArdaNavigationApplication[]
  /** The current app's own permission-filtered page navigation. */
  currentPages?: ReactNode
  /** Keep links to other applications inert during guided or access previews. */
  disabled?: boolean
}

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

const liveApplicationIds = new Set([
  'project-tracker', 'engineering-hub', 'estimating-dashboard',
  'quality-assurance', 'admin-console',
])

const moduleNames: Record<string, string> = {
  'project-tracker': 'Project Tracker', 'engineering-hub': 'Engineering Hub',
  'estimating-dashboard': 'Estimating', 'quality-assurance': 'Quality Assurance',
  'admin-console': 'Administration',
}

/** Only pages with a stable, addressable URL belong in the cross-app menu. */
const modulePages: Record<string, { label: string; path: string }[]> = {
  'project-tracker': [
    { label: 'Dashboard', path: '/#/dashboard' },
    { label: 'Project Detail', path: '/#/project' },
    { label: 'Calendar', path: '/#/calendar' },
    { label: 'Past Projects', path: '/#/past-projects' },
  ],
  'engineering-hub': [
    { label: 'Dashboard', path: '/#dashboard' },
    { label: 'Drawing Control', path: '/#drawing-dashboard' },
    { label: 'Tooling Management', path: '/#tooling-dashboard' },
    { label: 'Compound Test Data', path: '/#compound-dashboard' },
  ],
  'estimating-dashboard': [
    { label: 'Quotes Dashboard', path: '/#/quotes' },
    { label: 'Quote Status', path: '/#/quote-status' },
    { label: 'Estimate Calculator', path: '/#/calculator' },
    { label: 'Estimating Logs', path: '/#/history' },
    { label: 'Rates Reference', path: '/#/rates' },
    { label: 'Operation Rules', path: '/#/operation-rules' },
  ],
  'quality-assurance': [
    { label: 'Dashboard', path: '/#/dashboard' },
    { label: 'Shipping Status', path: '/#/shipping-status' },
  ],
  'admin-console': [
    { label: 'Access', path: '/#/admin/access' },
    { label: 'People', path: '/#/admin/people' },
    { label: 'RAID Log', path: '/#/admin/raid-log/board' },
  ],
}

function safeHttpUrl(value: string, base?: string): string | null {
  try {
    const url = new URL(value, base)
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null
  } catch {
    return null
  }
}

function moduleIcon(id: string) {
  switch (id) {
    case 'project-tracker': return <FolderKanban size={17} aria-hidden="true" />
    case 'engineering-hub': return <DraftingCompass size={17} aria-hidden="true" />
    case 'estimating-dashboard': return <Calculator size={17} aria-hidden="true" />
    case 'quality-assurance': return <ClipboardCheck size={17} aria-hidden="true" />
    default: return <ShieldCheck size={17} aria-hidden="true" />
  }
}

export default function ArdaGlobalNavigation({
  currentApplicationId, hubUrl, applications, currentPages, disabled = false,
}: ArdaGlobalNavigationProps) {
  const [fetchedApplications, setFetchedApplications] = useState<ArdaNavigationApplication[] | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
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
  const [expandedApplicationId, setExpandedApplicationId] = useState<string | null>(
    currentApplicationId === 'portal' ? null : currentApplicationId,
  )
  const safeHubUrl = safeHttpUrl(hubUrl)

  const loadInbox = useCallback(async (active: () => boolean = () => true) => {
    if (!safeHubUrl || disabled) return
    try {
      const response = await fetch(new URL('/api/push/inbox', safeHubUrl).href, {
        credentials: 'include',
      })
      if (!response.ok) throw new Error(`Arda inbox responded ${response.status}`)
      const next = parseInbox(await response.json() as unknown)
      if (!active()) return
      if (hasLoadedInboxRef.current && currentApplicationId !== 'project-tracker') {
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
      if (!active()) return
      setInboxLoading(false)
      setInboxError(true)
    }
  }, [currentApplicationId, disabled, safeHubUrl])

  async function markRead(item: ArdaInboxItem) {
    if (!safeHubUrl || readPendingId) return
    setReadPendingId(item.id)
    try {
      const response = await fetch(new URL(`/api/push/inbox/${encodeURIComponent(item.id)}/read`, safeHubUrl).href, {
        method: 'POST', credentials: 'include',
      })
      if (!response.ok) throw new Error(`Arda inbox responded ${response.status}`)
      setInbox((current) => current.map((row) => row.id === item.id ? { ...row, readAt: new Date().toISOString() } : row))
      setUnreadCount((count) => Math.max(0, count - 1))
      setToasts((current) => current.filter((row) => row.id !== item.id))
      setInboxError(false)
    } catch {
      setInboxError(true)
    } finally {
      setReadPendingId(null)
    }
  }

  useEffect(() => {
    setExpandedApplicationId(currentApplicationId === 'portal' ? null : currentApplicationId)
  }, [currentApplicationId])

  useEffect(() => {
    if (applications !== undefined || !safeHubUrl || disabled) return
    const controller = new AbortController()
    setFetchedApplications(null)
    setLoadFailed(false)
    const endpoint = new URL('/api/apps', safeHubUrl).href
    void fetch(endpoint, { credentials: 'include', signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`Arda catalog responded ${response.status}`)
        const data: unknown = await response.json()
        if (!Array.isArray(data)) throw new Error('Arda catalog response is invalid')
        setFetchedApplications(data as ArdaNavigationApplication[])
      })
      .catch(() => {
        if (!controller.signal.aborted) setLoadFailed(true)
      })
    return () => controller.abort()
  }, [applications, disabled, safeHubUrl])

  useEffect(() => {
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
    return () => {
      active = false
      window.clearInterval(interval)
      document.removeEventListener('visibilitychange', resume)
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

  const catalog = applications ?? (disabled ? null : fetchedApplications)
  const visibleApplications = (catalog ?? []).flatMap((application) => {
    if (!application || typeof application.id !== 'string'
      || typeof application.name !== 'string'
      || typeof application.url !== 'string'
      || typeof application.status !== 'string'
      || !liveApplicationIds.has(application.id)
      || application.status.toLowerCase() !== 'active') return []
    const url = safeHttpUrl(application.url, safeHubUrl ?? undefined)
    return url ? [{ ...application, url }] : []
  })

  // Keep the open app's own pages usable if the Portal catalog cannot load.
  const currentVisible = visibleApplications.some((application) => application.id === currentApplicationId)
  const currentUrl = typeof window === 'undefined' ? null : safeHttpUrl(window.location.origin)
  if (currentPages && liveApplicationIds.has(currentApplicationId) && !currentVisible && currentUrl) {
    visibleApplications.unshift({
      id: currentApplicationId, name: moduleNames[currentApplicationId],
      url: currentUrl, status: 'Active',
    })
  }

  return <section className="arda-global-navigation" aria-label="Arda navigation">
    <nav aria-label="Arda applications">
      {safeHubUrl && (disabled
        ? <span className="arda-global-navigation__home arda-global-navigation__home--disabled" aria-disabled="true" title="Home"><House size={17} aria-hidden="true" />Home</span>
        : <a className={`arda-global-navigation__home${currentApplicationId === 'portal' ? ' arda-global-navigation__home--current' : ''}`} href={safeHubUrl} target="_top" aria-current={currentApplicationId === 'portal' ? 'page' : undefined} title="Home"><House size={17} aria-hidden="true" />Home</a>)}
      {visibleApplications.map((application) => {
        const current = application.id === currentApplicationId
        const expanded = expandedApplicationId === application.id
        const panelId = `arda-module-pages-${application.id}`
        return <div className={`arda-global-navigation__module${current ? ' arda-global-navigation__module--current' : ''}`} key={application.id}>
          <button type="button" className="arda-global-navigation__module-toggle"
            aria-expanded={expanded} aria-controls={panelId} title={application.name}
            onClick={() => setExpandedApplicationId(expanded ? null : application.id)}>
            <span className="arda-global-navigation__module-mark" aria-hidden="true">{moduleIcon(application.id)}</span>
            <span className="arda-global-navigation__module-name">{application.name}</span>
            <span className="arda-global-navigation__chevron" aria-hidden="true" />
          </button>
          <div className="arda-global-navigation__panel" id={panelId} hidden={!expanded}>
            {current && currentPages ? currentPages : modulePages[application.id]?.map((page) => {
              const href = safeHttpUrl(page.path, application.url)
              return href && (disabled
                ? <span className="arda-global-navigation__page arda-global-navigation__page--disabled" aria-disabled="true" key={page.path}>{page.label}</span>
                : <a className="arda-global-navigation__page" href={href} target="_top" key={page.path}>{page.label}</a>)
            })}
          </div>
        </div>
      })}
      {applications === undefined && !disabled && !catalog && <span className="arda-global-navigation__status" role="status">
        {loadFailed ? 'Other areas unavailable' : 'Loading other areas…'}
      </span>}
    </nav>
    {inboxHost && !disabled && safeHubUrl && createPortal(
      <button ref={inboxButtonRef} type="button"
        className="arda-global-navigation__inbox-trigger" aria-label={`Arda notifications${unreadCount ? `, ${unreadCount} unread` : ''}`}
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
          <button type="button" aria-label="Close notifications" onClick={() => {
            setInboxOpen(false)
            inboxButtonRef.current?.focus()
          }}><X size={18} aria-hidden="true" /></button>
        </header>
        {inboxError && <div className="arda-global-inbox__error" role="status">
          Could not refresh notifications.
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
                {!item.readAt && <button type="button" className="arda-global-inbox__mark-read"
                  disabled={readPendingId !== null}
                  onClick={() => void markRead(item)}>Mark read</button>}
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
  </section>
}
