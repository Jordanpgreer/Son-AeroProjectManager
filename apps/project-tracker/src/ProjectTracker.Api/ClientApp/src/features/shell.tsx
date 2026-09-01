import '../App.css'
import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Archive,
  Bell,
  BellOff,
  BellRing,
  CalendarCheck2,
  CalendarRange,
  ChevronDown,
  Check,
  CheckCheck,
  Clock3,
  FileSpreadsheet,
  FileText,
  GraduationCap,
  History,
  LayoutDashboard,
  ListChecks,
  MessageSquare,
  Pencil,
  Plus,
  PanelLeftClose,
  PanelLeftOpen,
  RefreshCw,
  Search,
  Settings2,
  StickyNote,
  Trash2,
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
import { usePushNotifications } from '../push-notifications'

function hasPermission(user: User | null, permission: string) {
  return Boolean(user?.permissions?.includes(permission))
}

function isScheduleConfirmation(notification: MentionNotification) {
  return notification.kind === 'OperationStartConfirmation'
    || notification.kind === 'OperationFinishConfirmation'
}

function isScheduleResponse(notification: MentionNotification) {
  return notification.kind === 'OperationStartResponse'
    || notification.kind === 'OperationFinishResponse'
}

function isScheduleNotification(notification: MentionNotification) {
  return isScheduleConfirmation(notification) || isScheduleResponse(notification)
}

function scheduleConfirmationLabel(notification: MentionNotification) {
  return notification.kind === 'OperationFinishConfirmation' ? 'Yes, it finished' : 'Yes, it started'
}

function scheduleDeclineLabel(notification: MentionNotification) {
  return notification.kind === 'OperationFinishConfirmation' ? 'No, not finished' : 'No, not started'
}

function UserProfile({ user }: { user: User | null }) {
  const avatarLabel = user
    ? userInitials(user.displayName).padEnd(2, user.displayName.slice(1, 2).toUpperCase())
    : '...'

  return (
    <div
      className="topbar-user-chip"
      aria-label={user ? `Signed in as ${user.displayName}` : 'Checking signed-in user'}
      aria-live="polite"
    >
      <div className="topbar-user-copy">
        <strong>{user?.displayName ?? 'Checking access'}</strong>
      </div>
      <span className="topbar-user-avatar" title={user?.displayName} aria-hidden="true">
        {avatarLabel}
      </span>
    </div>
  )
}

