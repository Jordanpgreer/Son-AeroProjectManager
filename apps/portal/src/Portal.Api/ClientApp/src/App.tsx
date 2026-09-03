import { useEffect, useState } from 'react'
import type { MouseEvent as ReactMouseEvent } from 'react'
import {
  AlertTriangle,
  AppWindow,
  ArrowUpRight,
  Bell,
  Boxes,
  Calculator,
  ClipboardCheck,
  Clock,
  Database,
  Eye,
  GanttChart,
  LayoutGrid,
  Settings,
  ShieldCheck,
  Truck,
  Wrench,
} from 'lucide-react'
import './index.css'
import './portal-typography.css'
import './portal-dark.css'
import './hub-catalog.css'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import AccountSetupWelcome from './AccountSetupWelcome'
import type { AccountStatus } from './AccountSetupWelcome'
import AdminConsole from './admin/AdminConsole'
import { isAdminHash } from './admin/api'
import {
  applicationNavigationMode,
  canLaunchAccessPreview,
  canOpenAdminConsole,
} from './navigation'
import type { AdminAccessPreviewLaunch, AdminAccessPreviewTarget } from './admin/types'

type AppStatus = 'active' | 'comingSoon' | 'maintenance'

interface PortalApp {
  id: string
  name: string
  description: string
  category: string
  icon: string
  url: string
  order: number
  status: AppStatus
  hasPreview: boolean
}

interface Me {
  accountName: string
  displayName: string
  accountStatus: 'configured' | AccountStatus
  role: string | null
}

interface ApplicationNotification {
  applicationId: string
  unreadCount: number
}

const ICONS: Record<string, typeof AppWindow> = {
  'gantt-chart': GanttChart,
  'shield-check': ShieldCheck,
  truck: Truck,
  settings: Settings,
  boxes: Boxes,
  wrench: Wrench,
  clock: Clock,
  'layout-grid': LayoutGrid,
  database: Database,
  calculator: Calculator,
  'clipboard-check': ClipboardCheck,
}

const CAPABILITIES: Record<string, string[]> = {
  'project-tracker': ['Scheduling', 'Gantt timelines', 'Operations'],
  'engineering-hub': ['Drawing control', 'Tooling', 'Technical records'],
  'estimating-dashboard': ['Quoting', 'Cost roll-ups', 'Bid tracking'],
  'quality-assurance': ['Shipping status', 'Queue routing', 'Audit history'],
  'admin-console': ['Application catalog', 'Access control', 'Configuration'],
}

function iconFor(key: string) {
  return ICONS[key] ?? AppWindow
}

function appendUrlParameter(url: string, name: string, value: string) {
  const separator = url.includes('?') ? '&' : '?'
  return `${url}${separator}${encodeURIComponent(name)}=${encodeURIComponent(value)}`
}

const launchToken = new URLSearchParams(window.location.search).get('launch') ?? Date.now().toString()
const applicationLaunchUrl = (url: string) => appendUrlParameter(url, 'launch', launchToken)

const prefersReducedMotion = () =>
  typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

function initials(name: string) {
  const parts = name.split(' ').filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}

function capabilityLabels(application: PortalApp) {
  return CAPABILITIES[application.id] ?? [application.category, 'Internal workflow']
}

