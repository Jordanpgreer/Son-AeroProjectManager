import '../App.css'
import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Archive,
  Bell,
  CalendarRange,
  ChevronDown,
  Check,
  CheckCheck,
  FileSpreadsheet,
  FileText,
  History,
  LayoutDashboard,
  ListChecks,
  MessageSquare,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Settings2,
  StickyNote,
  X,
} from 'lucide-react'
import {
  api,
  formatActivityTime,
  hubUrl,
  screenEyebrow,
  screenTitle,
  screenSubtitle,
  userInitials,
} from '../lib'
import type {
  Screen,
  User,
  ProjectDetail,
  MentionNotification,
} from '../types'
import type { AppTheme } from '../theme'

function hasPermission(user: User | null, permission: string) {
  return Boolean(user?.permissions?.includes(permission))
}

function UserProfile({ user }: { user: User | null }) {
  const accessLabel = user?.isAdmin ? 'Admin' : user?.groups[0] ?? 'User'
  const avatarLabel = user
    ? userInitials(user.displayName).padEnd(2, user.displayName.slice(1, 2).toUpperCase())
    : '...'

  return (
    <div className="topbar-user-chip" aria-live="polite">
      <div className="topbar-user-copy">
        <strong>{user?.displayName ?? 'Checking access'}</strong>
        <span>{user ? accessLabel : 'Loading'}</span>
      </div>
      <span className="topbar-user-avatar" title={user?.accountName}>
        {avatarLabel}
      </span>
    </div>
  )
}

export function Sidebar({
  screen,
  setScreen,
  selectedProject,
  hasActiveProjects,
  onOpenActiveProjects,
  user,
}: {
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  hasActiveProjects: boolean
  onOpenActiveProjects: () => Promise<void>
  user: User | null
}) {
  return (
    <aside className="sidebar">
      <a
        className="brand brand-hub-link"
        href={hubUrl}
        target="_top"
        aria-label="Return to All Applications"
        title="Return to All Applications"
      >
        <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
      </a>

      <div className="nav-section">
        <span className="nav-heading">Program Control</span>
        <nav aria-label="Primary">
          <NavButton active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />
          <NavButton active={screen === 'project' && selectedProject?.status !== 'Complete'} onClick={() => void onOpenActiveProjects()} icon={<ListChecks size={17} />} label="Project Detail" disabled={!hasActiveProjects} />
          <NavButton active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />
          <NavButton active={screen === 'pastProjects' || (screen === 'project' && selectedProject?.status === 'Complete')} onClick={() => setScreen('pastProjects')} icon={<Archive size={17} />} label="Past Projects" />
        </nav>
      </div>

      {user?.isAdmin && (
        <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Secondary">
            <a
              className="nav-button"
              href={`${hubUrl.replace(/\/+$/, '')}/#/admin/project-tracker/access`}
              target="_top"
              aria-label="Hub Admin / Admin settings"
            >
              <span className="nav-icon"><Settings2 size={17} /></span>
              Hub Admin / Admin settings
            </a>
          </nav>
        </div>
      )}
    </aside>
  )
}

export function NavButton({
  active,
  onClick,
  icon,
  label,
  disabled,
}: {
  active: boolean
  onClick: () => void
  icon: ReactNode
  label: string
  disabled?: boolean
}) {
  return (
    <button className={`nav-button ${active ? 'active' : ''}`} onClick={onClick} disabled={disabled}>
      <span className="nav-icon">{icon}</span>
      {label}
    </button>
  )
}

