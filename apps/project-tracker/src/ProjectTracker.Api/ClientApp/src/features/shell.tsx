import { ArdaSidebarPin } from '../ArdaSidebar'
import '../App.css'
import type { ReactNode, HTMLAttributes } from 'react'
import {
  Archive,
  CalendarRange,
  ChevronDown,
  Check,
  FileSpreadsheet,
  FileText,
  GraduationCap,
  History,
  LayoutDashboard,
  ListChecks,
  Pencil,
  Plus,
  Search,
  Settings2,
} from 'lucide-react'
import {
  hubUrl,
  screenEyebrow,
  screenTitle,
  screenSubtitle,
  userInitials,
} from '../lib'
import type {
  Screen,
  User,
  ProjectDetail,
  MentionNotification,
} from '../types'
import type { AppTheme } from '../theme'
import { useTrackerInboxActions } from './TrackerInboxActions'
import { canViewProjectTrackerScreen } from '../permissions'
import ArdaGlobalNavigation from '../ArdaGlobalNavigation'

function hasPermission(user: User | null, permission: string) {
  return Boolean(user?.permissions?.includes(permission))
}

function UserProfile({ user }: { user: User | null }) {
  const avatarLabel = user
    ? userInitials(user.displayName).padEnd(2, user.displayName.slice(1, 2).toUpperCase())
    : '...'

  return (
    <div
      className="topbar-user-chip"
      aria-label={user ? `Signed in as ${user.displayName}` : 'Checking signed-in user'}
      aria-live="polite"
    >
      <div className="topbar-user-copy">
        <strong>{user?.displayName ?? 'Checking access'}</strong>
      </div>
      <span className="topbar-user-avatar" title={user?.displayName} aria-hidden="true">
        {avatarLabel}
      </span>
    </div>
  )
}

export function Sidebar({
  collapsed,
  onToggleCollapsed,
  pinned = !collapsed,
  sidebarProps,
  screen,
  setScreen,
  selectedProject,
  hasActiveProjects,
  onOpenActiveProjects,
  user,
  trainingMode = false,
}: {
  collapsed: boolean
  onToggleCollapsed: () => void
  pinned?: boolean
  sidebarProps?: HTMLAttributes<HTMLElement>
  screen: Screen
  setScreen: (screen: Screen) => void
  selectedProject: ProjectDetail | null
  hasActiveProjects: boolean
  onOpenActiveProjects: () => Promise<void>
  user: User | null
  trainingMode?: boolean
}) {
  const permissions = user?.permissions ?? []
  const renderNotificationActions = useTrackerInboxActions(user)

  return (
    <aside {...sidebarProps} className="sidebar" id="project-tracker-sidebar" data-guide-id={trainingMode ? 'main-navigation' : undefined}>
      {trainingMode ? <div className="brand brand-hub-link training-brand-static" aria-label="Arda Project Tracker training">
        <img className="brand-lockup brand-lockup-standard" src="/brand/arda-lockup.png" alt="" />
        <img className="brand-lockup brand-lockup-reversed" src="/brand/arda-lockup-reversed.png" alt="" />
        <img className="brand-mark brand-mark-standard" src="/brand/arda-mark.png" alt="" />
        <img className="brand-mark brand-mark-reversed" src="/brand/arda-mark-reversed.png" alt="" />
      </div> : <a
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
      </a>}

<ArdaGlobalNavigation
        currentApplicationId="project-tracker"
        renderNotificationActions={renderNotificationActions}
        hubUrl={hubUrl}
        disabled={trainingMode || Boolean(user?.preview?.readOnly)}
        currentPages={<nav aria-label="Project Tracker pages">
          {canViewProjectTrackerScreen(permissions, 'dashboard') && <NavButton guideId={trainingMode ? 'nav-dashboard' : undefined} active={screen === 'dashboard'} onClick={() => setScreen('dashboard')} icon={<LayoutDashboard size={17} />} label="Dashboard" />}
          {canViewProjectTrackerScreen(permissions, 'project') && <NavButton guideId={trainingMode ? 'nav-project' : undefined} active={screen === 'project' && selectedProject?.status !== 'Complete'} onClick={() => void onOpenActiveProjects()} icon={<ListChecks size={17} />} label="Project Detail" disabled={!hasActiveProjects} />}
          {canViewProjectTrackerScreen(permissions, 'calendar') && <NavButton guideId={trainingMode ? 'nav-calendar' : undefined} active={screen === 'calendar'} onClick={() => setScreen('calendar')} icon={<CalendarRange size={17} />} label="Calendar" />}
          {canViewProjectTrackerScreen(permissions, 'pastProjects') && <NavButton guideId={trainingMode ? 'nav-past' : undefined} active={screen === 'pastProjects' || (screen === 'project' && selectedProject?.status === 'Complete')} onClick={() => setScreen('pastProjects')} icon={<Archive size={17} />} label="Past Projects" />}
        </nav>}
      />

      {user?.isAdmin && (
        <div className="sidebar-foot">
          <nav className="foot-nav" aria-label="Secondary">
            <a
              className="nav-button"
              href={`${hubUrl.replace(/\/+$/, '')}/#/admin/access`}
              target="_top"
              aria-label="Hub Admin / Admin settings"
              title="Hub Admin / Admin settings"
            >
              <span className="nav-icon"><Settings2 size={17} /></span>
              <span className="nav-label">Hub Admin / Admin settings</span>
            </a>
          </nav>
        </div>
      )}
      <ArdaSidebarPin pinned={pinned} onToggle={onToggleCollapsed} />
    </aside>
  )
}

