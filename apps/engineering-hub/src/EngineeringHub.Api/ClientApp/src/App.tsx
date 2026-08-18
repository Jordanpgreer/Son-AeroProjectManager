import { useEffect, useMemo, useState } from 'react'
import {
  ArrowLeft,
  AlertTriangle,
  Archive,
  Beaker,
  Boxes,
  ChevronRight,
  Edit3,
  FileStack,
  FlaskConical,
  History,
  LoaderCircle,
  Lock,
  PanelLeftClose,
  PanelLeftOpen,
  RefreshCw,
  Settings,
  ShieldCheck,
  Wrench,
} from 'lucide-react'
import './index.css'
import { persistTheme, readThemePreference } from './theme'
import type { AppTheme } from './theme'
import DrawingDashboard from './DrawingDashboard'
import DrawingWorkspace from './DrawingWorkspace'
import type { DrawingRecordHeader } from './DrawingWorkspace'
import EngineeringDashboard from './EngineeringDashboard'
import type { EngineeringSearchResult } from './EngineeringDashboard'
import { engineeringPermissionKeys, hasEngineeringPermission } from './permissions'

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
const engineeringAdminUrl = new URL('/#/admin/engineering/file-storage', hubUrl).toString()

interface Me {
  accountName: string
  displayName: string
  role: string
  permissions: string[]
  groups: string[]
  isPreview: boolean
  previewActorAccountName: string | null
  previewTargetKey: string | null
  previewTargetTitle: string | null
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

function shortDate(value: string | null) {
  return value ? new Date(value).toLocaleDateString() : 'Not set'
}

function mylarSummary(drawing: DrawingRecordHeader) {
  if (drawing.mylarCount === 0) return 'Not registered'
  if (drawing.checkedOutMylarCount > 0) {
    return drawing.mylarCheckedOutBy
      ? `Checked out by ${drawing.mylarCheckedOutBy}`
      : 'Checked out'
  }
  return drawing.physicalMylarLocation
    ? `Checked in at ${drawing.physicalMylarLocation}`
    : 'Checked in'
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

function UserProfile({ me, className = '' }: { me: Me | null; className?: string }) {
  return <div className={`user-chip ${className}`.trim()} aria-live="polite">
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
}

export default function App() {
  const [theme, setTheme] = useState(() => readThemePreference())
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return window.localStorage.getItem('sonaero-engineering-sidebar') === 'collapsed'
    } catch {
      return false
    }
  })
  const [me, setMe] = useState<Me | null>(null)
  const [moduleData, setModuleData] = useState<EngineeringModule | null>(null)
  const [activeSectionId, setActiveSectionId] = useState<string | null>(null)
  const [drawingScreen, setDrawingScreen] = useState<'dashboard' | 'record'>('dashboard')
  const [drawingId, setDrawingId] = useState<number | null>(null)
  const [creatingDrawing, setCreatingDrawing] = useState(false)
  const [drawingHeader, setDrawingHeader] = useState<DrawingRecordHeader | null>(null)
  const [drawingEditRequest, setDrawingEditRequest] = useState(0)
  const [drawingArchiveRequest, setDrawingArchiveRequest] = useState(0)
  const [drawingAuditRequest, setDrawingAuditRequest] = useState(0)
  const [selectedModuleRecord, setSelectedModuleRecord] = useState<EngineeringSearchResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const permissions = me?.permissions ?? []
  const can = (permission: string) => hasEngineeringPermission(permissions, permission)
  const canEditMetadata = !me?.isPreview && (can(engineeringPermissionKeys.drawingMetadataEdit) || can(engineeringPermissionKeys.specificationsEdit))

  useEffect(() => {
    try {
      window.localStorage.setItem(
        'sonaero-engineering-sidebar',
        sidebarCollapsed ? 'collapsed' : 'expanded',
      )
    } catch {
      // Sidebar state persistence is optional.
    }
  }, [sidebarCollapsed])

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
        setDrawingHeader(null)
        setDrawingEditRequest(0)
        setDrawingArchiveRequest(0)
        setDrawingAuditRequest(0)
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('record')
        setDrawingId(null)
        setCreatingDrawing(true)
        return
      }
      if (route === 'settings') {
        window.location.replace(engineeringAdminUrl)
        return
      }
      if (route === 'drawing-record') {
        setDrawingHeader(null)
        setDrawingEditRequest(0)
        setDrawingArchiveRequest(0)
        setDrawingAuditRequest(0)
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('dashboard')
        setDrawingId(null)
        setCreatingDrawing(false)
        window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}#drawing-dashboard`)
        return
      }
      const match = route.match(/^drawing-record\/(\d+)$/)
      if (match) {
        setDrawingHeader(null)
        setDrawingEditRequest(0)
        setDrawingArchiveRequest(0)
        setDrawingAuditRequest(0)
        setActiveSectionId('drawing-document-control')
        setDrawingScreen('record')
        setDrawingId(Number(match[1]))
        setCreatingDrawing(false)
      } else if (route === 'drawing-dashboard') {
        setDrawingHeader(null)
        setDrawingEditRequest(0)
        setDrawingArchiveRequest(0)
        setDrawingAuditRequest(0)
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

  const availableSections = useMemo(() => {
    if (!moduleData || !me) return []
    const requiredPermissions: Record<string, string> = {
      dashboard: engineeringPermissionKeys.dashboardView,
      'drawing-document-control': engineeringPermissionKeys.drawingsView,
      'tooling-management': engineeringPermissionKeys.toolingView,
      'compound-test-data-management': engineeringPermissionKeys.compoundDataView,
    }
    const sections = moduleData.sections.filter(section => {
      const required = requiredPermissions[section.id]
      return !required || hasEngineeringPermission(me.permissions, required)
    })
    return sections
  }, [me, moduleData])

  const activeSection = useMemo(
    () => availableSections.find((section) => section.id === activeSectionId) ?? availableSections[0] ?? null,
    [activeSectionId, availableSections],
  )

  function openDrawingDashboard() {
    setSelectedModuleRecord(null)
    setDrawingHeader(null)
    setDrawingEditRequest(0)
    setDrawingArchiveRequest(0)
    setDrawingAuditRequest(0)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('dashboard')
    setDrawingId(null)
    setCreatingDrawing(false)
    window.location.hash = 'drawing-dashboard'
  }

  function openDrawingRecord(id: number) {
    setSelectedModuleRecord(null)
    setDrawingHeader(null)
    setDrawingEditRequest(0)
    setDrawingArchiveRequest(0)
    setDrawingAuditRequest(0)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('record')
    setDrawingId(id)
    setCreatingDrawing(false)
    window.location.hash = `drawing-record/${id}`
  }

  function openDrawingEditor() {
    setSelectedModuleRecord(null)
    setDrawingHeader(null)
    setDrawingEditRequest(0)
    setDrawingArchiveRequest(0)
    setDrawingAuditRequest(0)
    setActiveSectionId('drawing-document-control')
    setDrawingScreen('record')
    setDrawingId(null)
    setCreatingDrawing(false)
    window.location.hash = 'drawing-record'
  }

  function openDrawingCreation() {
    setSelectedModuleRecord(null)
    setDrawingHeader(null)
    setDrawingEditRequest(0)
    setDrawingArchiveRequest(0)
    setDrawingAuditRequest(0)
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

  const showingDrawingRecord = activeSection?.id === 'drawing-document-control' && drawingScreen === 'record'
  const showingDrawingRegister = activeSection?.id === 'drawing-document-control' && drawingScreen === 'dashboard'

  return (
    <div className={`engineering-shell engineering-app ${sidebarCollapsed ? 'is-sidebar-collapsed' : ''} ${me?.isPreview ? 'access-preview-active' : ''}`.trim()}>
      <aside className="sidebar" id="engineering-sidebar">
        <a
          className="brand brand-hub-link"
          href={hubUrl}
          target="_top"
          aria-label="Return to All Applications"
          title="Return to All Applications"
        >
          <img className="brand-lockup" src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
          <img className="brand-mark" src="/brand/son-aero-mark.png" alt="Son-Aero" />
        </a>

        <div className="nav-section">
          <div className="nav-heading">
            <span>Engineering Module</span>
            <span className="nav-flag">Testing</span>
          </div>
          <nav aria-label="Engineering pages">
          {availableSections.map((section) => {
            const Icon = ICONS[section.id] ?? Beaker
            const active = section.id === activeSection?.id
            return (
              <div className="engineering-nav-item" key={section.id}>
                <button
                  type="button"
                  className={`nav-button ${active ? 'active' : ''}`.trim()}
                  aria-current={active ? 'page' : undefined}
                  title={section.title}
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
                  <span className="nav-label">{section.title}</span>
                </button>
              </div>
            )
          })}
          </nav>
        </div>

        {can(engineeringPermissionKeys.settingsView) && <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Engineering administration">
            <a
              className={`nav-button ${me?.isPreview ? 'is-disabled' : ''}`.trim()}
              href={me?.isPreview ? undefined : engineeringAdminUrl}
              target="_top"
              aria-disabled={me?.isPreview || undefined}
              title={me?.isPreview ? 'Return to Admin before changing Engineering settings' : 'Engineering Admin / Settings'}
            >
              <span className="nav-icon"><Settings size={17}/></span>
              <span className="nav-label">Engineering Admin / Settings</span>
            </a>
          </nav>
        </div>}
      </aside>

      <main className="main-area">
        {me?.isPreview && <section className="access-preview-banner" role="status">
          <div>
            <strong>Read-only preview: {me.previewTargetTitle ?? me.displayName}</strong>
            <span>You are seeing the Engineering permissions and records available to this target. Changes are blocked.</span>
          </div>
          <a href="/access-preview/end" target="_top">Return to Admin</a>
        </section>}
        <header className={`topbar ${showingDrawingRecord ? 'drawing-record-topbar' : ''}`.trim()}>
          <div className="topbar-title-area">
            <button
              type="button"
              className="sidebar-toggle"
              aria-label={sidebarCollapsed ? 'Expand Engineering navigation' : 'Collapse Engineering navigation'}
              aria-expanded={!sidebarCollapsed}
              aria-controls="engineering-sidebar"
              title={sidebarCollapsed ? 'Expand navigation' : 'Collapse navigation'}
              onClick={() => setSidebarCollapsed((current) => !current)}
            >
              {sidebarCollapsed
                ? <PanelLeftOpen size={19} aria-hidden="true" />
                : <PanelLeftClose size={19} aria-hidden="true" />}
            </button>
            <div className={`page-title-block ${showingDrawingRecord ? 'drawing-record-page-title' : ''}`.trim()}>
            {showingDrawingRecord ? (
              <>
                <div className="record-header-kicker">
                  <span className="eyebrow">{creatingDrawing ? 'New controlled drawing' : 'Drawing control record'}</span>
                </div>
                <div className="record-header-title-row">
                  <h1 className={drawingHeader ? 'technical-id' : ''}>
                    {drawingHeader?.drawingNumber ?? (creatingDrawing ? 'Create drawing record' : 'Loading drawing record...')}
                  </h1>
                  {drawingHeader && <span className={`status-pill status-${drawingHeader.approvalStatus.toLowerCase()}`}>{drawingHeader.approvalStatus === 'Obsolete' ? 'Archived' : drawingHeader.approvalStatus.replace(/([a-z])([A-Z])/g, '$1 $2')}</span>}
                </div>
                {drawingHeader ? (
                  <>
                    <p className="record-header-title">{drawingHeader.title}</p>
                    {drawingHeader.pendingReviewRevision && <div className="record-review-pending" role="status">
                      <AlertTriangle size={15}/>
                      <span><strong>Review pending</strong>Revision {drawingHeader.pendingReviewRevision} is awaiting engineering disposition.</span>
                    </div>}
                    <dl className="record-header-facts">
                      <div><dt>Design authority</dt><dd>{drawingHeader.customer}</dd></div>
                      <div><dt>Current revision</dt><dd>{drawingHeader.currentRevision ?? 'None'}</dd></div>
                      <div><dt>Effective date</dt><dd>{drawingHeader.effectiveDate ? <time dateTime={drawingHeader.effectiveDate}>{shortDate(drawingHeader.effectiveDate)}</time> : 'Not set'}</dd></div>
                      <div><dt>Linked parts</dt><dd title={drawingHeader.partNumbers.join(', ') || undefined}>{drawingHeader.partNumbers.length ? drawingHeader.partNumbers.join(', ') : 'None'}</dd></div>
                      {can(engineeringPermissionKeys.mylarView) && <div><dt>Physical Mylar</dt><dd title={mylarSummary(drawingHeader)}>{mylarSummary(drawingHeader)}</dd></div>}
                    </dl>
                  </>
                ) : (
                  <p className="record-header-context">
                    {creatingDrawing
                      ? 'Enter the drawing identity, current revision, and optional controlled file.'
                      : 'Retrieving drawing identity and release information.'}
                  </p>
                )}
                <button className="record-header-back" type="button" onClick={openDrawingDashboard}>
                  <ArrowLeft size={15} />
                  <span><small>Return to</small><strong>Drawing register</strong></span>
                </button>
              </>
            ) : (
              <>
                <span className="eyebrow">{showingDrawingRegister ? 'Drawing and document control' : 'Standalone workspace'}</span>
                <h1>{showingDrawingRegister ? 'Drawing Register' : moduleData?.name ?? 'Engineering Module'}</h1>
                <p>{showingDrawingRegister
                  ? 'Search controlled drawings, review release status, and open complete revision records.'
                  : moduleData?.summary ?? 'Loading module overview...'}</p>
              </>
            )}
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
            <div className="topbar-identity">
              <ThemeSwitch
                theme={theme}
                onToggleTheme={() => setTheme((current) => current === 'dark' ? 'light' : 'dark')}
              />
              <UserProfile me={me} className="topbar-user-chip" />
            </div>
            {showingDrawingRecord
              ? drawingHeader && <>
                  {canEditMetadata && !drawingHeader.isObsolete && <button
                    className="button record-header-edit"
                    type="button"
                    aria-expanded={drawingHeader.isMetadataEditing}
                    aria-controls="drawing-metadata-editor"
                    onClick={() => setDrawingEditRequest(current => current + 1)}
                  >
                    <Edit3 size={14}/> {drawingHeader.isMetadataEditing ? 'Close metadata' : 'Edit metadata'}
                  </button>}
                  {can(engineeringPermissionKeys.auditView) && <button className="button ghost record-header-audit" type="button" onClick={() => setDrawingAuditRequest(current => current + 1)}>
                    <History size={14}/> Audit history
                  </button>}
                  {!me?.isPreview && can(engineeringPermissionKeys.drawingArchive) && !drawingHeader.isObsolete && <button className="button ghost record-header-archive" type="button" onClick={() => setDrawingArchiveRequest(current => current + 1)}>
                    <Archive size={14}/> Archive
                  </button>}
                </>
              : <button className="button ghost" type="button" onClick={() => window.location.reload()}>
                  <RefreshCw size={15} /> Refresh
                </button>}
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
                {!showingDrawingRecord && activeSection.id !== 'dashboard' && activeSection.id !== 'drawing-document-control' && <section className="panel engineering-hero">
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
                </section>}

                {activeSection.id === 'drawing-document-control' ? (
                  drawingScreen === 'dashboard' ? (
                    <DrawingDashboard permissions={permissions} onEditDrawing={openDrawingRecord} onCreateDrawing={openDrawingCreation}/>
                  ) : (
                    <DrawingWorkspace
                      drawingId={drawingId}
                      initialCreate={creatingDrawing}
                      onOpenDrawing={openDrawingRecord}
                      onBackToDashboard={openDrawingDashboard}
                      onRecordChange={setDrawingHeader}
                      editRequest={drawingEditRequest}
                      archiveRequest={drawingArchiveRequest}
                      auditRequest={drawingAuditRequest}
                      actorName={me?.accountName || me?.displayName || 'Signed-in user'}
                      permissions={permissions}
                    />
                  )
                ) : activeSection.id === 'dashboard' ? (
                  <EngineeringDashboard
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
                    <div className="kpi-value">{me?.role ?? 'Viewer'}</div>
                    <div className="kpi-hint">Your assigned Engineering module role</div>
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
                        <span className="status-bar-key"><b>Role based</b> Viewer, Editor, and Admin access</span>
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