function NotificationsMenu({
  user,
  onOpenNotification,
}: {
  user: User | null
  onOpenNotification: (notification: MentionNotification) => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [notifications, setNotifications] = useState<MentionNotification[]>([])
  const [toasts, setToasts] = useState<MentionNotification[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)
  const knownNotificationIdsRef = useRef<Set<number>>(new Set())
  const notificationsInitializedRef = useRef(false)

  const dismissToast = (notificationId: number) => {
    setToasts((current) => current.filter((notification) => notification.id !== notificationId))
  }

  const loadNotifications = async (showLoading = false) => {
    if (!user?.isRegistered) return
    if (showLoading) setLoading(true)
    try {
      const next = await api<MentionNotification[]>('/api/notifications')
      if (notificationsInitializedRef.current) {
        const arrivals = next.filter((notification) =>
          !notification.readAt && !knownNotificationIdsRef.current.has(notification.id))
        if (arrivals.length) {
          setToasts((current) => [
            ...arrivals,
            ...current.filter((notification) => !arrivals.some((arrival) => arrival.id === notification.id)),
          ].slice(0, 3))
        }
      }
      knownNotificationIdsRef.current = new Set(next.map((notification) => notification.id))
      notificationsInitializedRef.current = true
      setNotifications(next)
      setError(null)
    } catch {
      setNotifications([])
      setError('Notifications could not be refreshed. Please try again.')
    } finally {
      if (showLoading) setLoading(false)
    }
  }

  useEffect(() => {
    setNotifications([])
    setToasts([])
    setError(null)
    setLoading(false)
    knownNotificationIdsRef.current = new Set()
    notificationsInitializedRef.current = false
    if (!user?.isRegistered) return
    void loadNotifications()
    const interval = window.setInterval(() => void loadNotifications(), 10_000)
    return () => window.clearInterval(interval)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user?.accountName, user?.isRegistered])

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  const unreadCount = notifications.filter((notification) => !notification.readAt).length

  const openNotification = async (notification: MentionNotification) => {
    dismissToast(notification.id)
    if (!notification.readAt && !user?.preview?.readOnly) {
      const readAt = new Date().toISOString()
      setNotifications((current) => current.map((item) => item.id === notification.id ? { ...item, readAt } : item))
      try {
        await api<void>(`/api/notifications/${notification.id}/read`, { method: 'POST' })
      } catch {
        void loadNotifications()
      }
    }
    setOpen(false)
    if (notification.projectId) await onOpenNotification(notification)
  }

  const markAllRead = async () => {
    const readAt = new Date().toISOString()
    setNotifications((current) => current.map((notification) => ({ ...notification, readAt: notification.readAt ?? readAt })))
    try {
      await api<void>('/api/notifications/read-all', { method: 'POST' })
    } catch {
      void loadNotifications(true)
    }
  }

  return (
    <div className="notifications-menu" ref={rootRef}>
      <button
        className={`button ghost notification-trigger ${unreadCount ? 'has-unread' : ''}`}
        type="button"
        onClick={() => {
          setOpen((current) => !current)
          if (!open) void loadNotifications(true)
        }}
        aria-label={`Notifications${unreadCount ? `, ${unreadCount} unread` : ''}`}
        aria-expanded={open}
      >
        <Bell size={16} />
        <span className="notification-label">Notifications</span>
        {unreadCount > 0 && <span className="notification-count">{unreadCount > 99 ? '99+' : unreadCount}</span>}
      </button>
      {open && (
        <section className="notifications-popover" role="dialog" aria-label="Notifications">
          <header>
            <div>
              <span className="kicker">Personal Inbox</span>
              <h2>Notifications</h2>
            </div>
            {unreadCount > 0 && !user?.preview?.readOnly && (
              <button className="notification-read-all" type="button" onClick={() => void markAllRead()}>
                <CheckCheck size={14} /> Mark all read
              </button>
            )}
          </header>
          <div className="notification-list" aria-live="polite">
            {loading ? (
              <div className="notification-state">Loading notifications...</div>
            ) : error ? (
              <div className="notification-state error">
                <strong>Notifications unavailable</strong>
                <span>{error}</span>
                <button className="button ghost" type="button" onClick={() => void loadNotifications(true)}>Retry</button>
              </div>
            ) : notifications.length === 0 ? (
              <div className="notification-state">
                <Bell size={19} />
                <strong>You are all caught up</strong>
                <span>Chat and operation-note mentions will appear here.</span>
              </div>
            ) : notifications.map((notification) => (
              <button
                type="button"
                className={`notification-item ${notification.readAt ? '' : 'unread'}`}
                key={notification.id}
                onClick={() => void openNotification(notification)}
              >
                <span className={`notification-source ${notification.kind === 'OperationNoteMention' ? 'note' : 'chat'}`}>
                  {notification.kind === 'OperationNoteMention' ? <StickyNote size={14} /> : <MessageSquare size={14} />}
                </span>
                <span className="notification-copy">
                  <span>
                    <strong>{notification.actorDisplayName}</strong>
                    {!notification.readAt && <i aria-label="Unread" />}
                  </span>
                  <b>{notification.title || notification.projectName}</b>
                  <span>{notification.bodyPreview}</span>
                  <time dateTime={notification.createdAt}>{formatActivityTime(notification.createdAt)}</time>
                </span>
              </button>
            ))}
          </div>
        </section>
      )}
      {toasts.length > 0 && (
        <aside className="notification-toast-stack" aria-live="polite" aria-label="New notifications">
          {toasts.map((notification) => (
            <NotificationToast
              key={notification.id}
              notification={notification}
              onDismiss={() => dismissToast(notification.id)}
              onOpen={() => void openNotification(notification)}
            />
          ))}
        </aside>
      )}
    </div>
  )
}

function NotificationToast({
  notification,
  onDismiss,
  onOpen,
}: {
  notification: MentionNotification
  onDismiss: () => void
  onOpen: () => void
}) {
  const dismissRef = useRef(onDismiss)
  dismissRef.current = onDismiss
  useEffect(() => {
    const timeout = window.setTimeout(() => dismissRef.current(), 5_000)
    return () => window.clearTimeout(timeout)
  }, [notification.id])

  const destination = notification.kind === 'OperationNoteMention' && notification.operationName
    ? `${notification.operationName} in ${notification.projectName}`
    : `project ${notification.projectName}`

  return (
    <article className="notification-toast">
      <button className="notification-toast-open" type="button" onClick={onOpen}>
        <span className={`notification-source ${notification.kind === 'OperationNoteMention' ? 'note' : 'chat'}`}>
          {notification.kind === 'OperationNoteMention' ? <StickyNote size={16} /> : <MessageSquare size={16} />}
        </span>
        <span>
          <strong>{notification.actorDisplayName} mentioned you</strong>
          <small>{destination}</small>
          {notification.bodyPreview && <span>{notification.bodyPreview}</span>}
        </span>
      </button>
      <button className="notification-toast-dismiss" type="button" onClick={onDismiss} aria-label="Dismiss notification">
        <X size={15} />
      </button>
      <span className="notification-toast-timer" aria-hidden="true" />
    </article>
  )
}

function ThemeSwitch({
  theme,
  onToggleTheme,
}: {
  theme: AppTheme
  onToggleTheme: () => void
}) {
  const dark = theme === 'dark'
  const actionLabel = dark ? 'Switch to light mode' : 'Switch to dark mode'

  return (
    <label className="theme-switch" title={actionLabel}>
      <input
        type="checkbox"
        className="theme-switch__checkbox"
        checked={dark}
        onChange={onToggleTheme}
        aria-label={actionLabel}
      />
      <span className="theme-switch__container" aria-hidden="true">
        <span className="theme-switch__clouds" />
        <span className="theme-switch__stars-container">
          <i />
          <i />
          <i />
          <i />
        </span>
        <span className="theme-switch__circle-container">
          <span className="theme-switch__sun-moon-container">
            <span className="theme-switch__moon">
              <i className="theme-switch__spot" />
              <i className="theme-switch__spot" />
              <i className="theme-switch__spot" />
            </span>
          </span>
        </span>
      </span>
    </label>
  )
}


export function PageHeader({
  theme,
  onToggleTheme,
  screen,
  selectedProject,
  canEnterProjectEdit,
  canCreateProject,
  editMode,
  hasUnsavedChanges,
  onToggleEdit,
  dashboardSearch,
  setDashboardSearch,
  pastProjectsSearch,
  setPastProjectsSearch,
  refresh,
  onAddProject,
  onOpenActivity,
  user,
  onOpenNotification,
}: {
  theme: AppTheme
  onToggleTheme: () => void
  screen: Screen
  selectedProject: ProjectDetail | null
  canEnterProjectEdit: boolean
  canCreateProject: boolean
  editMode: boolean
  hasUnsavedChanges: boolean
  onToggleEdit: () => void
  dashboardSearch: string
  setDashboardSearch: (value: string) => void
  pastProjectsSearch: string
  setPastProjectsSearch: (value: string) => void
  refresh: () => Promise<void>
  onAddProject: () => void
  onOpenActivity: () => void
  user: User | null
  onOpenNotification: (notification: MentionNotification) => Promise<void>
}) {
  const portfolioExports = screen === 'dashboard'
  const pastProjectExports = screen === 'pastProjects'
  const projectId = selectedProject?.id
  const xlsxHref = portfolioExports ? '/api/reports/portfolio.xlsx' : pastProjectExports ? '/api/reports/past-projects.xlsx' : `/api/reports/projects/${projectId}.xlsx`
  const pdfHref = portfolioExports ? '/api/reports/portfolio.pdf' : pastProjectExports ? '/api/reports/past-projects.pdf' : `/api/reports/projects/${projectId}.pdf`
  const showExports = screen === 'dashboard' || screen === 'project' || screen === 'pastProjects'
  const subtitle = screenSubtitle(screen)

  return (
    <header className="topbar">
      <div className="page-title-block">
        <span className="eyebrow">{screenEyebrow(screen)}</span>
        <div className="page-title-row">
          <h1 className={screen === 'project' ? 'technical-id' : undefined}>{screenTitle(screen, selectedProject)}</h1>
          {screen === 'project' && selectedProject && hasPermission(user, 'project.activity.view') && (
            <button className="button ghost page-activity-button" type="button" onClick={onOpenActivity}>
              <History size={15} /> Activity
            </button>
          )}
        </div>
        {subtitle && <p>{subtitle}</p>}
      </div>
      <div className="topbar-actions">
        <a
          className="topbar-brand-link"
          href={hubUrl}
          target="_top"
          aria-label="Return to All Applications"
          title="Return to All Applications"
        >
          <img src="/brand/son-aero-mark.png" alt="" />
        </a>
        <NotificationsMenu user={user} onOpenNotification={onOpenNotification} />
        <div className="topbar-identity">
          <ThemeSwitch theme={theme} onToggleTheme={onToggleTheme} />
          <UserProfile user={user} />
        </div>
        <button className="button ghost" onClick={refresh} title="Reload tracker data">
          <RefreshCw size={15} /> Refresh
        </button>
        {screen === 'project' && canEnterProjectEdit && selectedProject && selectedProject.status !== 'Complete' && (
          <button className={`button ${editMode ? 'primary' : 'ghost'} ${hasUnsavedChanges ? 'has-unsaved-changes' : ''}`} onClick={onToggleEdit} title={editMode && hasUnsavedChanges ? 'Review unsaved project details before leaving edit mode' : 'Edit the operation grid inline'}>
            {editMode ? <><Check size={15} /> Done{hasUnsavedChanges && <span className="button-dirty-dot" aria-label="Unsaved project details" />}</> : <><Pencil size={15} /> Edit</>}
          </button>
        )}
        {showExports && (
          <details className="export-menu">
            <summary className="button ghost">
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list">
              <a href={xlsxHref}><FileSpreadsheet size={15} /> XLSX</a>
              <a href={pdfHref}><FileText size={15} /> PDF</a>
            </div>
          </details>
        )}
        {screen === 'dashboard' && (
          <>
            <label className="topbar-search" aria-label="Search dashboard programs">
              <Search size={15} />
              <input
                value={dashboardSearch}
                onChange={(event) => setDashboardSearch(event.target.value)}
                placeholder="Search part, sales order, or customer"
              />
            </label>
            {canCreateProject && <button className="button primary" onClick={onAddProject}><Plus size={15} /> Add Project</button>}
          </>
        )}
        {screen === 'pastProjects' && (
          <label className="topbar-search" aria-label="Search past projects">
            <Search size={15} />
            <input
              value={pastProjectsSearch}
              onChange={(event) => setPastProjectsSearch(event.target.value)}
              placeholder="Search completed projects"
            />
          </label>
        )}
      </div>
    </header>
  )
}

/* ---------------------------------------------------------------------- */
/* Dashboard                                                              */
/* ---------------------------------------------------------------------- */
