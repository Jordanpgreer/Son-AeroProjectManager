import { useEffect, useMemo, useState } from 'react'
import {
  Beaker,
  Boxes,
  ChevronRight,
  FileStack,
  FlaskConical,
  LoaderCircle,
  Lock,
  Moon,
  RefreshCw,
  Search,
  ShieldCheck,
  SunMedium,
  Wrench,
} from 'lucide-react'
import './index.css'
import { persistTheme, readThemePreference } from './theme'
import DrawingControl from './DrawingControl'

interface Me {
  accountName: string
  displayName: string
  role: string
}

interface ModuleSection {
  id: string
  title: string
  summary: string
  status: string
  highlights: string[]
}

interface EngineeringModule {
  id: string
  name: string
  summary: string
  accessNotice: string
  sections: ModuleSection[]
}

interface SearchCategory {
  id: string
  title: string
  count: number
}

interface SearchResult {
  id: string
  category: string
  categoryLabel: string
  title: string
  identifier: string
  subtitle: string
  customer: string | null
  specificationNumber: string | null
  workOrder: string | null
  reportNumber: string | null
  tags: string[]
  note: string
}

interface DashboardData {
  searchHint: string
  categories: SearchCategory[]
  results: SearchResult[]
}

const ICONS: Record<string, typeof FileStack> = {
  dashboard: Boxes,
  'drawing-document-control': FileStack,
  'tooling-management': Wrench,
  'compound-test-data-management': FlaskConical,
}

const SECTION_TONES: Record<string, string> = {
  dashboard: 'tone-ink',
  'drawing-document-control': 'tone-steel',
  'tooling-management': 'tone-red',
  'compound-test-data-management': 'tone-ok',
}

const PAGE_NOTES: Record<string, { label: string; detail: string }> = {
  'drawing-document-control': {
    label: 'Controlled release',
    detail: 'Stage drawing issue logs, revision history, and approval routing before connecting live records.',
  },
  'tooling-management': {
    label: 'Separate inventory workspace',
    detail: 'Lay out tooling ownership, service intervals, and storage zones without touching active production schedules.',
  },
  'compound-test-data-management': {
    label: 'Material and test archive',
    detail: 'Prepare the structure for compound specs, cert packs, and engineering data retention in one place.',
  },
}

function initials(name: string) {
  const parts = name.split(' ').filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return `${parts[0][0]}${parts[parts.length - 1][0]}`.toUpperCase()
}

