import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  LayoutDashboard,
  LockKeyhole,
  RefreshCw,
  Settings2,
  ShieldCheck,
  Truck,
} from 'lucide-react'
import { qualityApi } from './api'
import Dashboard from './Dashboard'
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
  const [user, setUser] = useState<QualityAssuranceUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [route, setRoute] = useState(routeFromHash)
  const [reloadKey, setReloadKey] = useState(0)

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
    document.title = `${route === 'dashboard' ? 'Quality Dashboard' : 'Shipping Status'} - SON-AERO`
  }, [route])

  if (!user) {
    return (
      <main className="access-state">
        <section className="access-card" aria-live="polite">
          <span className="access-icon">{loading ? <ShieldCheck size={31} /> : <LockKeyhole size={31} />}</span>
          <span className="eyebrow">Quality Assurance access</span>
          <h1>{loading ? 'Checking your access' : 'Access unavailable'}</h1>
          <p>{loading ? 'Verifying your SON-AERO account and shared Quality permissions.' : error ?? 'Your assigned groups do not grant access to Quality Assurance.'}</p>
          {!loading && <a className="return-button" href={hubUrl} target="_top"><AlertTriangle size={17} /> Return to Applications</a>}
        </section>
      </main>
    )
  }

  const page = route === 'dashboard'
    ? { eyebrow: 'Quality Assurance Module', title: 'Dashboard', description: 'Your workload, due-date risk, and completion performance.' }
    : { eyebrow: 'Quality Operations', title: 'Shipping Status', description: 'Controlled shipment queue, ownership, and completion tracking.' }

  return (
    <div className="qa-shell quality-assurance-app">
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <aside className="sidebar">
        <a className="brand brand-hub-link" href={hubUrl} target="_top" aria-label="Return to All Applications" title="Return to All Applications"><img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero - Sonfarrel Aerospace" /></a>
        <div className="nav-section">
          <span className="nav-heading">Quality Assurance</span>
          <nav aria-label="Quality Assurance pages">
            <a className={`nav-button ${route === 'dashboard' ? 'active' : ''}`} href="#/dashboard" aria-current={route === 'dashboard' ? 'page' : undefined}><span className="nav-icon"><LayoutDashboard size={17} /></span>Dashboard</a>
            <a className={`nav-button ${route === 'shipping-status' ? 'active' : ''}`} href="#/shipping-status" aria-current={route === 'shipping-status' ? 'page' : undefined}><span className="nav-icon"><Truck size={17} /></span>Shipping Status</a>
          </nav>
        </div>
        <div className="sidebar-foot"><nav className="foot-nav" aria-label="Quality Assurance administration"><a className="nav-button" href={qualityAdminUrl} target="_top"><span className="nav-icon"><Settings2 size={17} /></span>Quality Admin / Settings</a></nav></div>
      </aside>

      <main className="main-area" id="main-content">
        <header className="topbar">
          <div className="page-title-block"><span className="eyebrow">{page.eyebrow}</span><h1>{page.title}</h1><p>{page.description}</p></div>
          <div className="topbar-actions">
            <a className="topbar-brand-link" href={hubUrl} target="_top" aria-label="Return to All Applications" title="Return to All Applications"><img src="/brand/son-aero-mark.png" alt="" /></a>
            <div className="topbar-identity"><ThemeSwitch theme={theme} onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')} /><div className="user-chip topbar-user-chip" title={`${user.accountName}\n${user.groups.join(', ')}`}><span className="user-copy"><strong>{user.displayName}</strong><small>{user.role}</small></span><span className="avatar">{initials(user.displayName)}</span></div></div>
            <button className="button ghost" type="button" onClick={() => setReloadKey((value) => value + 1)}><RefreshCw size={15} /> Refresh</button>
          </div>
        </header>
        <div className="main-scroll">{route === 'dashboard' ? <Dashboard reloadKey={reloadKey} /> : <ShippingStatus user={user} reloadKey={reloadKey} />}</div>
      </main>
    </div>
  )
}