export function Sidebar({
  collapsed,
  onToggleCollapsed,
  screen,
  setScreen,
  selectedProject,
  hasActiveProjects,
  onOpenActiveProjects,
  user,
  trainingMode = false,
}: {
  collapsed: boolean
  onToggleCollapsed: () => void
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  hasActiveProjects: boolean
  onOpenActiveProjects: () => Promise<void>
  user: User | null
  trainingMode?: boolean
}) {
  return (
    <aside className="sidebar" id="project-tracker-sidebar" data-guide-id={trainingMode ? 'main-navigation' : undefined}>
      {trainingMode ? <div className="brand brand-hub-link training-brand-static" aria-label="Arda Project Tracker training">
        <img className="brand-lockup brand-lockup-standard" src="/brand/arda-lockup.png" alt="" />
        <img className="brand-lockup brand-lockup-reversed" src="/brand/arda-lockup-reversed.png" alt="" />
        <img className="brand-mark brand-mark-standard" src="/brand/arda-mark.png" alt="" />
        <img className="brand-mark brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
      </div> : <a
        className="brand brand-hub-link"
        href={hubUrl}
        target="_top"
        aria-label="Return to Arda applications"
        title="Return to Arda applications"
      >
        <img className="brand-lockup brand-lockup-standard" src="/brand/arda-lockup.png" alt="" />
        <img className="brand-lockup brand-lockup-reversed" src="/brand/arda-lockup-reversed.png" alt="" />
        <img className="brand-mark brand-mark-standard" src="/brand/arda-mark.png" alt="" />
        <img className="brand-mark brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
      </a>}

      <button
        type="button"
        className="sidebar-rail-toggle"
        aria-label={collapsed ? 'Expand Project Tracker navigation' : 'Collapse Project Tracker navigation'}
        aria-expanded={!collapsed}
        aria-controls="project-tracker-sidebar"
        title={collapsed ? 'Expand navigation' : 'Collapse navigation'}
        onClick={onToggleCollapsed}
      >
        {collapsed
          ? <PanelLeftOpen size={18} aria-hidden="true" />
          : <PanelLeftClose size={18} aria-hidden="true" />}
        <span className="sidebar-rail-toggle-label">{collapsed ? 'Expand menu' : 'Collapse menu'}</span>
      </button>

      <div className="nav-section">
        <span className="nav-heading">Program Control</span>
        <nav aria-label="Primary">
          <NavButton guideId={trainingMode ? 'nav-dashboard' : undefined} active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />
          <NavButton guideId={trainingMode ? 'nav-project' : undefined} active={screen === 'project' && selectedProject?.status !== 'Complete'} onClick={() => void onOpenActiveProjects()} icon={<ListChecks size={17} />} label="Project Detail" disabled={!hasActiveProjects} />
          <NavButton guideId={trainingMode ? 'nav-calendar' : undefined} active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />
          <NavButton guideId={trainingMode ? 'nav-past' : undefined} active={screen === 'pastProjects' || (screen === 'project' && selectedProject?.status === 'Complete')} onClick={() => setScreen('pastProjects')} icon={<Archive size={17} />} label="Past Projects" />
        </nav>
      </div>

      {user?.isAdmin && (
        <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Secondary">
            <a
              className="nav-button"
              href={`${hubUrl.replace(/\/+$/, '')}/#/admin/access`}
              target="_top"
              aria-label="Hub Admin / Admin settings"
              title="Hub Admin / Admin settings"
            >
              <span className="nav-icon"><Settings2 size={17} /></span>
              <span className="nav-label">Hub Admin / Admin settings</span>
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
  guideId,
}: {
  active: boolean
  onClick: () => void
  icon: ReactNode
  label: string
  disabled?: boolean
  guideId?: string
}) {
  return (
    <button
      className={`nav-button ${active ? 'active' : ''}`}
      onClick={onClick}
      disabled={disabled}
      aria-current={active ? 'page' : undefined}
      title={label}
      data-guide-id={guideId}
    >
      <span className="nav-icon">{icon}</span>
      <span className="nav-label">{label}</span>
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
  const [actionError, setActionError] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<string | null>(null)
  const [scheduleActionNotificationId, setScheduleActionNotificationId] = useState<number | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)
  const knownNotificationIdsRef = useRef<Set<number>>(new Set())
  const notificationsInitializedRef = useRef(false)
  const notificationRequestGenerationRef = useRef(0)
  const push = usePushNotifications({
    registered: Boolean(user?.isRegistered),
    previewReadOnly: Boolean(user?.preview?.readOnly),
  })

  const dismissToast = (notificationId: number) => {
    setToasts((current) => current.filter((notification) => notification.id !== notificationId))
  }

  const loadNotifications = async (showLoading = false) => {
    if (!user?.isRegistered) return
    const requestGeneration = notificationRequestGenerationRef.current
    if (showLoading) setLoading(true)
    try {
      const next = await api<MentionNotification[]>('/api/notifications')
      if (requestGeneration !== notificationRequestGenerationRef.current) return
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
      if (requestGeneration !== notificationRequestGenerationRef.current) return
      setNotifications([])
      setError('Notifications could not be refreshed. Please try again.')
    } finally {
      if (showLoading && requestGeneration === notificationRequestGenerationRef.current) {
        setLoading(false)
      }
    }
  }

  useEffect(() => {
    setNotifications([])
    setToasts([])
    setError(null)
    setActionError(null)
    setActionMessage(null)
    setScheduleActionNotificationId(null)
    setLoading(false)
    notificationRequestGenerationRef.current += 1
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

  const respondToScheduleNotification = async (notification: MentionNotification, response: 'Yes' | 'No') => {
    if (!isScheduleConfirmation(notification) || scheduleActionNotificationId !== null) return
    setActionError(null)
    setActionMessage(null)
    setScheduleActionNotificationId(notification.id)
    try {
      await api<void>(`/api/notifications/${notification.id}/respond`, {
        method: 'POST',
        body: JSON.stringify({ response }),
      })
      setNotifications((current) => current.filter((item) => item.id !== notification.id))
      dismissToast(notification.id)
      knownNotificationIdsRef.current.delete(notification.id)
      setActionMessage(response === 'No'
        ? `${notification.operationName || 'Operation'} was reported as not ${notification.kind === 'OperationFinishConfirmation' ? 'finished' : 'started'}.`
        : notification.kind === 'OperationFinishConfirmation'
          ? `${notification.operationName || 'Operation'} was marked complete.`
          : `${notification.operationName || 'Operation'} will now update progress automatically each workday.`)
      await loadNotifications()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : 'That response could not be saved. Please try again.')
      await loadNotifications()
    } finally {
      setScheduleActionNotificationId(null)
    }
  }

  const snoozeScheduleNotification = async (notification: MentionNotification) => {
    if (!isScheduleConfirmation(notification) || scheduleActionNotificationId !== null) return
    setActionError(null)
    setActionMessage(null)
    setScheduleActionNotificationId(notification.id)
    try {
      await api<void>(`/api/notifications/${notification.id}/snooze`, { method: 'POST' })
      setNotifications((current) => current.filter((item) => item.id !== notification.id))
      dismissToast(notification.id)
      knownNotificationIdsRef.current.delete(notification.id)
      setActionMessage(`We'll ask about ${notification.operationName || 'this operation'} again tomorrow.`)
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : 'That reminder could not be snoozed. Please try again.')
      await loadNotifications()
    } finally {
      setScheduleActionNotificationId(null)
    }
  }

  const deleteNotification = async (notificationId: number) => {
    setActionError(null)
    notificationRequestGenerationRef.current += 1
    setNotifications((current) => current.filter((notification) => notification.id !== notificationId))
    dismissToast(notificationId)
    knownNotificationIdsRef.current.delete(notificationId)

    try {
      await api<void>(`/api/notifications/${notificationId}`, { method: 'DELETE' })
      notificationRequestGenerationRef.current += 1
      setNotifications((current) => current.filter((notification) => notification.id !== notificationId))
      dismissToast(notificationId)
      knownNotificationIdsRef.current.delete(notificationId)
      setLoading(false)
    } catch {
      notificationRequestGenerationRef.current += 1
      await loadNotifications(true)
      setActionError('That notification could not be deleted. Please try again.')
    }
  }

  const clearAllNotifications = async () => {
    setActionError(null)
    setActionMessage(null)
    notificationRequestGenerationRef.current += 1
    setNotifications((current) => current.filter(isScheduleConfirmation))
    setToasts((current) => current.filter(isScheduleConfirmation))
    knownNotificationIdsRef.current = new Set(
      notifications.filter(isScheduleConfirmation).map((notification) => notification.id),
    )

    try {
      await api<void>('/api/notifications', { method: 'DELETE' })
      notificationRequestGenerationRef.current += 1
      setNotifications((current) => current.filter(isScheduleConfirmation))
      setToasts((current) => current.filter(isScheduleConfirmation))
      knownNotificationIdsRef.current = new Set(
        notifications.filter(isScheduleConfirmation).map((notification) => notification.id),
      )
      setLoading(false)
    } catch {
      notificationRequestGenerationRef.current += 1
      await loadNotifications(true)
      setActionError('Notifications could not be cleared. Please try again.')
    }
  }

  return (
    <div className="notifications-menu" ref={rootRef}>
      <button
        className={`button ghost notification-trigger ${unreadCount ? 'has-unread' : ''}`}
        type="button"
        data-benny-target="notifications"
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
            <div className="notification-heading">
              <span className="kicker">Personal Inbox</span>
              <h2>Notifications</h2>
            </div>
            {!user?.preview?.readOnly && notifications.length > 0 && (
              <div className="notification-actions">
                {unreadCount > 0 && (
                  <button className="notification-read-all" type="button" onClick={() => void markAllRead()}>
                    <CheckCheck size={14} /> Mark all read
                  </button>
                )}
                {notifications.some((notification) => !isScheduleConfirmation(notification)) && (
                  <button className="notification-clear-all" type="button" onClick={() => void clearAllNotifications()}>
                    <Trash2 size={14} /> Clear mentions
                  </button>
                )}
              </div>
            )}
          </header>
          <DesktopNotificationControl
            status={push.status}
            message={push.message}
            onEnable={() => void push.enable()}
            onDisable={() => void push.disable()}
            onRetry={() => void push.refresh()}
          />
          <div className="notification-list" aria-live="polite">
            {actionError && (
              <div className="notification-action-error" role="alert">
                <span>{actionError}</span>
                <button
                  type="button"
                  onClick={() => {
                    setActionError(null)
                    void loadNotifications(true)
                  }}
                >
                  Refresh
                </button>
              </div>
            )}
            {actionMessage && (
              <div className="notification-action-success" role="status">
                <Check size={14} aria-hidden="true" />
                <span>{actionMessage}</span>
              </div>
            )}
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
                <span>Mentions and operation schedule checks will appear here.</span>
              </div>
            ) : notifications.map((notification) => (
              <div
                className={`notification-item ${notification.readAt ? '' : 'unread'} ${isScheduleNotification(notification) ? 'schedule-confirmation' : ''}`}
                key={notification.id}
              >
                <button
                  type="button"
                  className="notification-item-open"
                  onClick={() => void openNotification(notification)}
                >
                  <span className={`notification-source ${isScheduleNotification(notification) ? 'schedule' : notification.kind === 'OperationNoteMention' ? 'note' : 'chat'}`}>
                    {isScheduleNotification(notification)
                      ? <CalendarCheck2 size={14} />
                      : notification.kind === 'OperationNoteMention' ? <StickyNote size={14} /> : <MessageSquare size={14} />}
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
                {!user?.preview?.readOnly && !isScheduleConfirmation(notification) && (
                  <button
                    type="button"
                    className="notification-delete"
                    onClick={() => void deleteNotification(notification.id)}
                    aria-label={`Delete notification from ${notification.actorDisplayName}`}
                    title="Delete notification"
                  >
                    <X size={15} />
                  </button>
                )}
                {!user?.preview?.readOnly && isScheduleConfirmation(notification) && (
                  <div className="notification-confirm-actions">
                    <button
                      className="button primary"
                      type="button"
                      onClick={() => void respondToScheduleNotification(notification, 'Yes')}
                      disabled={scheduleActionNotificationId !== null}
                    >
                      <Check size={14} aria-hidden="true" />
                      {scheduleActionNotificationId === notification.id ? 'Saving...' : scheduleConfirmationLabel(notification)}
                    </button>
                    <button
                      className="button ghost"
                      type="button"
                      onClick={() => void respondToScheduleNotification(notification, 'No')}
                      disabled={scheduleActionNotificationId !== null}
                    >
                      <X size={14} aria-hidden="true" />
                      {scheduleDeclineLabel(notification)}
                    </button>
                    <button
                      className="button ghost"
                      type="button"
                      onClick={() => void snoozeScheduleNotification(notification)}
                      disabled={scheduleActionNotificationId !== null}
                    >
                      <Clock3 size={14} aria-hidden="true" />
                      Snooze 1 day
                    </button>
                    <button className="button ghost" type="button" onClick={() => void openNotification(notification)}>
                      Review operation
                    </button>
                  </div>
                )}
              </div>
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
              onRespond={isScheduleConfirmation(notification)
                ? (response) => void respondToScheduleNotification(notification, response)
                : undefined}
              onSnooze={isScheduleConfirmation(notification)
                ? () => void snoozeScheduleNotification(notification)
                : undefined}
              acting={scheduleActionNotificationId === notification.id}
            />
          ))}
        </aside>
      )}
      {push.invitationOpen && (
        <DesktopNotificationInvitation
          onEnable={() => void push.enable()}
          onDismiss={push.dismissInvitation}
        />
      )}
    </div>
  )
}