export default function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [me, setMe] = useState<Me | null>(null)
  const [moduleData, setModuleData] = useState<EngineeringModule | null>(null)
  const [dashboardData, setDashboardData] = useState<DashboardData | null>(null)
  const [activeSectionId, setActiveSectionId] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [searchLoading, setSearchLoading] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    async function load() {
      setLoading(true)
      setError(null)
      try {
        const [meResponse, navigationResponse] = await Promise.all([
          fetch('/api/me', { credentials: 'include' }),
          fetch('/api/navigation', { credentials: 'include' }),
        ])

        if (!meResponse.ok || !navigationResponse.ok) {
          throw new Error(`Engineering module responded ${meResponse.status} / ${navigationResponse.status}.`)
        }

        const meData = (await meResponse.json()) as Me
        const navData = (await navigationResponse.json()) as EngineeringModule
        setMe(meData)
        setModuleData(navData)
        setActiveSectionId((current) => current ?? navData.sections[0]?.id ?? null)
        await loadDashboard('')
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Unable to load the engineering module.')
      } finally {
        setLoading(false)
      }
    }

    void load()
  }, [])

  async function loadDashboard(query: string) {
    setSearchLoading(true)
    try {
      const response = await fetch(`/api/dashboard?query=${encodeURIComponent(query)}`, { credentials: 'include' })
      if (!response.ok) {
        throw new Error(`Engineering dashboard responded ${response.status}.`)
      }
      setDashboardData((await response.json()) as DashboardData)
    } finally {
      setSearchLoading(false)
    }
  }

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

  const activeSection = useMemo(
    () => moduleData?.sections.find((section) => section.id === activeSectionId) ?? moduleData?.sections[0] ?? null,
    [activeSectionId, moduleData],
  )

  const groupedResults = useMemo(() => {
    if (!dashboardData) return []
    return dashboardData.categories
      .map((category) => ({
        category,
        items: dashboardData.results.filter((result) => result.category === category.id),
      }))
      .filter((group) => group.items.length > 0 || !search.trim())
  }, [dashboardData, search])

  useEffect(() => {
    if (!moduleData || activeSectionId !== 'dashboard') return
    const handle = window.setTimeout(() => {
      void loadDashboard(search)
    }, 180)
    return () => window.clearTimeout(handle)
  }, [activeSectionId, moduleData, search])

  return (
    <div className="engineering-shell">
      <aside className="sidebar">
        <a className="brand" href="http://localhost:5140" target="_top" aria-label="Return to SON-AERO Internal Hub">
          <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
        </a>

        <div className="nav-section">
          <div className="nav-heading">
            <span>Engineering Module</span>
            <span className="nav-flag">Testing</span>
          </div>
          <nav aria-label="Engineering pages">
          {moduleData?.sections.map((section) => {
            const Icon = ICONS[section.id] ?? Beaker
            const active = section.id === activeSection?.id
            return (
              <button
                key={section.id}
                type="button"
                className={`nav-button ${active ? 'active' : ''}`.trim()}
                onClick={() => setActiveSectionId(section.id)}
              >
                <span className="nav-icon">
                  <Icon size={17} />
                </span>
                {section.title}
              </button>
            )
          })}
          </nav>
        </div>

        <div className="sidebar-foot">
          <div className="rail-panel">
            <span className="eyebrow">Access boundary</span>
            <div className="secure-flag">
              <ShieldCheck size={14} />
              <span>Admin-only during testing</span>
            </div>
            <p>
              This module is isolated from Project Tracker workflows so we can build engineering features safely before rollout.
            </p>
          </div>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="page-title-block">
            <span className="eyebrow">Standalone workspace</span>
            <h1>{moduleData?.name ?? 'Engineering Module'}</h1>
            <p>{moduleData?.summary ?? 'Loading module overview...'}</p>
          </div>
          <div className="topbar-actions">
            <button
              className="button ghost"
              type="button"
              onClick={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
              aria-label={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? <SunMedium size={15} /> : <Moon size={15} />}
              {theme === 'dark' ? 'Light mode' : 'Dark mode'}
            </button>
            <button className="button ghost" type="button" onClick={() => window.location.reload()}>
              <RefreshCw size={15} /> Refresh
            </button>
            <div className="user-chip" aria-live="polite">
            {me ? (
              <>
                <div className="user-copy">
                  <strong>{me.displayName}</strong>
                  <span>{me.role}</span>
                </div>
                <span className="avatar" title={me.accountName}>
                  {initials(me.displayName)}
                </span>
              </>
            ) : (
              <>
                <div className="user-copy">
                  <strong>Checking access</strong>
                  <span>Loading</span>
                </div>
                <span className="avatar">
                  <LoaderCircle size={16} className="spin" />
                </span>
              </>
            )}
          </div>
          </div>
        </header>

        <div className="main-scroll">
          <div className="view">
            {loading ? (
              <section className="panel skeleton-panel" aria-label="Loading engineering module">
                <div className="skeleton-line lg" />
                <div className="skeleton-line" />
                <div className="skeleton-line" style={{ width: '68%' }} />
              </section>
            ) : error ? (
              <section className="panel state-error" role="alert">
                <Lock size={20} />
                <div>
                  <strong>Couldn’t open the engineering module</strong>
                  <p>{error}</p>
                </div>
              </section>
            ) : activeSection ? (
              <>
                <section className="panel engineering-hero">
                  <div className="panel-head">
                    <div className="panel-head-text">
                      <span className="eyebrow">{moduleData?.accessNotice}</span>
                      <h2>{activeSection.title}</h2>
                      <p>{activeSection.summary}</p>
                    </div>
                    <div className="hero-callout">
                      <span className="eyebrow">{PAGE_NOTES[activeSection.id]?.label ?? 'Testing note'}</span>
                      <p>{PAGE_NOTES[activeSection.id]?.detail ?? 'Structure in place for the next build step.'}</p>
                    </div>
                  </div>
                </section>

                {activeSection.id === 'drawing-document-control' ? (
                  <DrawingControl />
                ) : activeSection.id === 'dashboard' ? (
                  <>
                    <section className="panel dashboard-search-panel">
                      <div className="panel-head compact">
                        <div className="panel-head-text">
                          <span className="eyebrow">Global search</span>
                          <h2>Engineering record lookup</h2>
                          <p>{dashboardData?.searchHint ?? 'Loading search guidance...'}</p>
                        </div>
                      </div>
                      <label className="topbar-search engineering-search" aria-label="Search engineering records">
                        <Search size={15} />
                        <input
                          value={search}
                          onChange={(event) => setSearch(event.target.value)}
                          placeholder="Search part, tool, drawing, compound, customer, spec, work order, report, or notes"
                        />
                      </label>
                    </section>

                    <section className="kpi-row">
                      {(dashboardData?.categories ?? []).map((category) => (
                        <article key={category.id} className="kpi tone-steel">
                          <div className="kpi-top">
                            <span className="kpi-label">{category.title}</span>
                            <span className="kpi-icon"><Boxes size={18} /></span>
                          </div>
                          <div className="kpi-value">{category.count}</div>
                          <div className="kpi-hint">Indexed engineering records</div>
                        </article>
                      ))}
                    </section>

                    <section className="dashboard-results">
                      {searchLoading ? (
                        <section className="panel skeleton-panel">
                          <div className="skeleton-line lg" />
                          <div className="skeleton-line" />
                          <div className="skeleton-line" style={{ width: '75%' }} />
                        </section>
                      ) : groupedResults.length > 0 ? (
                        groupedResults.map((group) => (
                          <article key={group.category.id} className="panel results-group">
                            <div className="panel-head compact">
                              <div className="panel-head-text">
                                <span className="eyebrow">{group.category.title}</span>
                                <h2>{group.items.length} result{group.items.length === 1 ? '' : 's'}</h2>
                              </div>
                            </div>
                            <div className="results-list">
                              {group.items.map((item) => (
                                <div key={item.id} className="result-card">
                                  <div className="result-head">
                                    <div>
                                      <strong>{item.title}</strong>
                                      <span className="result-id">{item.identifier}</span>
                                    </div>
                                    <span className="result-category">{item.categoryLabel}</span>
                                  </div>
                                  <p className="result-subtitle">{item.subtitle}</p>
                                  <dl className="result-meta">
                                    {item.customer && <div><dt>Customer</dt><dd>{item.customer}</dd></div>}
                                    {item.specificationNumber && <div><dt>Spec</dt><dd>{item.specificationNumber}</dd></div>}
                                    {item.workOrder && <div><dt>Work order</dt><dd>{item.workOrder}</dd></div>}
                                    {item.reportNumber && <div><dt>Report</dt><dd>{item.reportNumber}</dd></div>}
                                  </dl>
                                  <div className="token-list">
                                    {item.tags.map((tag) => <span key={tag} className="token-chip">{tag}</span>)}
                                  </div>
                                  <p className="result-note">{item.note}</p>
                                </div>
                              ))}
                            </div>
                          </article>
                        ))
                      ) : (
                        <section className="panel empty-search-state">
                          <strong>No engineering records matched this search</strong>
                          <p>Try a part number, tool number, drawing number, compound name, customer, work order, report number, or note keyword.</p>
                        </section>
                      )}
                    </section>
                  </>
                ) : (
                  <>
                <section className="kpi-row">
                  <article className={`kpi ${SECTION_TONES[activeSection.id] ?? 'tone-steel'}`}>
                    <div className="kpi-top">
                      <span className="kpi-label">Workspace areas</span>
                      <span className="kpi-icon"><Boxes size={18} /></span>
                    </div>
                    <div className="kpi-value">{activeSection.highlights.length}</div>
                    <div className="kpi-hint">Initial work surfaces staged for this page</div>
                  </article>
                  <article className="kpi tone-ink">
                    <div className="kpi-top">
                      <span className="kpi-label">Module boundary</span>
                      <span className="kpi-icon"><ShieldCheck size={18} /></span>
                    </div>
                    <div className="kpi-value">1</div>
                    <div className="kpi-hint">Standalone application boundary from Project Tracker</div>
                  </article>
                  <article className="kpi tone-ok">
                    <div className="kpi-top">
                      <span className="kpi-label">Access tier</span>
                      <span className="kpi-icon"><ShieldCheck size={18} /></span>
                    </div>
                    <div className="kpi-value">Admin</div>
                    <div className="kpi-hint">Visible only to admins while testing is active</div>
                  </article>
                </section>

                <section className="engineering-grid">
                  <article className="panel">
                    <div className="panel-head compact">
                      <div className="panel-head-text">
                        <span className="eyebrow">Page structure</span>
                        <h2>Initial workspace areas</h2>
                      </div>
                    </div>
                    <div className="area-list">
                      {activeSection.highlights.map((item, index) => (
                        <div key={item} className="area-row">
                          <span className="area-index">{index + 1}</span>
                          <div className="area-copy">
                            <strong>{item}</strong>
                            <p>Placeholder surface created for this engineering page. Data models and workflows can plug in here next.</p>
                          </div>
                          <ChevronRight size={16} className="area-go" />
                        </div>
                      ))}
                    </div>
                  </article>

                  <article className="panel">
                    <div className="panel-head compact">
                      <div className="panel-head-text">
                        <span className="eyebrow">Isolation status</span>
                        <h2>Testing controls in place</h2>
                      </div>
                    </div>
                    <div className="status-bar">
                      <div className="status-bar-track">
                        <span className="status-seg complete" style={{ width: '34%' }} />
                        <span className="status-seg on-track" style={{ width: '33%' }} />
                        <span className="status-seg behind" style={{ width: '33%' }} />
                      </div>
                      <div className="status-bar-legend">
                        <span className="status-bar-key"><b>Separate app</b> no project endpoint calls</span>
                        <span className="status-bar-key"><b>Admin only</b> hidden from non-admin users</span>
                        <span className="status-bar-key"><b>Testing mode</b> safe for iteration</span>
                      </div>
                    </div>
                  </article>
                </section>
                  </>
                )}
              </>
            ) : null}
          </div>
        </div>
      </main>
    </div>
  )
}
