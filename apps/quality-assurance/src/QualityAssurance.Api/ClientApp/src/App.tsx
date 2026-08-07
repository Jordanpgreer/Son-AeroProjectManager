import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  Check,
  LayoutDashboard,
  LockKeyhole,
  RefreshCw,
  Settings2,
  ShieldCheck,
} from 'lucide-react'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'

interface QualityAssuranceUser {
  accountName: string
  displayName: string
  moduleKey: 'quality-assurance'
  role: 'Admin'
  permissions: string[]
}

const hubUrl = import.meta.env.VITE_HUB_URL
  ?? `${window.location.protocol}//${window.location.hostname}:5140`
const qualityAdminUrl = new URL('/#/admin/access', hubUrl).toString()

function initials(name: string) {
  const parts = name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return 'QA'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return `${parts[0][0]}${parts.at(-1)?.[0] ?? ''}`.toUpperCase()
}

function ThemeSwitch({ theme, onToggleTheme }: { theme: AppTheme; onToggleTheme: () => void }) {
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
        <span className="theme-switch__stars-container"><i /><i /><i /><i /></span>
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

export default function App() {
  const [theme, setTheme] = useState<AppTheme>(() => readThemePreference())
  const [user, setUser] = useState<QualityAssuranceUser | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    void fetch('/api/me', { credentials: 'include' })
      .then(async (response) => {
        if (!response.ok) {
          const problem = await response.json().catch(() => null) as { message?: string } | null
          throw new Error(problem?.message ?? `Quality Assurance access failed (${response.status}).`)
        }
        return response.json() as Promise<QualityAssuranceUser>
      })
      .then((currentUser) => {
        if (active) setUser(currentUser)
      })
      .catch((cause) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Unable to verify access.')
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    persistTheme(theme)
  }, [theme])

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

  if (!user) {
    return (
      <main className="access-state">
        <section className="access-card" aria-live="polite">
          <span className="access-icon">
            {loading ? <ShieldCheck size={31} /> : <LockKeyhole size={31} />}
          </span>
          <span className="eyebrow">Quality Assurance access</span>
          <h1>{loading ? 'Checking administrator access' : 'Access unavailable'}</h1>
          <p>{loading
            ? 'Verifying your SON-AERO account and Quality Assurance assignment.'
            : error ?? 'Only active administrators can open this module.'}</p>
          {!loading && (
            <a className="return-button" href={hubUrl} target="_top">
              <AlertTriangle size={17} /> Return to Applications
            </a>
          )}
        </section>
      </main>
    )
  }

  return (
    <div className="qa-shell quality-assurance-app">
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <aside className="sidebar">
        <a
          className="brand brand-hub-link"
          href={hubUrl}
          target="_top"
          aria-label="Return to All Applications"
          title="Return to All Applications"
        >
          <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero - Sonfarrel Aerospace" />
        </a>

        <div className="nav-section">
          <span className="nav-heading">Quality Assurance</span>
          <nav aria-label="Quality Assurance pages">
            <a className="nav-button active" href="#/dashboard" aria-current="page">
              <span className="nav-icon"><LayoutDashboard size={17} /></span>
              Dashboard
            </a>
          </nav>
        </div>

        <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Quality Assurance administration">
            <a className="nav-button" href={qualityAdminUrl} target="_top">
              <span className="nav-icon"><Settings2 size={17} /></span>
              Quality Admin / Settings
            </a>
          </nav>
        </div>
      </aside>

      <main className="main-area" id="main-content">
        <header className="topbar">
          <div className="page-title-block">
            <span className="eyebrow">Quality Assurance Module</span>
            <h1>Dashboard</h1>
            <p>Controlled quality workspace and system readiness.</p>
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
            <div className="topbar-identity">
              <ThemeSwitch
                theme={theme}
                onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
              />
              <div className="user-chip topbar-user-chip" title={user.accountName}>
                <span className="user-copy"><strong>{user.displayName}</strong><small>{user.role}</small></span>
                <span className="avatar">{initials(user.displayName)}</span>
              </div>
            </div>
            <button className="button ghost" type="button" onClick={() => window.location.reload()}>
              <RefreshCw size={15} /> Refresh
            </button>
          </div>
        </header>

        <div className="main-scroll">
          <div className="view">
            <section className="kpi-row" aria-label="Quality Assurance status">
              <article className="kpi"><span className="kpi-label">Module status</span><strong>Online</strong><small>Dashboard available</small></article>
              <article className="kpi"><span className="kpi-label">Access level</span><strong>Admin</strong><small>Permission controlled</small></article>
              <article className="kpi"><span className="kpi-label">Operational data</span><strong>Not configured</strong><small>Foundation only</small></article>
            </section>

            <section className="dashboard-grid" aria-label="Quality Assurance dashboard readiness">
              <article className="panel readiness-card">
              <div className="card-heading">
                <div><span className="eyebrow">System readiness</span><h3>Module foundation</h3></div>
                <span className="status-pill"><span /> Online</span>
              </div>
              <ul className="readiness-list">
                <li><span><Check size={15} /></span><div><strong>Administrator access enforced</strong><small>Portal and direct module access are permission controlled.</small></div></li>
                <li><span><Check size={15} /></span><div><strong>Shared identity connected</strong><small>Windows or development identity resolves through the Hub user registry.</small></div></li>
                <li><span><Check size={15} /></span><div><strong>Dashboard shell ready</strong><small>Future quality workflows can be added as controlled dashboard sections.</small></div></li>
              </ul>
              </article>

              <article className="panel scope-card">
              <span className="eyebrow">Current scope</span>
              <h3>Dashboard only</h3>
              <p>No quality records, audits, inspections, or corrective-action workflows have been enabled yet.</p>
              <div className="scope-rule" />
              <dl>
                <div><dt>Access level</dt><dd>Admin</dd></div>
                <div><dt>Operational data</dt><dd>Not configured</dd></div>
                <div><dt>Module status</dt><dd>Foundation ready</dd></div>
              </dl>
              </article>
            </section>
          </div>
        </div>
      </main>
    </div>
  )
}
