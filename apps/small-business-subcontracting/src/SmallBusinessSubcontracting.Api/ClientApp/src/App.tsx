import { useEffect, useState } from 'react'
import {
  ArrowLeft,
  Building2,
  LayoutDashboard,
  PanelLeftClose,
  PanelLeftOpen,
  ShieldCheck,
} from 'lucide-react'
import { api } from './api'
import ComplianceDashboard from './ComplianceDashboard'
import type { AppUser } from './types'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import VendorsPage from './VendorsPage'
import { routeFromHash } from './navigation'

function resolveHubUrl() {
  const host = window.location.hostname.toLowerCase()
  if (host === 'localhost' || host === '127.0.0.1' || host === '[::1]') return `http://${window.location.hostname}:5140`
  if (host === 'son-iis2') return window.location.protocol === 'https:' ? 'https://SON-IIS2:6140' : 'http://SON-IIS2:5140'
  return 'https://hub.son4l.local'
}

export const hubUrl = resolveHubUrl()

function ThemeSwitch({ theme, onChange }: { theme: AppTheme; onChange: (theme: AppTheme) => void }) {
  const dark = theme === 'dark'
  const actionLabel = dark ? 'Switch to light mode' : 'Switch to dark mode'
  return (
    <label className="theme-switch" title={actionLabel}>
      <input
        type="checkbox"
        className="theme-switch__checkbox"
        checked={dark}
        onChange={() => onChange(dark ? 'light' : 'dark')}
        aria-label={actionLabel}
      />
      <span className="theme-switch__track" aria-hidden="true">
        <span className="theme-switch__icon theme-switch__icon--sun">☀</span>
        <span className="theme-switch__icon theme-switch__icon--moon">●</span>
        <span className="theme-switch__thumb" />
      </span>
    </label>
  )
}