export default function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [me, setMe] = useState<Me | null>(null)
  const [apps, setApps] = useState<PortalApp[] | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notificationCounts, setNotificationCounts] = useState<Record<string, number>>({})
  const [launchingAppId, setLaunchingAppId] = useState<string | null>(null)
  const [locationHash, setLocationHash] = useState(() => window.location.hash)
  const [accessPreview, setAccessPreview] = useState<AdminAccessPreviewTarget | null>(null)
  const [previewLaunchError, setPreviewLaunchError] = useState<string | null>(null)

  async function load() {
    setLoading(true)
    setError(null)
    try {
      const [meResponse, appsResponse] = await Promise.all([
        fetch('/api/me', { credentials: 'include' }),
        fetch('/api/apps', { credentials: 'include' }),
      ])
      if (!meResponse.ok || !appsResponse.ok) {
        throw new Error(`Portal service responded ${meResponse.status} / ${appsResponse.status}.`)
      }
      setMe(await meResponse.json())
      setApps(await appsResponse.json())
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to reach the portal service.')
    } finally {
      setLoading(false)
    }
  }

  async function loadApplicationNotifications() {
    try {
      const response = await fetch('/api/application-notifications', { credentials: 'include' })
      if (!response.ok) {
        setNotificationCounts({})
        return
      }
      const notifications = await response.json() as ApplicationNotification[]
      setNotificationCounts(Object.fromEntries(
        notifications.map((notification) => [notification.applicationId, notification.unreadCount]),
      ))
    } catch {
      // Notification badges are best-effort and must never block the application catalog.
      setNotificationCounts({})
    }
  }

  useEffect(() => {
    void load()
  }, [])

  useEffect(() => {
    if (me?.accountStatus !== 'configured') {
      setNotificationCounts({})
      return
    }

    void loadApplicationNotifications()
    const notificationInterval = window.setInterval(() => {
      void loadApplicationNotifications()
    }, 20_000)
    return () => window.clearInterval(notificationInterval)
  }, [me?.accountStatus])

  useEffect(() => {
    persistTheme(theme)
  }, [theme])

  useEffect(() => {
    const updateRoute = () => {
      setLocationHash(window.location.hash)
      setLaunchingAppId(null)
    }
    window.addEventListener('hashchange', updateRoute)
    return () => window.removeEventListener('hashchange', updateRoute)
  }, [])

  useEffect(() => {
    const syncTheme = () => setTheme(readThemePreference())
    const onVisibility = () => {
      if (document.visibilityState === 'visible') syncTheme()
    }
    window.addEventListener('focus', syncTheme)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      window.removeEventListener('focus', syncTheme)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [])

  const catalogApps = accessPreview?.applications ?? apps
  const requestedAdminRoute = isAdminHash(locationHash)
  const adminRoute = requestedAdminRoute && canOpenAdminConsole(me?.role)
  const previewAccountStatus = accessPreview?.kind === 'user'
    && accessPreview.accountStatus !== 'configured'
    ? accessPreview.accountStatus
    : null
  const accountStatus = previewAccountStatus
    ?? (!accessPreview && me?.accountStatus !== 'configured' ? me?.accountStatus : null)
  const accountDisplayName = accessPreview?.title ?? me?.displayName ?? ''

  useEffect(() => {
    if (!adminRoute) document.title = 'Arda · Applications'
  }, [adminRoute])

  useEffect(() => {
    if (adminRoute) setAccessPreview(null)
  }, [adminRoute])

  useEffect(() => {
    if (requestedAdminRoute && me && !canOpenAdminConsole(me.role)) {
      window.location.hash = '#/'
    }
  }, [me, requestedAdminRoute])

  async function launchApplication(application: PortalApp, event: ReactMouseEvent<HTMLAnchorElement>) {
    if (!accessPreview && (event.metaKey || event.ctrlKey || event.shiftKey || event.button === 1 || prefersReducedMotion())) return
    event.preventDefault()
    if (launchingAppId) return
    if (accessPreview) {
      setLaunchingAppId(application.id)
      setPreviewLaunchError(null)
      try {
        const response = await fetch(
          `/api/admin/access-previews/${encodeURIComponent(accessPreview.key)}/launch/${encodeURIComponent(application.id)}`,
          { method: 'POST', credentials: 'include' },
        )
        if (!response.ok) {
          const problem = await response.json().catch(() => null) as { detail?: string } | null
          throw new Error(problem?.detail ?? `Preview launch failed (${response.status}).`)
        }
        const launch = await response.json() as AdminAccessPreviewLaunch
        const form = document.createElement('form')
        form.method = 'POST'
        form.action = launch.actionUrl
        const token = document.createElement('input')
        token.type = 'hidden'
        token.name = 'token'
        token.value = launch.token
        form.append(token)
        document.body.append(form)
        form.submit()
      } catch (cause) {
        setLaunchingAppId(null)
        setPreviewLaunchError(cause instanceof Error ? cause.message : 'The preview could not be opened.')
      }
      return
    }
    const destination = applicationLaunchUrl(application.url)
    if (applicationNavigationMode(destination) === 'same-document') {
      window.location.assign(destination)
      return
    }
    setLaunchingAppId(application.id)
    window.setTimeout(() => {
      window.location.assign(destination)
    }, 240)
  }

  function startAccessPreview(target: AdminAccessPreviewTarget) {
    setAccessPreview(target)
    setPreviewLaunchError(null)
    window.location.hash = '#/'
  }

  async function startWalkthroughPreview(target: AdminAccessPreviewTarget) {
    setPreviewLaunchError(null)
    const response = await fetch(
      `/api/admin/access-previews/${encodeURIComponent(target.key)}/walkthrough`,
      { method: 'POST', credentials: 'include' },
    )
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { detail?: string } | null
      const message = problem?.detail ?? `Walkthrough preview launch failed (${response.status}).`
      throw new Error(message)
    }
    const launch = await response.json() as AdminAccessPreviewLaunch
    const form = document.createElement('form')
    form.method = 'POST'
    form.action = launch.actionUrl
    const token = document.createElement('input')
    token.type = 'hidden'
    token.name = 'token'
    token.value = launch.token
    form.append(token)
    document.body.append(form)
    form.submit()
  }

  function returnToAdmin() {
    setAccessPreview(null)
    setPreviewLaunchError(null)
    window.location.hash = '#/admin/access'
  }

  return (
    <div className={`portal ${adminRoute ? 'admin-portal' : 'catalog-portal'} ${launchingAppId ? 'is-launching' : ''}`.trim()}>
      <header className="portal-top">
        <div className="brand arda-brand">
          <span className="arda-logo-surface">
            <img
              className="arda-logo arda-logo-lockup arda-logo-standard"
              src="/brand/arda-lockup.png"
              alt="Arda"
              width="1825"
              height="862"
            />
            <img
              className="arda-logo arda-logo-lockup arda-logo-reversed"
              src="/brand/arda-lockup-reversed.png"
              alt="Arda"
              width="1825"
              height="862"
            />
            <img
              className="arda-logo arda-logo-mark arda-logo-standard"
              src="/brand/arda-mark.png"
              alt="Arda"
              width="1254"
              height="1254"
            />
            <img
              className="arda-logo arda-logo-mark arda-logo-reversed"
              src="/brand/arda-mark-reversed.png"
              alt="Arda"
              width="1254"
              height="1254"
            />
          </span>
        </div>
        <div className="portal-user-actions">
          <ThemeSwitch
            theme={theme}
            onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
          />
          <div className="portal-user" aria-live="polite">
            {me ? (
              <>
                <div className="portal-user-text">
                  <span className="portal-user-name">{me.displayName}</span>
                </div>
                <span className="portal-avatar" title={me.displayName}>
                  {initials(me.displayName)}
                </span>
              </>
            ) : (
              <span className="portal-user-name muted">Signing in...</span>
            )}
          </div>
        </div>
      </header>

      {accessPreview && (
        <section className="access-preview-banner" role="status">
          <span><Eye size={17} aria-hidden="true" /></span>
          <div>
            <strong>Read-only preview: {accessPreview.title}</strong>
            <small>{accessPreview.kind === 'user' ? accessPreview.subtitle : accessPreview.role} · Open an application to inspect its full read-only experience</small>
          </div>
          <button type="button" className="ghost-button" onClick={returnToAdmin}>Return to Admin</button>
        </section>
      )}

      {previewLaunchError && (
        <div className="access-preview-launch-error" role="alert">
          <AlertTriangle size={16} aria-hidden="true" />
          <span>{previewLaunchError}</span>
          <button type="button" onClick={() => setPreviewLaunchError(null)} aria-label="Dismiss preview launch error">Dismiss</button>
        </div>
      )}

      {accountStatus ? (
        <AccountSetupWelcome
          accountStatus={accountStatus}
          displayName={accountDisplayName}
          onRetry={() => void load()}
        />
      ) : adminRoute ? (
        <AdminConsole
          currentAccountName={me?.accountName ?? null}
          currentPortalRole={me?.role ?? null}
          onPreviewAccess={startAccessPreview}
          onPreviewWalkthrough={startWalkthroughPreview}
        />
      ) : (
        <main className="portal-main catalog-main">
        <section className="catalog-intro" aria-labelledby="catalog-title">
          <div className="catalog-intro-copy">
            <span className="kicker">Arda Internal Systems</span>
            <h1 id="catalog-title">{accessPreview ? `Access for ${accessPreview.title}` : 'Applications'}</h1>
            <p>{accessPreview ? 'This is a read-only view of the application cards available to this target.' : 'Choose an internal workspace for your account.'}</p>
          </div>
        </section>

        {loading ? (
          <section className="catalog-loading" aria-live="polite" aria-busy="true">
            <span className="sr-only">Loading applications</span>
            <div className="catalog-section-heading skeleton-heading" aria-hidden="true" />
            <ul className="catalog-grid" aria-hidden="true">
              {[0, 1, 2, 3].map((key) => (
                <li key={key} className="catalog-card-skeleton" />
              ))}
            </ul>
          </section>
        ) : error ? (
          <section className="catalog-state error" role="alert">
            <AlertTriangle size={27} />
            <h2>Could not load the application catalog</h2>
            <p>{error}</p>
            <button type="button" className="solid-button" onClick={() => void load()}>
              Try again
            </button>
          </section>
        ) : !catalogApps || catalogApps.length === 0 ? (
          <section className="catalog-state" aria-live="polite">
            <AppWindow size={27} />
            <h2>No applications available</h2>
            <p>Your account does not currently have access to an application.</p>
          </section>
        ) : (
          <div className="catalog-results" aria-live="polite">
            <ApplicationSection
              title="Application catalog"
              description="Company tools and workspaces"
              applications={catalogApps}
              notificationCounts={accessPreview ? {} : notificationCounts}
              launchingAppId={launchingAppId}
              onLaunch={launchApplication}
              previewMode={Boolean(accessPreview)}
            />
          </div>
        )}
        </main>
      )}

      <footer className="portal-foot">
        <span className="foot-mark">ARDA</span>
        <span className="foot-sep" aria-hidden="true" />
        <span>Internal Operations Platform</span>
      </footer>
      {launchingAppId && <div className="catalog-launch-veil" aria-hidden="true" />}
    </div>
  )
}

