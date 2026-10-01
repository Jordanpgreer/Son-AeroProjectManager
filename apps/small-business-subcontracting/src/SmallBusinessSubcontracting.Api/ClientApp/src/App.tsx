import { useEffect, useState } from 'react'
import { ArrowLeft, Building2, LayoutDashboard, Moon, ShieldCheck, Sun } from 'lucide-react'
import { api } from './api'
import ComplianceDashboard from './ComplianceDashboard'
import type { AppUser } from './types'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import VendorsPage from './VendorsPage'

function resolveHubUrl() {
  const host = window.location.hostname.toLowerCase()
  if (host === 'localhost' || host === '127.0.0.1' || host === '[::1]') return `http://${window.location.hostname}:5140`
  if (host === 'son-iis2') return window.location.protocol === 'https:' ? 'https://SON-IIS2:6140' : 'http://SON-IIS2:5140'
  return 'https://hub.son4l.local'
}

export const hubUrl = resolveHubUrl()

function readRoute() {
  const value = window.location.hash.replace(/^#\/?/, '')
  const vendorMatch = value.match(/^vendors\/(\d+)$/)
  return {
    page: value.startsWith('compliance') ? 'compliance' : 'vendors',
    vendorId: vendorMatch ? Number(vendorMatch[1]) : null,
  }
}

export default function App() {
  const [user, setUser] = useState<AppUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [route, setRoute] = useState(readRoute)
  const [theme, setTheme] = useState<AppTheme>(readThemePreference)

  useEffect(() => {
    const onHashChange = () => setRoute(readRoute())
    window.addEventListener('hashchange', onHashChange)
    api<AppUser>('/api/me')
      .then(setUser)
      .catch((reason: Error) => setError(reason.message))
      .finally(() => setLoading(false))
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])

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

  function toggleTheme() {
    const next = theme === 'dark' ? 'light' : 'dark'
    setTheme(next)
    persistTheme(next)
  }

  if (loading) return <div className="startup-state"><div className="brand-glyph">A</div><p>Opening Small Business Subcontracting...</p></div>
  if (!user) return <div className="access-state"><ShieldCheck size={30} /><h1>Module unavailable</h1><p>{error ?? 'Your account could not be verified.'}</p><a className="primary-button" href={hubUrl}>Return to Applications</a></div>

  const canViewVendors = user.permissions.includes('small-business-subcontracting.vendors.view')
  const canViewDashboard = user.permissions.includes('small-business-subcontracting.dashboard.view')

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="arda-brand" href={hubUrl} title="Return to Applications">
          <img className="brand-lockup" src="/brand/arda-lockup-reversed.png" alt="Arda by Son-Aero" />
          <img className="brand-mark" src="/brand/arda-mark-reversed.png" alt="" aria-hidden="true" />
        </a>
        <div className="module-title"><Building2 size={18} /><span>Small Business<br />Subcontracting</span></div>
        <nav>
          {canViewVendors && <button type="button" className={route.page === 'vendors' ? 'active' : ''} onClick={() => selectPage('vendors')}><Building2 size={17} /><span>Vendors</span></button>}
          {canViewDashboard && <button type="button" className={route.page === 'compliance' ? 'active' : ''} onClick={() => selectPage('compliance')}><LayoutDashboard size={17} /><span>Compliance Dashboard</span></button>}
        </nav>
        <div className="sidebar-bottom">
          <a href={hubUrl}><ArrowLeft size={16} /><span>All Applications</span></a>
          <div className="user-block"><span>{initials(user.displayName)}</span><div><strong>{user.displayName}</strong><small>{user.role}</small></div></div>
        </div>
      </aside>
      <div className="main-shell">
        <header className="topbar">
          <div><span className="topbar-kicker">Compliance Workspace</span><strong>{route.page === 'vendors' ? 'Vendor Records' : 'Vendor Compliance'}</strong></div>
          <button className="theme-button" type="button" onClick={toggleTheme} aria-label={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}>{theme === 'dark' ? <Sun size={17} /> : <Moon size={17} />}</button>
        </header>
        <main>
          {error && <div className="error-banner" role="alert"><span>{error}</span><button type="button" onClick={() => setError(null)}>Dismiss</button></div>}
          {route.page === 'vendors' && canViewVendors ? (
            <VendorsPage user={user} selectedVendorId={route.vendorId} onOpenVendor={openVendor} onCloseVendor={() => selectPage('vendors')} onError={setError} />
          ) : route.page === 'compliance' && canViewDashboard ? (
            <ComplianceDashboard onOpenVendor={openVendor} onError={setError} />
          ) : (
            <div className="page-access-state"><ShieldCheck size={26} /><h1>No page access</h1><p>An administrator can grant access from Arda's Permission Groups page.</p></div>
          )}
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