export default function App() {
  const [user, setUser] = useState<AppUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [route, setRoute] = useState(() => routeFromHash(window.location.hash))
  const [theme, setTheme] = useState<AppTheme>(readThemePreference)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('sonaero-subcontracting-sidebar') === 'collapsed'
    } catch {
      return false
    }
  })

  useEffect(() => {
    const onHashChange = () => setRoute(routeFromHash(window.location.hash))
    window.addEventListener('hashchange', onHashChange)
    api<AppUser>('/api/me')
      .then(setUser)
      .catch((reason: Error) => setError(reason.message))
      .finally(() => setLoading(false))
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

  useEffect(() => {
    try {
      window.localStorage.setItem('sonaero-subcontracting-sidebar', sidebarCollapsed ? 'collapsed' : 'expanded')
    } catch {
      // Sidebar persistence is optional in locked-down browsers.
    }
  }, [sidebarCollapsed])

  useEffect(() => {
    document.title = `${route.page === 'vendors' ? 'Vendor Records' : 'Vendor Compliance'} · Arda`
  }, [route.page])

  useEffect(() => {
    if (!user) return
    const canViewVendors = user.permissions.includes('small-business-subcontracting.vendors.view')
    const canViewDashboard = user.permissions.includes('small-business-subcontracting.dashboard.view')
    if (route.page === 'vendors' && !canViewVendors && canViewDashboard) selectPage('compliance')
    if (route.page === 'compliance' && !canViewDashboard && canViewVendors) selectPage('vendors')
  }, [route.page, user])

  function selectPage(page: 'vendors' | 'compliance') {
    window.location.hash = page === 'vendors' ? '#/vendors' : '#/compliance'
  }

  function openVendor(id: number) {
    window.location.hash = `#/vendors/${id}`
  }

  function changeTheme(next: AppTheme) {
    setTheme(next)
    persistTheme(next)
  }

  if (loading) return <div className="startup-state"><div className="brand-glyph">A</div><p>Opening Small Business Subcontracting...</p></div>
  if (!user) return <div className="access-state"><ShieldCheck size={30} /><h1>Module unavailable</h1><p>{error ?? 'Your account could not be verified.'}</p><a className="primary-button" href={hubUrl}>Return to Applications</a></div>

  const canViewVendors = user.permissions.includes('small-business-subcontracting.vendors.view')
  const canViewDashboard = user.permissions.includes('small-business-subcontracting.dashboard.view')
  const canExportDashboard = user.permissions.includes('small-business-subcontracting.export')

  return (
    <div className={`app-shell small-business-subcontracting-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''}`}>
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <aside className="sidebar" id="subcontracting-sidebar">
        <a className="brand brand-hub-link" href={hubUrl} target="_top" aria-label="Return to Arda applications" title="Return to Arda applications">
          <img className="brand-lockup brand-lockup-standard" src="/brand/arda-lockup.png" alt="" />
          <img className="brand-lockup brand-lockup-reversed" src="/brand/arda-lockup-reversed.png" alt="" />
          <img className="brand-mark brand-mark-standard" src="/brand/arda-mark.png" alt="" />
          <img className="brand-mark brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
        </a>
        <button
          type="button"
          className="sidebar-rail-toggle"
          aria-label={sidebarCollapsed ? 'Expand subcontracting navigation' : 'Collapse subcontracting navigation'}
          aria-expanded={!sidebarCollapsed}
          aria-controls="subcontracting-sidebar"
          title={sidebarCollapsed ? 'Expand navigation' : 'Collapse navigation'}
          onClick={() => setSidebarCollapsed((current) => !current)}
        >
          {sidebarCollapsed ? <PanelLeftOpen size={18} aria-hidden="true" /> : <PanelLeftClose size={18} aria-hidden="true" />}
          <span>{sidebarCollapsed ? 'Expand menu' : 'Collapse menu'}</span>
        </button>
        <div className="module-title"><Building2 size={18} aria-hidden="true" /><span>Small Business<br />Subcontracting</span></div>
        <nav className="primary-nav" aria-label="Small Business Subcontracting pages">
          {canViewVendors && <a className={`nav-link ${route.page === 'vendors' ? 'active' : ''}`} href="#/vendors" aria-current={route.page === 'vendors' ? 'page' : undefined} title="Vendor Records"><span className="nav-icon"><Building2 size={17} aria-hidden="true" /></span><span className="nav-link-label">Vendor Records</span></a>}
          {canViewDashboard && <a className={`nav-link ${route.page === 'compliance' ? 'active' : ''}`} href="#/compliance" aria-current={route.page === 'compliance' ? 'page' : undefined} title="Compliance Dashboard"><span className="nav-icon"><LayoutDashboard size={17} aria-hidden="true" /></span><span className="nav-link-label">Compliance Dashboard</span></a>}
        </nav>
        <div className="sidebar-bottom">
          <a className="all-applications-link" href={hubUrl} target="_top"><ArrowLeft size={16} aria-hidden="true" /><span>All Applications</span></a>
          <div className="user-block"><span className="avatar">{initials(user.displayName)}</span><div><strong>{user.displayName}</strong><small>{user.role}</small></div></div>
        </div>
      </aside>
      <div className="main-shell main-area">
        <header className="topbar">
          <div className="topbar-title-area">
            <span className="topbar-kicker">Compliance Workspace</span>
            <strong>{route.page === 'vendors' ? 'Vendor Records' : 'Vendor Compliance'}</strong>
            <span>{route.page === 'vendors' ? 'Fulcrum directory and Arda-controlled records' : 'Certification readiness and reporting'}</span>
          </div>
          <div className="topbar-actions">
            <span className="topbar-user-name" title={user.displayName}>{user.displayName}</span>
            <a className="topbar-brand-link" href={hubUrl} target="_top" aria-label="Return to Arda applications" title="Return to Arda applications">
              <img className="topbar-brand-mark-standard" src="/brand/arda-mark.png" alt="" />
              <img className="topbar-brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
            </a>
            <ThemeSwitch theme={theme} onChange={changeTheme} />
          </div>
        </header>
        <nav className="mobile-page-nav" aria-label="Small Business Subcontracting pages">
          {canViewVendors && <a href="#/vendors" aria-current={route.page === 'vendors' ? 'page' : undefined}><Building2 size={16} aria-hidden="true" />Vendors</a>}
          {canViewDashboard && <a href="#/compliance" aria-current={route.page === 'compliance' ? 'page' : undefined}><LayoutDashboard size={16} aria-hidden="true" />Compliance</a>}
        </nav>
        <main className="main-scroll">
          <div className="view" id="main-content" tabIndex={-1}>
            {error && <div className="error-banner" role="alert"><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
            {route.page === 'vendors' && canViewVendors ? (
              <VendorsPage user={user} selectedVendorId={route.vendorId} onOpenVendor={openVendor} onCloseVendor={() => selectPage('vendors')} onError={setError} />
            ) : route.page === 'compliance' && canViewDashboard ? (
              <ComplianceDashboard canOpenVendors={canViewVendors} canExport={canExportDashboard} onOpenVendor={openVendor} onError={setError} />
            ) : (
              <div className="page-access-state"><ShieldCheck size={26} /><h1>No page access</h1><p>An administrator can grant access from Arda's Permission Groups page.</p></div>
            )}
          </div>
        </main>
      </div>
    </div>
  )
}

function initials(name: string) {
  const parts = name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return 'SB'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return `${parts[0][0]}${parts.at(-1)?.[0] ?? ''}`.toUpperCase()
}