function ApplicationSection({
  title,
  description,
  applications,
  notificationCounts,
  launchingAppId,
  onLaunch,
  previewMode,
}: {
  title: string
  description: string
  applications: PortalApp[]
  notificationCounts: Record<string, number>
  launchingAppId: string | null
  onLaunch: (application: PortalApp, event: ReactMouseEvent<HTMLAnchorElement>) => void
  previewMode: boolean
}) {
  return (
    <section className="catalog-section">
      <header className="catalog-section-heading">
        <div>
          <h2>{title}</h2>
          <p>{description}</p>
        </div>
      </header>
      <ul className="catalog-grid">
        {applications.map((application) => (
          <ApplicationCard
            key={application.id}
            application={application}
            unreadCount={notificationCounts[application.id] ?? 0}
            launching={launchingAppId === application.id}
            onLaunch={onLaunch}
            previewMode={previewMode}
          />
        ))}
      </ul>
    </section>
  )
}

function ApplicationCard({
  application,
  unreadCount,
  launching,
  onLaunch,
  previewMode,
}: {
  application: PortalApp
  unreadCount: number
  launching: boolean
  onLaunch: (application: PortalApp, event: ReactMouseEvent<HTMLAnchorElement>) => void
  previewMode: boolean
}) {
  const Icon = iconFor(application.icon)
  const available = application.status === 'active' && application.url.length > 0
  const previewAvailable = !previewMode || canLaunchAccessPreview(application.id)
  const openable = available && previewAvailable
  const content = (
    <>
      <div className="catalog-card-top">
        <span className="catalog-app-icon" aria-hidden="true">
          <Icon size={24} strokeWidth={1.7} />
        </span>
        {unreadCount > 0 && (
          <span
            className="catalog-notification"
            title={`${unreadCount} unread notification${unreadCount === 1 ? '' : 's'} in ${application.name}`}
            aria-label={`${unreadCount} unread notification${unreadCount === 1 ? '' : 's'}`}
          >
            <Bell size={15} aria-hidden="true" />
            <strong>{unreadCount > 99 ? '99+' : unreadCount}</strong>
          </span>
        )}
      </div>
      <div className="catalog-card-copy">
        <span className="catalog-category">{application.category}</span>
        <h3>{application.name}</h3>
        <p>{application.description}</p>
      </div>
      <ul className="catalog-capabilities" aria-label={`${application.name} capabilities`}>
        {capabilityLabels(application).map((label) => <li key={label}>{label}</li>)}
      </ul>
      {available && (
        <div className="catalog-card-footer">
          <span className="catalog-open">
            {launching
              ? 'Opening...'
              : previewMode
                ? previewAvailable ? 'Open read-only preview' : 'Visible to this user · Full preview unavailable'
                : 'Open application'}
            {previewMode
              ? previewAvailable && <Eye size={16} aria-hidden="true" />
              : <ArrowUpRight size={16} aria-hidden="true" />}
          </span>
        </div>
      )}
    </>
  )

  return (
    <li>
      {openable ? (
        <a
          className={`catalog-app-card is-openable ${launching ? 'is-launching' : ''}`.trim()}
          href={previewMode ? '#' : applicationLaunchUrl(application.url)}
          onClick={(event) => onLaunch(application, event)}
          aria-label={`Open ${application.name}`}
        >
          {content}
        </a>
      ) : (
        <article className={`catalog-app-card ${previewMode && available ? 'is-preview' : ''}`.trim()} data-status={available ? undefined : application.status} aria-disabled="true">
          <span className="sr-only">This application cannot currently be opened.</span>
          {content}
        </article>
      )}
    </li>
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