function DesktopNotificationInvitation({
  onEnable,
  onDismiss,
}: {
  onEnable: () => void
  onDismiss: () => void
}) {
  return (
    <section className="desktop-notification-invitation" role="dialog" aria-labelledby="desktop-notification-invitation-title" aria-describedby="desktop-notification-invitation-description">
      <span className="desktop-notification-invitation-icon" aria-hidden="true"><BellRing size={20} /></span>
      <div>
        <span className="kicker">Project mentions</span>
        <h2 id="desktop-notification-invitation-title">Receive desktop notifications?</h2>
        <p id="desktop-notification-invitation-description">Project Tracker can notify you through Windows when someone mentions you, even while this tab is in the background.</p>
      </div>
      <div className="desktop-notification-invitation-actions">
        <button className="button primary" type="button" onClick={onEnable}>Enable notifications</button>
        <button className="button ghost" type="button" onClick={onDismiss}>Not now</button>
      </div>
    </section>
  )
}

function DesktopNotificationControl({
  status,
  message,
  onEnable,
  onDisable,
  onRetry,
}: {
  status: ReturnType<typeof usePushNotifications>['status']
  message: string | null
  onEnable: () => void
  onDisable: () => void
  onRetry: () => void
}) {
  const copy = {
    checking: ['Checking desktop notifications', 'Your in-app notifications still work.'],
    unsupported: ['Desktop notifications unavailable', 'This browser does not support web push. In-app notifications will still appear.'],
    insecure: ['HTTPS required', 'Open Project Tracker over HTTPS to enable Windows desktop notifications.'],
    preview: ['Unavailable during access preview', 'Return to your own account before changing desktop notification settings.'],
    denied: ['Notifications blocked by the browser', 'Allow notifications for this site in browser settings, then retry.'],
    disabled: ['Desktop notifications are off', 'Enable them to receive mentions while Project Tracker is in the background.'],
    enabled: ['Desktop notifications are on', 'Mentions can appear through Windows even when this tab is in the background.'],
    working: ['Updating desktop notifications', 'Please keep this window open for a moment.'],
    error: ['Desktop notifications need attention', message ?? 'The setting could not be updated.'],
  } satisfies Record<typeof status, [string, string]>
  const [title, description] = copy[status]

  return (
    <div className={`desktop-notification-control ${status}`} aria-live="polite">
      <span className="desktop-notification-icon" aria-hidden="true">
        {status === 'enabled' ? <BellRing size={16} /> : <BellOff size={16} />}
      </span>
      <span className="desktop-notification-copy">
        <strong>{title}</strong>
        <span>{description}</span>
      </span>
      {status === 'disabled' && (
        <button className="button ghost" type="button" onClick={onEnable}>Enable</button>
      )}
      {status === 'enabled' && (
        <button className="button ghost" type="button" onClick={onDisable}>Turn off</button>
      )}
      {(status === 'denied' || status === 'error') && (
        <button className="button ghost" type="button" onClick={onRetry}>Retry</button>
      )}
    </div>
  )
}

