import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  BookOpen,
  Calculator,
  LayoutDashboard,
  History,
  LockKeyhole,
  PanelLeftClose,
  PanelLeftOpen,
  ShieldCheck,
} from 'lucide-react'
import EstimateCalculatorPage from './EstimateCalculatorPage'
import EstimatingRatesPage from './EstimatingRatesPage'
import EstimatingHistoryPage from './EstimatingHistoryPage'
import QuotesDashboardPage from './QuotesDashboardPage'
import {
  estimatingPermissions,
  hasEstimatingPermission,
} from './authorization'
import type { EstimatingMe } from './authorization'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'

type EstimatingPage = 'quotes' | 'calculator' | 'history' | 'rates'

function defaultHubUrl() {
  const hostname = window.location.hostname.toLowerCase()
  const permanentHosts = new Set([
    'hub.son4l.local',
    'projects.hub.son4l.local',
    'engineering.hub.son4l.local',
    'estimating.hub.son4l.local',
    'quality.hub.son4l.local',
  ])
  if (permanentHosts.has(hostname)) {
    return 'https://hub.son4l.local'
  }
  const localHosts = new Set(['localhost', '127.0.0.1', '[::1]'])
  if (localHosts.has(hostname)) return `http://${window.location.hostname}:5140`
  if (hostname === 'son-iis2') {
    return window.location.protocol === 'https:'
      ? 'https://SON-IIS2:6140'
      : 'http://SON-IIS2:5140'
  }
  return 'https://hub.son4l.local'
}

const hubUrl = defaultHubUrl()

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
    subtitle: 'Build standard, rubber, and subassembly estimates across controlled quantity tiers.',
  },
  history: {
    eyebrow: 'Controlled quote intelligence',
    title: 'Estimating Logs',
    subtitle: 'Search imported Fulcrum history and monitor estimator throughput and queue health.',
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
  if (route === 'quotes' || route === 'calculator' || route === 'history' || route === 'rates') return route
  return null
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
  const [historyImportOpen, setHistoryImportOpen] = useState(false)

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
    document.title = `${PAGE_META[page].title} · Arda`
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
  ) && !me?.isPreview
  const canManageInputs = hasEstimatingPermission(
    me,
    estimatingPermissions.manageInputs,
  ) && !me?.isPreview
  const canViewHistory = hasEstimatingPermission(me, estimatingPermissions.viewHistory)
  const canImportHistory = hasEstimatingPermission(
    me,
    estimatingPermissions.importHistory,
  ) && !me?.isPreview
  useEffect(() => {
    if (accessLoading || !me || page !== 'history' || canViewHistory) return
    window.history.replaceState(
      null,
      '',
      `${window.location.pathname}${window.location.search}#/quotes`,
    )
    setPage('quotes')
  }, [accessLoading, canViewHistory, me, page])

  useEffect(() => {
    if (page !== 'history' || !canImportHistory) setHistoryImportOpen(false)
  }, [canImportHistory, page])

  if (accessLoading || !me) {
    return (
      <main className="estimating-access-state">
        <span className="access-state-icon">
          {accessLoading
            ? <ShieldCheck size={30} aria-hidden="true" />
            : <LockKeyhole size={30} aria-hidden="true" />}
        </span>
        <span className="eyebrow">Estimating access</span>
        <h1>{accessLoading ? 'Checking Module Access' : 'Access Unavailable'}</h1>
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
    <div className={`estimating-shell estimating-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''} ${me.isPreview ? 'access-preview-active' : ''}`.trim()}>
      <a className="skip-link" href="#main-content">Skip to main content</a>

      <aside className="sidebar" id="estimating-sidebar">
        <a
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
        </a>

        <button
          type="button"
          className="sidebar-rail-toggle"
          aria-label={sidebarCollapsed ? 'Expand estimating navigation' : 'Collapse estimating navigation'}
          aria-expanded={!sidebarCollapsed}
          aria-controls="estimating-sidebar"
          title={sidebarCollapsed ? 'Expand navigation' : 'Collapse navigation'}
          onClick={() => setSidebarCollapsed((current) => !current)}
        >
          {sidebarCollapsed
            ? <PanelLeftOpen size={18} aria-hidden="true" />
            : <PanelLeftClose size={18} aria-hidden="true" />}
          <span className="sidebar-rail-toggle-label">{sidebarCollapsed ? 'Expand menu' : 'Collapse menu'}</span>
        </button>

        <section className="nav-section" aria-labelledby="estimating-nav-heading">
          <div className="nav-heading" id="estimating-nav-heading">
            <span>Estimating</span>
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
            {canViewHistory && <a
              className={`nav-link ${page === 'history' ? 'active' : ''}`}
              href="#/history"
              aria-current={page === 'history' ? 'page' : undefined}
              title="Estimating Logs"
            >
              <span className="nav-icon"><History size={17} aria-hidden="true" /></span>
              <span className="nav-link-label">Estimating Logs</span>
            </a>}
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
      </aside>

      <main className="main-area">
        {me.isPreview && <section className="access-preview-banner" role="status">
          <div>
            <strong>Read-only preview: {me.previewTargetTitle ?? me.displayName}</strong>
            <span>Role and permissions are previewed. This user&apos;s browser-local quote records are not available on this computer.</span>
          </div>
          <a href="/access-preview/end" target="_top">Return to Admin</a>
        </section>}
        <header className="topbar">
          <div className="topbar-title-area">
            <div className="page-title-block">
              <span className="eyebrow">{meta.eyebrow}</span>
              <h1>{meta.title}</h1>
              <p>{meta.subtitle}</p>
            </div>
          </div>
          <div className="topbar-actions">
            <span className="topbar-user-name" title={me.displayName}>
              {me.displayName}
            </span>
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
            <ThemeSwitch theme={theme} onChange={setTheme} />
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
          {canViewHistory && <a href="#/history" aria-current={page === 'history' ? 'page' : undefined}>
            <History size={16} aria-hidden="true" />
            Logs
          </a>}
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
            {page === 'rates' && <EstimatingRatesPage />}
            {page === 'history' && canViewHistory && (
              <EstimatingHistoryPage
                canImport={canImportHistory}
                importOpen={historyImportOpen}
                onImportOpenChange={setHistoryImportOpen}
              />
            )}
          </div>
        </div>
      </main>
    </div>
  )
}
