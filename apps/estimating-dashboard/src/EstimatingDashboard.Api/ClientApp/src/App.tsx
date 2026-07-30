import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  BookOpen,
  Calculator,
  LayoutDashboard,
  LockKeyhole,
  PanelLeftClose,
  PanelLeftOpen,
  ShieldCheck,
} from 'lucide-react'
import EstimateCalculatorPage from './EstimateCalculatorPage'
import EstimatingRatesPage from './EstimatingRatesPage'
import QuotesDashboardPage from './QuotesDashboardPage'
import {
  estimatingPermissions,
  hasEstimatingPermission,
} from './authorization'
import type { EstimatingMe } from './authorization'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'

type EstimatingPage = 'quotes' | 'calculator' | 'rates'

const hubUrl = import.meta.env.VITE_HUB_URL
  ?? `${window.location.protocol}//${window.location.hostname}:5140`

const PAGE_META: Record<EstimatingPage, {
  eyebrow: string
  title: string
  subtitle: string
}> = {
  quotes: {
    eyebrow: 'Estimating portfolio',
    title: 'Quotes Dashboard',
    subtitle: 'Manage draft, current, and completed quotes from one workspace.',
  },
  calculator: {
    eyebrow: 'Quote preparation',
    title: 'Estimate Calculator',
    subtitle: 'Build standard and rubber estimates across controlled quantity tiers.',
  },
  rates: {
    eyebrow: 'Controlled reference',
    title: 'Estimating Rates',
    subtitle: 'Review annual labor, burden, G&A, profit, and source history.',
  },
}