function NotificationToast({
  notification,
  onDismiss,
  onOpen,
  onRespond,
  onSnooze,
  acting,
}: {
  notification: MentionNotification
  onDismiss: () => void
  onOpen: () => void
  onRespond?: (response: 'Yes' | 'No') => void
  onSnooze?: () => void
  acting: boolean
}) {
  const dismissRef = useRef(onDismiss)
  dismissRef.current = onDismiss
  useEffect(() => {
    const timeout = window.setTimeout(() => dismissRef.current(), 8_000)
    return () => window.clearTimeout(timeout)
  }, [notification.id])

  const scheduleConfirmation = isScheduleConfirmation(notification)
  const scheduleNotification = isScheduleNotification(notification)
  const destination = (notification.kind === 'OperationNoteMention' || scheduleNotification) && notification.operationName
    ? `${notification.operationName} in ${notification.projectName}`
    : `project ${notification.projectName}`

  return (
    <article className={`notification-toast ${scheduleNotification ? 'schedule-confirmation' : ''}`}>
      <button className="notification-toast-open" type="button" onClick={onOpen}>
        <span className={`notification-source ${scheduleNotification ? 'schedule' : notification.kind === 'OperationNoteMention' ? 'note' : 'chat'}`}>
          {scheduleNotification
            ? <CalendarCheck2 size={16} />
            : notification.kind === 'OperationNoteMention' ? <StickyNote size={16} /> : <MessageSquare size={16} />}
        </span>
        <span>
          <strong>{scheduleNotification ? notification.title : `${notification.actorDisplayName} mentioned you`}</strong>
          <small>{destination}</small>
          {notification.bodyPreview && <span>{notification.bodyPreview}</span>}
        </span>
      </button>
      <button className="notification-toast-dismiss" type="button" onClick={onDismiss} aria-label="Dismiss notification">
        <X size={15} />
      </button>
      {scheduleConfirmation && onRespond && onSnooze && (
        <div className="notification-toast-actions">
          <button className="button primary" type="button" onClick={() => onRespond('Yes')} disabled={acting}>
            <Check size={14} aria-hidden="true" />
            {acting ? 'Saving...' : scheduleConfirmationLabel(notification)}
          </button>
          <button className="button ghost" type="button" onClick={() => onRespond('No')} disabled={acting}>
            {scheduleDeclineLabel(notification)}
          </button>
          <button className="button ghost" type="button" onClick={onSnooze} disabled={acting}>
            <Clock3 size={14} aria-hidden="true" /> Snooze
          </button>
          <button className="button ghost" type="button" onClick={onOpen}>Review</button>
        </div>
      )}
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
  onStartTour,
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
  onStartTour: () => void
  user: User | null
  onOpenNotification: (notification: MentionNotification) => Promise<void>
}) {
  const portfolioExports = screen === 'dashboard'
  const pastProjectExports = screen === 'pastProjects'
  const projectId = selectedProject?.id
  const xlsxHref = portfolioExports ? '/api/reports/portfolio.xlsx' : pastProjectExports ? '/api/reports/past-projects.xlsx' : `/api/reports/projects/${projectId}.xlsx`
  const pdfHref = portfolioExports ? '/api/reports/portfolio.pdf' : pastProjectExports ? '/api/reports/past-projects.pdf' : `/api/reports/projects/${projectId}.pdf`
  const customerPdfHref = `/api/reports/projects/${projectId}/customer.pdf`
  const showExports = screen === 'dashboard' || screen === 'project' || screen === 'pastProjects'
  const showCustomerExport = screen === 'project' && projectId !== undefined
  const showTraining = Boolean(user?.walkthroughEnabled && user.permissions.includes('module.view') && !user.preview)
  const eyebrow = screenEyebrow(screen)
  const subtitle = screenSubtitle(screen)

  return (
    <header className="topbar">
      <div className="topbar-title-area">
        <div className="page-title-block">
          {eyebrow && <span className="eyebrow">{eyebrow}</span>}
          <div className="page-title-row">
            <h1 className={screen === 'project' ? 'technical-id' : undefined}>{screenTitle(screen, selectedProject)}</h1>
            {screen === 'project' && selectedProject && hasPermission(user, 'project.activity.view') && (
              <button className="button ghost page-activity-button" data-benny-target="project-activity" type="button" onClick={onOpenActivity}>
                <History size={15} /> Activity
              </button>
            )}
          </div>
          {subtitle && <p>{subtitle}</p>}
        </div>
      </div>
      <div className="topbar-actions">
        {screen === 'dashboard' && (
          <label className={`topbar-search topbar-live-filter ${dashboardSearch.trim() ? 'is-active' : ''}`} data-benny-target="dashboard-search" aria-label="Search and live-filter dashboard projects">
            <Search size={15} aria-hidden="true" />
            <input
              value={dashboardSearch}
              onChange={(event) => setDashboardSearch(event.target.value)}
              placeholder="Search part, sales order, job, or customer"
            />
            {dashboardSearch.trim() && <span className="live-filter-indicator" aria-hidden="true">Live</span>}
          </label>
        )}
        {screen === 'pastProjects' && (
          <label className="topbar-search" data-benny-target="past-search" aria-label="Search past projects">
            <Search size={15} aria-hidden="true" />
            <input
              value={pastProjectsSearch}
              onChange={(event) => setPastProjectsSearch(event.target.value)}
              placeholder="Search completed projects"
            />
          </label>
        )}
        <a
          className="topbar-brand-link"
          href={hubUrl}
          target="_top"
          aria-label="Return to Arda applications"
          title="Return to Arda applications"
        >
          <img className="topbar-brand-mark-standard" src="/brand/arda-mark.png" alt="" />
          <img className="topbar-brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
        </a>
        <NotificationsMenu user={user} onOpenNotification={onOpenNotification} />
        <div className="topbar-identity">
          <ThemeSwitch theme={theme} onToggleTheme={onToggleTheme} />
          <UserProfile user={user} />
        </div>
        {showTraining && (
          <button
            className="button ghost"
            type="button"
            title={`Take the ${screenTitle(screen, selectedProject)} tour with fictional training data`}
            onClick={onStartTour}
          >
            <GraduationCap size={15} /> Tour
          </button>
        )}
        <button className="button ghost" onClick={refresh} title="Reload tracker data">
          <RefreshCw size={15} /> Refresh
        </button>
        {screen === 'project' && canEnterProjectEdit && selectedProject && selectedProject.status !== 'Complete' && (
          <button className={`button ${editMode ? 'primary' : 'ghost'} ${hasUnsavedChanges ? 'has-unsaved-changes' : ''}`} data-benny-target="project-edit" onClick={onToggleEdit} title={editMode && hasUnsavedChanges ? 'Review unsaved project details before leaving edit mode' : 'Edit the operation grid inline'}>
            {editMode ? <><Check size={15} /> Done{hasUnsavedChanges && <span className="button-dirty-dot" aria-label="Unsaved project details" />}</> : <><Pencil size={15} /> Edit</>}
          </button>
        )}
        {showExports && (
          <details className="export-menu">
            <summary className="button ghost" data-benny-target="exports">
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list">
              <a href={xlsxHref}><FileSpreadsheet size={15} /> XLSX</a>
              <a href={pdfHref}><FileText size={15} /> PDF</a>
              {showCustomerExport && (
                <a href={customerPdfHref} title="Customer-facing schedule without internal detail">
                  <FileText size={15} /> Customer PDF
                </a>
              )}
            </div>
          </details>
        )}
        {screen === 'dashboard' && canCreateProject && <button className="button primary" data-benny-target="add-project" onClick={onAddProject}><Plus size={15} /> Add Project</button>}
      </div>
    </header>
  )
}

/* ---------------------------------------------------------------------- */
/* Dashboard                                                              */
/* ---------------------------------------------------------------------- */