export function NavButton({
  active,
  onClick,
  icon,
  label,
  disabled,
  guideId,
}: {
  active: boolean
  onClick: () => void
  icon: ReactNode
  label: string
  disabled?: boolean
  guideId?: string
}) {
  return (
    <button
      className={`nav-button ${active ? 'active' : ''}`}
      onClick={onClick}
      disabled={disabled}
      aria-current={active ? 'page' : undefined}
      title={label}
      data-guide-id={guideId}
    >
      <span className="nav-icon">{icon}</span>
      <span className="nav-label">{label}</span>
    </button>
  )
}

export function ThemeSwitch({
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


export function PageHeader({
  theme,
  onToggleTheme,
  screen,
  selectedProject,
  canEnterProjectEdit,
  canCreateProject,
  editMode,
  hasUnsavedChanges,
  onToggleEdit,
  dashboardSearch,
  setDashboardSearch,
  pastProjectsSearch,
  setPastProjectsSearch,
  onAddProject,
  onOpenActivity,
  onStartTour,
  user,
}: {
  theme: AppTheme
  onToggleTheme: () => void
  screen: Screen
  selectedProject: ProjectDetail | null
  canEnterProjectEdit: boolean
  canCreateProject: boolean
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
  onStartTour: () => void
  user: User | null
  onOpenNotification: (notification: MentionNotification) => Promise<void>
}) {
  const portfolioExports = screen === 'dashboard'
  const pastProjectExports = screen === 'pastProjects'
  const projectId = selectedProject?.id
  const xlsxHref = portfolioExports ? '/api/reports/portfolio.xlsx' : pastProjectExports ? '/api/reports/past-projects.xlsx' : `/api/reports/projects/${projectId}.xlsx`
  const pdfHref = portfolioExports ? '/api/reports/portfolio.pdf' : pastProjectExports ? '/api/reports/past-projects.pdf' : `/api/reports/projects/${projectId}.pdf`
  const customerPdfHref = `/api/reports/projects/${projectId}/customer.pdf`
  const showExports = screen === 'dashboard' || screen === 'project' || screen === 'pastProjects'
  const showCustomerExport = screen === 'project' && projectId !== undefined
  const showTraining = Boolean(user?.walkthroughEnabled && user.permissions.includes('module.view') && !user.preview)
  const eyebrow = screenEyebrow(screen)
  const subtitle = screenSubtitle(screen)

  return (
    <header className="topbar">
      <div className="topbar-title-area">
        <div className="page-title-block">
          {eyebrow && <span className="eyebrow">{eyebrow}</span>}
          <div className="page-title-row">
            <h1 className={screen === 'project' ? 'technical-id' : undefined}>{screenTitle(screen, selectedProject)}</h1>
            {screen === 'project' && selectedProject && hasPermission(user, 'project.activity.view') && (
              <button className="button ghost page-activity-button" data-benny-target="project-activity" type="button" onClick={onOpenActivity}>
                <History size={15} /> Activity
              </button>
            )}
          </div>
          {subtitle && <p>{subtitle}</p>}
        </div>
      </div>
      <div className="topbar-actions">
        {screen === 'dashboard' && (
          <label className={`topbar-search topbar-live-filter ${dashboardSearch.trim() ? 'is-active' : ''}`} data-benny-target="dashboard-search" aria-label="Search and live-filter dashboard projects">
            <Search size={15} aria-hidden="true" />
            <input
              value={dashboardSearch}
              onChange={(event) => setDashboardSearch(event.target.value)}
              placeholder="Search part, sales order, job, or customer"
            />
            {dashboardSearch.trim() && <span className="live-filter-indicator" aria-hidden="true">Live</span>}
          </label>
        )}
        {screen === 'pastProjects' && (
          <label className="topbar-search" data-benny-target="past-search" aria-label="Search past projects">
            <Search size={15} aria-hidden="true" />
            <input
              value={pastProjectsSearch}
              onChange={(event) => setPastProjectsSearch(event.target.value)}
              placeholder="Search completed projects"
            />
          </label>
        )}
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
        <div className="topbar-identity">
          <ThemeSwitch theme={theme} onToggleTheme={onToggleTheme} />
          <UserProfile user={user} />
        </div>
        {showTraining && (
          <button
            className="button ghost"
            type="button"
            title={`Take the ${screenTitle(screen, selectedProject)} tour with fictional training data`}
            onClick={onStartTour}
          >
            <GraduationCap size={15} /> Tour
          </button>
        )}
        {screen === 'project' && canEnterProjectEdit && selectedProject && selectedProject.status !== 'Complete' && (
          <button className={`button ${editMode ? 'primary' : 'ghost'} ${hasUnsavedChanges ? 'has-unsaved-changes' : ''}`} data-benny-target="project-edit" onClick={onToggleEdit} title={editMode && hasUnsavedChanges ? 'Review unsaved project details before leaving edit mode' : 'Edit the operation grid inline'}>
            {editMode ? <><Check size={15} /> Done{hasUnsavedChanges && <span className="button-dirty-dot" aria-label="Unsaved project details" />}</> : <><Pencil size={15} /> Edit</>}
          </button>
        )}
        {showExports && (
          <details className="export-menu">
            <summary className="button ghost" data-benny-target="exports">
              Export <ChevronDown size={15} />
            </summary>
            <div className="export-menu-list">
              <a href={xlsxHref}><FileSpreadsheet size={15} /> XLSX</a>
              <a href={pdfHref}><FileText size={15} /> PDF</a>
              {showCustomerExport && (
                <a href={customerPdfHref} title="Customer-facing schedule without internal detail">
                  <FileText size={15} /> Customer PDF
                </a>
              )}
            </div>
          </details>
        )}
        {screen === 'dashboard' && canCreateProject && <button className="button primary" data-benny-target="add-project" onClick={onAddProject}><Plus size={15} /> Add Project</button>}
      </div>
    </header>
  )
}

/* ---------------------------------------------------------------------- */
/* Dashboard                                                              */
/* ---------------------------------------------------------------------- */