function pageFromHash(): EstimatingPage | null {
  const route = window.location.hash
    .replace(/^#\/?/, '')
    .split('?')[0]
    .toLowerCase()
  if (route === 'quotes' || route === 'calculator' || route === 'rates') return route
  return null
}

function initials(name: string) {
  const parts = name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return `${parts[0][0]}${parts.at(-1)?.[0] ?? ''}`.toUpperCase()
}

function ThemeSwitch({
  theme,
  onChange,
}: {
  theme: AppTheme
  onChange: (theme: AppTheme) => void
}) {
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

function UserChip({ me }: { me: EstimatingMe | null }) {
  return (
    <div className="user-chip" aria-live="polite">
      <div className="user-copy">
        <strong>{me?.displayName ?? 'Checking access'}</strong>
        <span>{me?.role ?? 'Loading'}</span>
      </div>
      <span className="avatar" title={me?.accountName}>
        {me ? initials(me.displayName) : '··'}
      </span>
    </div>
  )
}

export default function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [page, setPage] = useState<EstimatingPage>(() => pageFromHash() ?? 'quotes')
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('sonaero-estimating-sidebar') === 'collapsed'
    } catch {
      return false
    }
  })
  const [me, setMe] = useState<EstimatingMe | null>(null)
  const [accessLoading, setAccessLoading] = useState(true)
  const [accessError, setAccessError] = useState<string | null>(null)

  useEffect(() => {
    if (!pageFromHash()) {
      window.history.replaceState(
        null,
        '',
        `${window.location.pathname}${window.location.search}#/quotes`,
      )
    }

    const updateRoute = () => {
      const nextPage = pageFromHash()
      if (!nextPage) {
        window.history.replaceState(
          null,
          '',
          `${window.location.pathname}${window.location.search}#/quotes`,
        )
        setPage('quotes')
        return
      }
      setPage(nextPage)
    }

    window.addEventListener('hashchange', updateRoute)
    return () => window.removeEventListener('hashchange', updateRoute)
  }, [])

  useEffect(() => {
    try {
      window.localStorage.setItem(
        'sonaero-estimating-sidebar',
        sidebarCollapsed ? 'collapsed' : 'expanded',
      )
    } catch {
      // Sidebar state persistence is optional.
    }
  }, [sidebarCollapsed])

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

  useEffect(() => {
    document.title = `${PAGE_META[page].title} · SON-AERO`
  }, [page])

  useEffect(() => {
    let active = true
    void fetch('/api/me', { credentials: 'include' })
      .then(async (response) => {
        if (!response.ok) {
          const payload = await response.json().catch(() => null) as { message?: string } | null
          throw new Error(payload?.message ?? `Access service responded ${response.status}.`)
        }
        return response.json() as Promise<EstimatingMe>
      })
      .then((currentUser) => {
        if (active) {
          setMe(currentUser)
          setAccessError(null)
        }
      })
      .catch((cause) => {
        if (active) {
          setMe(null)
          setAccessError(cause instanceof Error ? cause.message : 'Unable to verify Estimating access.')
        }
      })
      .finally(() => {
        if (active) setAccessLoading(false)
      })
    return () => {
      active = false
    }
  }, [])

  const meta = PAGE_META[page]
  const canManageQuotes = hasEstimatingPermission(
    me,
    estimatingPermissions.manageQuotes,
  )
  const canManageInputs = hasEstimatingPermission(
    me,
    estimatingPermissions.manageInputs,
  )
  const canAdministerRates = hasEstimatingPermission(
    me,
    estimatingPermissions.administerRates,
  )

  if (accessLoading || !me) {
    return (
      <main className="estimating-access-state">
        <span className="access-state-icon">
          {accessLoading
            ? <ShieldCheck size={30} aria-hidden="true" />
            : <LockKeyhole size={30} aria-hidden="true" />}
        </span>
        <span className="eyebrow">Estimating access</span>
        <h1>{accessLoading ? 'Checking module access' : 'Access unavailable'}</h1>
        <p>{accessLoading
          ? 'Verifying your enabled Estimating role...'
          : accessError ?? 'Your account does not have enabled access to Estimating.'}</p>
        {!accessLoading && (
          <a className="primary-action-button" href={hubUrl} target="_top">
            <AlertTriangle size={17} aria-hidden="true" />
            Return to Applications
          </a>
        )}
      </main>
    )
  }

  return (
    <div className={`estimating-shell estimating-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''}`}>
      <a className="skip-link" href="#main-content">Skip to main content</a>

      <aside className="sidebar" id="estimating-sidebar">
        <a
          className="brand brand-hub-link"
          href={hubUrl}
          target="_top"
          aria-label="Return to All Applications"
          title="Return to All Applications"
        >
          <img className="brand-lockup" src="/brand/son-aero-lockup-dark.png" alt="SON-AERO — Sonfarrel Aerospace" />
          <img className="brand-mark" src="/brand/son-aero-mark.png" alt="SON-AERO" />
        </a>

        <section className="nav-section" aria-labelledby="estimating-nav-heading">
          <div className="nav-heading" id="estimating-nav-heading">
            <span>Estimating</span>
            <span className="nav-flag">Controlled</span>
          </div>
          <nav className="primary-nav" aria-label="Estimating pages">
            <a
              className={`nav-link ${page === 'quotes' ? 'active' : ''}`}
              href="#/quotes"
              aria-current={page === 'quotes' ? 'page' : undefined}
              title="Quotes Dashboard"
            >
              <span className="nav-icon"><LayoutDashboard size={17} aria-hidden="true" /></span>
              <span className="nav-link-label">Quotes Dashboard</span>
            </a>
            <a
              className={`nav-link ${page === 'calculator' ? 'active' : ''}`}
              href="#/calculator"
              aria-current={page === 'calculator' ? 'page' : undefined}
              title="Estimate Calculator"
            >
              <span className="nav-icon"><Calculator size={17} aria-hidden="true" /></span>
              <span className="nav-link-label">Estimate Calculator</span>
            </a>
            <a
              className={`nav-link ${page === 'rates' ? 'active' : ''}`}
              href="#/rates"
              aria-current={page === 'rates' ? 'page' : undefined}
              title="Rates Reference"
            >
              <span className="nav-icon"><BookOpen size={17} aria-hidden="true" /></span>
              <span className="nav-link-label">Rates Reference</span>
            </a>
          </nav>
        </section>

        <div className="sidebar-source">
          <ShieldCheck size={16} aria-hidden="true" />
          <div>
            <strong>Workbook parity</strong>
            <span>Annual matrix · 2023–2029</span>
          </div>
        </div>

        <div className="sidebar-foot">
          <UserChip me={me} />
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="topbar-title-area">
            <button
              type="button"
              className="icon-button sidebar-toggle"
              aria-label={sidebarCollapsed ? 'Expand estimating navigation' : 'Collapse estimating navigation'}
              aria-expanded={!sidebarCollapsed}
              aria-controls="estimating-sidebar"
              title={sidebarCollapsed ? 'Expand navigation' : 'Collapse navigation'}
              onClick={() => setSidebarCollapsed((current) => !current)}
            >
              {sidebarCollapsed
                ? <PanelLeftOpen size={19} aria-hidden="true" />
                : <PanelLeftClose size={19} aria-hidden="true" />}
            </button>
            <div className="page-title-block">
              <span className="eyebrow">{meta.eyebrow}</span>
              <h1>{meta.title}</h1>
              <p>{meta.subtitle}</p>
            </div>
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
            <ThemeSwitch theme={theme} onChange={setTheme} />
            <div className="topbar-user"><UserChip me={me} /></div>
          </div>
        </header>

        <nav className="mobile-page-nav" aria-label="Estimating pages">
          <a href="#/quotes" aria-current={page === 'quotes' ? 'page' : undefined}>
            <LayoutDashboard size={16} aria-hidden="true" />
            Quotes
          </a>
          <a href="#/calculator" aria-current={page === 'calculator' ? 'page' : undefined}>
            <Calculator size={16} aria-hidden="true" />
            Calculator
          </a>
          <a href="#/rates" aria-current={page === 'rates' ? 'page' : undefined}>
            <BookOpen size={16} aria-hidden="true" />
            Rates
          </a>
        </nav>

        <div className="main-scroll">
          <div className="view" id="main-content" tabIndex={-1}>
            {page === 'quotes' && (
              <QuotesDashboardPage
                ownerAccountName={me.accountName}
                canManageQuotes={canManageQuotes}
              />
            )}
            {page === 'calculator' && (
              <EstimateCalculatorPage
                key={me.accountName}
                ownerAccountName={me.accountName}
                canManageQuotes={canManageQuotes}
                canManageInputs={canManageInputs}
              />
            )}
            {page === 'rates' && (
              <EstimatingRatesPage canAdministerRates={canAdministerRates} />
            )}
          </div>
        </div>
      </main>
    </div>
  )
}
