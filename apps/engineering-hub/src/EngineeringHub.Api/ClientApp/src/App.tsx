import { useEffect, useMemo, useState } from 'react'
import {
  ArrowLeft,
  Beaker,
  Boxes,
  ChevronRight,
  FileStack,
  FlaskConical,
  LoaderCircle,
  Lock,
  RefreshCw,
  ShieldCheck,
  Wrench,
} from 'lucide-react'
import './index.css'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import DrawingDashboard from './DrawingDashboard'
import DrawingWorkspace from './DrawingWorkspace'
import EngineeringDashboard from './EngineeringDashboard'
import type { EngineeringSearchResult } from './EngineeringDashboard'

const hubUrl = import.meta.env.VITE_HUB_URL ?? `${window.location.protocol}//${window.location.hostname}:5140`

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

export default function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [me, setMe] = useState<Me | null>(null)
  const [moduleData, setModuleData] = useState<EngineeringModule | null>(null)
  const [activeSectionId, setActiveSectionId] = useState<string | null>(null)
  const [drawingScreen, setDrawingScreen] = useState<'dashboard' | 'record'>('dashboard')
  const [drawingId, setDrawingId] = useState<number | null>(null)
  const [creatingDrawing, setCreatingDrawing] = useState(false)
  const [selectedModuleRecord, setSelectedModuleRecord] = useState<EngineeringSearchResult | null>(null)
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
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Unable to load the engineering module.')
      } finally {
        setLoading(false)
      }
    }

    void load()
  }, [])

  useEffect(() => {
    persistTheme(theme)
  }, [theme])

  useEffect(() => {
    const applyDrawingRoute = () => {
      const route = window.location.hash.replace(/^#\/?/, '')
      if (route === 'drawing-record/new') {
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('record')
        setDrawingId(null)
        setCreatingDrawing(true)
        return
      }
      if (route === 'drawing-record') {
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('record')
        setDrawingId(null)
        setCreatingDrawing(false)
        return
      }
      const match = route.match(/^drawing-record\/(\d+)$/)
      if (match) {
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('record')
        setDrawingId(Number(match[1]))
        setCreatingDrawing(false)
      } else if (route === 'drawing-dashboard') {
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('dashboard')
        setDrawingId(null)
        setCreatingDrawing(false)
      }
    }
    applyDrawingRoute()
    window.addEventListener('hashchange', applyDrawingRoute)
    return () => window.removeEventListener('hashchange', applyDrawingRoute)
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

  const activeSection = useMemo(
    () => moduleData?.sections.find((section) => section.id === activeSectionId) ?? moduleData?.sections[0] ?? null,
    [activeSectionId, moduleData],
  )

  function openDrawingDashboard() {
    setSelectedModuleRecord(null)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('dashboard')
    setDrawingId(null)
    setCreatingDrawing(false)
    window.location.hash = 'drawing-dashboard'
  }

  function openDrawingRecord(id: number) {
    setSelectedModuleRecord(null)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('record')
    setDrawingId(id)
    setCreatingDrawing(false)
    window.location.hash = `drawing-record/${id}`
  }

  function openDrawingEditor() {
    setSelectedModuleRecord(null)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('record')
    setDrawingId(null)
    setCreatingDrawing(false)
    window.location.hash = 'drawing-record'
  }

  function openDrawingCreation() {
    setSelectedModuleRecord(null)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('record')
    setDrawingId(null)
    setCreatingDrawing(true)
    window.location.hash = 'drawing-record/new'
  }

  function openEngineeringResult(result: EngineeringSearchResult) {
    if (result.drawingId) {
      setSelectedModuleRecord(null)
      openDrawingRecord(result.drawingId)
      return
    }

    setSelectedModuleRecord(result)
    if (result.category === 'tools') {
      setActiveSectionId('tooling-management')
      window.location.hash = `tooling-record/${result.id}`
    } else if (result.category === 'compounds' || result.category === 'test-reports') {
      setActiveSectionId('compound-test-data-management')
      window.location.hash = `compound-record/${result.id}`
    } else {
      openDrawingEditor()
    }
  }

  return (
    <div className="engineering-shell engineering-app">
      <aside className="sidebar">
        <div className="brand">
          <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
        </div>
        <a className="hub-return" href={hubUrl} target="_top">
          <ArrowLeft size={15} />
          <span>
            <strong>All Applications</strong>
            <small>Return to Son-Aero Hub</small>
          </span>
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
              <div className="engineering-nav-item" key={section.id}>
                <button
                  type="button"
                  className={`nav-button ${active ? 'active' : ''}`.trim()}
                  onClick={() => {
                    if (section.id === 'drawing-document-control') openDrawingDashboard()
                    else {
                      setSelectedModuleRecord(null)
                      setActiveSectionId(section.id)
                      window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}`)
                    }
                  }}
                >
                  <span className="nav-icon">
                    <Icon size={17} />
                  </span>
                  {section.title}
                </button>
                {section.id === 'drawing-document-control' && active && <div className="drawing-subnav">
                  <button type="button" className={drawingScreen === 'dashboard' ? 'active' : ''} onClick={openDrawingDashboard}>Drawing register</button>
                  <button type="button" className={drawingScreen === 'record' ? 'active' : ''} onClick={() => drawingId ? openDrawingRecord(drawingId) : openDrawingEditor()}>Record editor</button>
                </div>}
              </div>
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
            <a className="topbar-hub" href={hubUrl} target="_top">
              <ArrowLeft size={15} /> All Applications
            </a>
            <ThemeSwitch
              theme={theme}
              onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
            />
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
                      <h2>{activeSection.id === 'drawing-document-control'
                        ? drawingScreen === 'dashboard' ? 'Drawing register' : 'Drawing record editor'
                        : activeSection.title}</h2>
                      <p>{activeSection.summary}</p>
                    </div>
                    <div className="hero-callout">
                      <span className="eyebrow">{PAGE_NOTES[activeSection.id]?.label ?? 'Testing note'}</span>
                      <p>{PAGE_NOTES[activeSection.id]?.detail ?? 'Structure in place for the next build step.'}</p>
                    </div>
                  </div>
                </section>

                {activeSection.id === 'drawing-document-control' ? (
                  drawingScreen === 'dashboard' ? (
                    <DrawingDashboard onEditDrawing={openDrawingRecord} onCreateDrawing={openDrawingCreation}/>
                  ) : (
                    <DrawingWorkspace
                      drawingId={drawingId}
                      initialCreate={creatingDrawing}
                      onOpenDrawing={openDrawingRecord}
                      onBackToDashboard={openDrawingDashboard}
                    />
                  )
                ) : activeSection.id === 'dashboard' ? (
                  <EngineeringDashboard
                    onOpenDrawing={openDrawingRecord}
                    onOpenResult={openEngineeringResult}
                  />
                ) : (
                  <>
                {selectedModuleRecord && (
                  (activeSection.id === 'tooling-management' && selectedModuleRecord.category === 'tools') ||
                  (activeSection.id === 'compound-test-data-management' && ['compounds', 'test-reports'].includes(selectedModuleRecord.category))
                ) && <section className="panel module-record-route">
                  <div>
                    <span className="eyebrow">{selectedModuleRecord.categoryLabel} record editor</span>
                    <h2><span className="technical-id">{selectedModuleRecord.identifier}</span> · {selectedModuleRecord.title}</h2>
                    <p>{selectedModuleRecord.subtitle}</p>
                  </div>
                  <dl>
                    <div><dt>Customer</dt><dd>{selectedModuleRecord.customer ?? 'Not assigned'}</dd></div>
                    <div><dt>Specification</dt><dd>{selectedModuleRecord.specificationNumber ?? 'Not linked'}</dd></div>
                    <div><dt>Work order</dt><dd>{selectedModuleRecord.workOrder ?? 'Not linked'}</dd></div>
                    <div><dt>Record type</dt><dd>{selectedModuleRecord.categoryLabel}</dd></div>
                  </dl>
                  <p className="module-record-notice">This is the owning record page. The tooling and compound-specific edit forms will be added as those modules are built out.</p>
                </section>}
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
