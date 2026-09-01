import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  FileUp,
  LayoutDashboard,
  LockKeyhole,
  PanelLeftClose,
  PanelLeftOpen,
  RefreshCw,
  Settings2,
  ShieldCheck,
  Truck,
} from 'lucide-react'
import { qualityApi } from './api'
import Dashboard from './Dashboard'
import QualityNotificationCenter from './QualityNotificationCenter'
import ShippingStatus from './ShippingStatus'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import type { QualityAssuranceUser } from './types'

function defaultHubUrl() {
  const hostname = window.location.hostname.toLowerCase()
  const permanentHosts = new Set([
    'hub.son4l.local',
    'projects.hub.son4l.local',
    'engineering.hub.son4l.local',
    'estimating.hub.son4l.local',
    'quality.hub.son4l.local',
  ])
  if (permanentHosts.has(hostname)) return 'https://hub.son4l.local'
  if (new Set(['localhost', '127.0.0.1', '[::1]']).has(hostname)) {
    return `http://${window.location.hostname}:5140`
  }
  if (hostname === 'son-iis2') {
    return window.location.protocol === 'https:' ? 'https://SON-IIS2:6140' : 'http://SON-IIS2:5140'
  }
  return 'https://hub.son4l.local'
}

const hubUrl = defaultHubUrl()
const qualityAdminUrl = new URL('/#/admin/quality-assurance/assignment-rules', hubUrl).toString()

function initials(name: string) {
  const parts = name.split(/\s+/).filter(Boolean)
  if (!parts.length) return 'QA'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return `${parts[0][0]}${parts.at(-1)?.[0] ?? ''}`.toUpperCase()
}

function routeFromHash() {
  return window.location.hash.toLowerCase().startsWith('#/shipping-status')
    ? 'shipping-status'
    : 'dashboard'
}

function ThemeSwitch({ theme, onToggleTheme }: { theme: AppTheme; onToggleTheme: () => void }) {
  const dark = theme === 'dark'
  const actionLabel = dark ? 'Switch to light mode' : 'Switch to dark mode'
  return (
    <label className="theme-switch" title={actionLabel}>
      <input type="checkbox" className="theme-switch__checkbox" checked={dark} onChange={onToggleTheme} aria-label={actionLabel} />
      <span className="theme-switch__container" aria-hidden="true">
        <span className="theme-switch__clouds" />
        <span className="theme-switch__stars-container"><i /><i /><i /><i /></span>
        <span className="theme-switch__circle-container"><span className="theme-switch__sun-moon-container"><span className="theme-switch__moon"><i className="theme-switch__spot" /><i className="theme-switch__spot" /><i className="theme-switch__spot" /></span></span></span>
      </span>
    </label>
  )
}

