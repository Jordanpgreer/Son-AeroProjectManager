import '../App.css'
import type { ReactNode } from 'react'
import {
  Archive,
  CalendarRange,
  ChevronDown,
  Check,
  FileSpreadsheet,
  FileText,
  History,
  LayoutDashboard,
  ListChecks,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Settings2,
  UploadCloud,
} from 'lucide-react'
import {
  hubUrl,
  screenEyebrow,
  screenTitle,
  screenSubtitle,
} from '../lib'
import type {
  Screen,
  User,
  ProjectDetail,
} from '../types'

function hasPermission(user: User | null, permission: string) {
  return Boolean(user?.permissions?.includes(permission))
}

export function Sidebar({
  screen,
  setScreen,
  selectedProject,
  hasActiveProjects,
  onOpenActiveProjects,
  user,
}: {
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  hasActiveProjects: boolean
  onOpenActiveProjects: () => Promise<void>
  user: User | null
}) {
  return (
    <aside className="sidebar">
      <a className="brand" href={hubUrl} target="_top" aria-label="Return to SON-AERO Internal Hub">
        <img src="/brand/son-aero-lockup-dark.png" alt="Son-Aero — Sonfarrel Aerospace" />
      </a>

      <div className="nav-section">
        <span className="nav-heading">Program Control</span>
        <nav aria-label="Primary">
          <NavButton active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />
          <NavButton active={screen === 'project' && selectedProject?.status !== 'Complete'} onClick={() => void onOpenActiveProjects()} icon={<ListChecks size={17} />} label="Project Detail" disabled={!hasActiveProjects} />
          <NavButton active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />
          <NavButton active={screen === 'pastProjects' || (screen === 'project' && selectedProject?.status === 'Complete')} onClick={() => setScreen('pastProjects')} icon={<Archive size={17} />} label="Past Projects" />
        </nav>
      </div>

      {(hasPermission(user, 'settings.workCalendar.manage')
        || hasPermission(user, 'settings.holidays.manage')
        || hasPermission(user, 'settings.workCenters.manage')
        || hasPermission(user, 'access.manageUsers')
        || hasPermission(user, 'access.manageGroups')
        || hasPermission(user, 'archived.restore')
        || hasPermission(user, 'import.manage')) && (
        <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Secondary">
            {(hasPermission(user, 'settings.workCalendar.manage')
              || hasPermission(user, 'settings.holidays.manage')
              || hasPermission(user, 'settings.workCenters.manage')
              || hasPermission(user, 'access.manageUsers')
              || hasPermission(user, 'access.manageGroups')
              || hasPermission(user, 'archived.restore')) && (
              <NavButton active={screen === 'settings'} onClick={() => setScreen('settings')} icon={<Settings2 size={17} />} label="Settings" />
            )}
            {hasPermission(user, 'import.manage') && (
              <NavButton active={screen === 'import'} onClick={() => setScreen('import')} icon={<UploadCloud size={17} />} label="Imports / Admin" />
            )}
          </nav>
        </div>
      )}
    </aside>
  )
}

export function NavButton({
  active,
  onClick,
  icon,
  label,
  disabled,
}: {
  active: boolean
  onClick: () => void
  icon: ReactNode
  label: string
  disabled?: boolean
}) {
  return (
    <button className={`nav-button ${active ? 'active' : ''}`} onClick={onClick} disabled={disabled}>
      <span className="nav-icon">{icon}</span>
      {label}
    </button>
  )
}


export function PageHeader({
  screen,
  selectedProject,
  canEdit,
  editMode,
  hasUnsavedChanges,
  onToggleEdit,
  dashboardSearch,
  setDashboardSearch,
  pastProjectsSearch,
  setPastProjectsSearch,
  refresh,
  onAddProject,
  onOpenActivity,
}: {
  screen: Screen
  selectedProject: ProjectDetail | null
  canEdit: boolean
  editMode: boolean
  hasUnsavedChanges: boolean
  onToggleEdit: () => void
  dashboardSearch: string
  setDashboardSearch: (value: string) => void
  pastProjectsSearch: string
  setPastProjectsSearch: (value: string) => void
  refresh: () => Promise<void>
  onAddProject: () => void
  onOpenActivity: () => void
}) {
  const portfolioExports = screen === 'dashboard'
  const pastProjectExports = screen === 'pastProjects'
  const projectId = selectedProject?.id
  const xlsxHref = portfolioExports ? '/api/reports/portfolio.xlsx' : pastProjectExports ? '/api/reports/past-projects.xlsx' : `/api/reports/projects/${projectId}.xlsx`
  const pdfHref = portfolioExports ? '/api/reports/portfolio.pdf' : pastProjectExports ? '/api/reports/past-projects.pdf' : `/api/reports/projects/${projectId}.pdf`
  const showExports = screen === 'dashboard' || screen === 'project' || screen === 'pastProjects'
  const subtitle = screenSubtitle(screen)

  return (
    <header className="topbar">
      <div className="page-title-block">
        <span className="eyebrow">{screenEyebrow(screen)}</span>
        <div className="page-title-row">
          <h1>{screenTitle(screen, selectedProject)}</h1>
          {screen === 'project' && selectedProject && (
            <button className="button ghost page-activity-button" type="button" onClick={onOpenActivity}>
              <History size={15} /> Activity
            </button>
          )}
        </div>
        {subtitle && <p>{subtitle}</p>}
      </div>
      <div className="topbar-actions">
        <button className="button ghost" onClick={refresh} title="Reload tracker data">
          <RefreshCw size={15} /> Refresh
        </button>
        {screen === 'project' && canEdit && selectedProject && selectedProject.status !== 'Complete' && (
          <button className={`button ${editMode ? 'primary' : 'ghost'} ${hasUnsavedChanges ? 'has-unsaved-changes' : ''}`} onClick={onToggleEdit} title={editMode && hasUnsavedChanges ? 'Review unsaved project details before leaving edit mode' : 'Edit the operation grid inline'}>
            {editMode ? <><Check size={15} /> Done{hasUnsavedChanges && <span className="button-dirty-dot" aria-label="Unsaved project details" />}</> : <><Pencil size={15} /> Edit</>}
          </button>
        )}
        {showExports && (
          <details className="export-menu">
            <summary className="button ghost">
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list">
              <a href={xlsxHref}><FileSpreadsheet size={15} /> XLSX</a>
              <a href={pdfHref}><FileText size={15} /> PDF</a>
            </div>
          </details>
        )}
        {screen === 'dashboard' && (
          <>
            <label className="topbar-search" aria-label="Search dashboard programs">
              <Search size={15} />
              <input
                value={dashboardSearch}
                onChange={(event) => setDashboardSearch(event.target.value)}
                placeholder="Search part, sales order, or customer"
              />
            </label>
            {canEdit && <button className="button primary" onClick={onAddProject}><Plus size={15} /> Add Project</button>}
          </>
        )}
        {screen === 'pastProjects' && (
          <label className="topbar-search" aria-label="Search past projects">
            <Search size={15} />
            <input
              value={pastProjectsSearch}
              onChange={(event) => setPastProjectsSearch(event.target.value)}
              placeholder="Search completed projects"
            />
          </label>
        )}
      </div>
    </header>
  )
}

/* ---------------------------------------------------------------------- */
/* Dashboard                                                              */
/* ---------------------------------------------------------------------- */