export default function App() {
  const [theme, setTheme] = useState<AppTheme>(() => readThemePreference())
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('sonaero-quality-sidebar') === 'collapsed'
    } catch {
      return false
    }
  })
  const [user, setUser] = useState<QualityAssuranceUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [route, setRoute] = useState(routeFromHash)
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    try {
      window.localStorage.setItem(
        'sonaero-quality-sidebar',
        sidebarCollapsed ? 'collapsed' : 'expanded',
      )
    } catch {
      // Sidebar state persistence is optional.
    }
  }, [sidebarCollapsed])

  useEffect(() => {
    let active = true
    void qualityApi<QualityAssuranceUser>('/api/me')
      .then((current) => { if (active) setUser(current) })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Unable to verify access.') })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [])

  useEffect(() => { persistTheme(theme) }, [theme])
  useEffect(() => {
    const syncTheme = () => setTheme(readThemePreference())
    const syncRoute = () => setRoute(routeFromHash())
    const onVisibility = () => { if (document.visibilityState === 'visible') syncTheme() }
    window.addEventListener('focus', syncTheme)
    window.addEventListener('hashchange', syncRoute)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      window.removeEventListener('focus', syncTheme)
      window.removeEventListener('hashchange', syncRoute)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [])

  useEffect(() => {
    document.title = `${route === 'dashboard' ? 'Quality Dashboard' : 'Shipping Status'} · Arda`
  }, [route])

  if (!user) {
    return (
      <main className="access-state">
        <section className="access-card" aria-live="polite">
          <span className="access-icon">{loading ? <ShieldCheck size={31} /> : <LockKeyhole size={31} />}</span>
          <span className="eyebrow">Quality Assurance access</span>
          <h1>{loading ? 'Checking your access' : 'Access unavailable'}</h1>
          <p>{loading ? 'Verifying your Arda account and shared Quality permissions.' : error ?? 'Your assigned groups do not grant access to Quality Assurance.'}</p>
          {!loading && <a className="return-button" href={hubUrl} target="_top"><AlertTriangle size={17} /> Return to Applications</a>}
        </section>
      </main>
    )
  }

  const page = route === 'dashboard'
    ? { eyebrow: 'Quality Assurance Module', title: 'Dashboard', description: 'Your workload, due-date risk, and completion performance.' }
    : { eyebrow: 'Quality Operations', title: 'Shipping Status', description: 'Controlled shipment queue, ownership, and completion tracking.' }
  const userPermissions = user.permissions

  function openMentionedShipment(shipmentId: number, isShipped: boolean) {
    const notificationScope = userPermissions.includes('quality-assurance.shipments.view-all')
      ? 'all'
      : userPermissions.includes('quality-assurance.dashboard.team-view') ? 'team' : 'mine'
    window.location.hash = `#/shipping-status?shipment=${shipmentId}&comments=1&scope=${notificationScope}&status=${isShipped ? 'shipped' : 'open'}`
    setRoute('shipping-status')
    setReloadKey((value) => value + 1)
  }

  return (
    <div className={`qa-shell quality-assurance-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''}`}>
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <aside className="sidebar" id="quality-sidebar">
        <a className="brand brand-hub-link" href={hubUrl} target="_top" aria-label="Return to Arda applications" title="Return to Arda applications"><img className="brand-lockup brand-lockup-standard" src="/brand/arda-lockup.png" alt="" /><img className="brand-lockup brand-lockup-reversed" src="/brand/arda-lockup-reversed.png" alt="" /><img className="brand-mark brand-mark-standard" src="/brand/arda-mark.png" alt="" /><img className="brand-mark brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" /></a>
        <button type="button" className="sidebar-rail-toggle" aria-label={sidebarCollapsed ? 'Expand Quality Assurance navigation' : 'Collapse Quality Assurance navigation'} aria-expanded={!sidebarCollapsed} aria-controls="quality-sidebar" title={sidebarCollapsed ? 'Expand navigation' : 'Collapse navigation'} onClick={() => setSidebarCollapsed((current) => !current)}>
          {sidebarCollapsed ? <PanelLeftOpen size={18} aria-hidden="true" /> : <PanelLeftClose size={18} aria-hidden="true" />}
          <span className="sidebar-rail-toggle-label">{sidebarCollapsed ? 'Expand menu' : 'Collapse menu'}</span>
        </button>
        <div className="nav-section">
          <span className="nav-heading">Quality Assurance</span>
          <nav aria-label="Quality Assurance pages">
            <a className={`nav-button ${route === 'dashboard' ? 'active' : ''}`} href="#/dashboard" aria-current={route === 'dashboard' ? 'page' : undefined} title="Dashboard"><span className="nav-icon"><LayoutDashboard size={17} /></span><span className="nav-label">Dashboard</span></a>
            <a className={`nav-button ${route === 'shipping-status' ? 'active' : ''}`} href="#/shipping-status" aria-current={route === 'shipping-status' ? 'page' : undefined} title="Shipping Status"><span className="nav-icon"><Truck size={17} /></span><span className="nav-label">Shipping Status</span></a>
          </nav>
        </div>
        <div className="sidebar-foot"><nav className="foot-nav" aria-label="Quality Assurance administration"><a className="nav-button" href={qualityAdminUrl} target="_top" title="Quality Admin / Settings"><span className="nav-icon"><Settings2 size={17} /></span><span className="nav-label">Quality Admin / Settings</span></a></nav></div>
      </aside>

      <main className="main-area" id="main-content">
        <header className="topbar">
          <div className="topbar-title-area">
            <div className="page-title-block"><span className="eyebrow">{page.eyebrow}</span><h1>{page.title}</h1><p>{page.description}</p></div>
          </div>
          <div className="topbar-actions">
            <a className="topbar-brand-link" href={hubUrl} target="_top" aria-label="Return to Arda applications" title="Return to Arda applications"><img className="topbar-brand-mark-standard" src="/brand/arda-mark.png" alt="" /><img className="topbar-brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" /></a>
            <div className="topbar-identity"><ThemeSwitch theme={theme} onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')} /><div className="user-chip topbar-user-chip" title={`${user.displayName}\n${user.groups.join(', ')}`}><span className="user-copy"><strong>{user.displayName}</strong></span><span className="avatar">{initials(user.displayName)}</span></div></div>
            {userPermissions.includes('quality-assurance.shipments.view') && userPermissions.includes('quality-assurance.fields.comments.view') && <QualityNotificationCenter onOpenShipment={openMentionedShipment} />}
            {route === 'shipping-status' && userPermissions.includes('quality-assurance.shipments.import') && <button className="button ghost" type="button" onClick={() => window.dispatchEvent(new Event('quality:open-shipping-import'))}><FileUp size={15} /> Import Excel</button>}
            <button className="button ghost" type="button" onClick={() => setReloadKey((value) => value + 1)}><RefreshCw size={15} /> Refresh</button>
          </div>
        </header>
        <div className="main-scroll">{route === 'dashboard' ? <Dashboard reloadKey={reloadKey} /> : <ShippingStatus user={user} reloadKey={reloadKey} />}</div>
      </main>
    </div>
  )
}
