import { useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'
import {
  ArrowLeft,
  Boxes,
  CalendarDays,
  Calculator,
  Database,
  ExternalLink,
  Factory,
  GanttChart,
  LockKeyhole,
  Settings2,
  ShieldCheck,
  UploadCloud,
  Users,
} from 'lucide-react'
import AccessPanel from './AccessPanel'
import AccessPreviewPanel from './AccessPreviewPanel'
import EngineeringAccessPanel from './EngineeringAccessPanel'
import ModuleAccessPanel from './ModuleAccessPanel'
import { projectTrackerUrl, toErrorMessage, trackerApi } from './api'
import { ImportsPanel } from './ProjectTrackerDataPanels'
import {
  HolidaysPanel,
  WorkCalendarPanel,
  WorkCentersPanel,
} from './ProjectTrackerSettingsPanels'
import type {
  AdminModuleKey,
  ProjectTrackerAdminSection,
  ProjectTrackerUser,
  AdminAccessPreviewTarget,
} from './types'
import './admin.css'
import './admin-responsive.css'

interface AdminRoute {
  module: AdminModuleKey
  section: string
}

const PERMISSIONS = {
  manageUsers: 'access.manageUsers',
  manageGroups: 'access.manageGroups',
  calendar: 'settings.workCalendar.manage',
  workCenters: 'settings.workCenters.manage',
  holidays: 'settings.holidays.manage',
  imports: 'import.manage',
} as const

const MODULES: {
  key: AdminModuleKey
  label: string
  description: string
  icon: typeof Settings2
  href: string
  openUrl?: string
}[] = [
  {
    key: 'hub',
    label: 'Hub',
    description: 'Administration directory and module ownership',
    icon: Boxes,
    href: '#/admin/hub/access',
  },
  {
    key: 'project-tracker',
    label: 'Project Tracker',
    description: 'Access, scheduling references, recovery, and imports',
    icon: GanttChart,
    href: '#/admin/project-tracker/access',
    openUrl: projectTrackerUrl,
  },
  {
    key: 'engineering',
    label: 'Engineering',
    description: 'Engineering module administration',
    icon: Database,
    href: '#/admin/engineering/access',
    openUrl: `${window.location.protocol}//${window.location.hostname}:5150`,
  },
  {
    key: 'estimating',
    label: 'Estimating',
    description: 'Estimating module administration',
    icon: Calculator,
    href: '#/admin/estimating/access',
    openUrl: `${window.location.protocol}//${window.location.hostname}:5160`,
  },
]

const PROJECT_TRACKER_SECTIONS: {
  key: ProjectTrackerAdminSection
  label: string
  icon: typeof Settings2
}[] = [
  { key: 'access', label: 'Access', icon: Users },
  { key: 'calendar', label: 'Work Calendar', icon: CalendarDays },
  { key: 'work-centers', label: 'Work Centers', icon: Factory },
  { key: 'holidays', label: 'Holidays', icon: CalendarDays },
  { key: 'imports', label: 'Imports', icon: UploadCloud },
]

function parseRoute(hash = window.location.hash): AdminRoute {
  const path = hash.replace(/^#\/?/, '').split('?')[0]
  const [, rawModule, rawSection] = path.split('/')
  const module = MODULES.some((candidate) => candidate.key === rawModule)
    ? rawModule as AdminModuleKey
    : 'project-tracker'
  const validTrackerSection = PROJECT_TRACKER_SECTIONS.some(
    (candidate) => candidate.key === rawSection,
  )
  return {
    module,
    section: module === 'project-tracker' && validTrackerSection ? rawSection : 'access',
  }
}

function handleTabKeys(event: KeyboardEvent<HTMLElement>, currentHref: string) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  const tabs = [...event.currentTarget.querySelectorAll<HTMLAnchorElement>(
    '[role="tab"]:not([aria-disabled="true"])',
  )]
  if (!tabs.length) return
  const currentIndex = Math.max(0, tabs.findIndex((tab) => tab.hash === currentHref))
  const nextIndex = event.key === 'Home'
    ? 0
    : event.key === 'End'
      ? tabs.length - 1
      : (currentIndex + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
  event.preventDefault()
  tabs[nextIndex].click()
  tabs[nextIndex].focus()
}

function NoAccess({ detail }: { detail: string }) {
  return (
    <section className="admin-surface admin-placeholder" role="alert">
      <span className="admin-placeholder-icon"><LockKeyhole size={25} /></span>
      <h2>No access to this section</h2>
      <p>{detail}</p>
    </section>
  )
}

function HubOverview({
  canPreviewAccess,
  onPreviewAccess,
}: {
  canPreviewAccess: boolean
  onPreviewAccess: (target: AdminAccessPreviewTarget) => void
}) {
  return (
    <section className="admin-surface admin-hub-overview" aria-label="Hub access preview">
      {canPreviewAccess && <AccessPreviewPanel onPreview={onPreviewAccess} />}
    </section>
  )
}

export default function AdminConsole({
  currentAccountName,
  currentPortalRole,
  onPreviewAccess,
}: {
  currentAccountName: string | null
  currentPortalRole: string | null
  onPreviewAccess: (target: AdminAccessPreviewTarget) => void
}) {
  const route = parseRoute()
  const activeModule = MODULES.find((module) => module.key === route.module) ?? MODULES[1]
  const panelHeadingRef = useRef<HTMLHeadingElement>(null)
  const [trackerUser, setTrackerUser] = useState<ProjectTrackerUser | null>(null)
  const [permissionsLoading, setPermissionsLoading] = useState(true)
  const [permissionsError, setPermissionsError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    void trackerApi<ProjectTrackerUser>('/api/me')
      .then((user) => {
        if (active) setTrackerUser(user)
      })
      .catch((cause) => {
        if (active) setPermissionsError(toErrorMessage(cause))
      })
      .finally(() => {
        if (active) setPermissionsLoading(false)
      })
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    const canonical = route.module === 'project-tracker'
      ? `#/admin/project-tracker/${route.section}`
      : `#/admin/${route.module}/access`
    if (window.location.hash.split('?')[0] !== canonical) {
      window.history.replaceState(null, '', canonical)
    }
    document.title = `${activeModule.label} Admin - SON-AERO`
    panelHeadingRef.current?.focus()
  }, [activeModule.label, route.module, route.section])

  const granted = useMemo(
    () => new Set(trackerUser?.permissions ?? []),
    [trackerUser],
  )
  const canManageUsers = granted.has(PERMISSIONS.manageUsers)
  const canManageGroups = granted.has(PERMISSIONS.manageGroups)
  const canOpenAccess = canManageUsers || canManageGroups
  const canOpenSection = (section: ProjectTrackerAdminSection) => {
    if (section === 'access') return canOpenAccess
    if (section === 'calendar') return granted.has(PERMISSIONS.calendar)
    if (section === 'work-centers') return granted.has(PERMISSIONS.workCenters)
    if (section === 'holidays') return granted.has(PERMISSIONS.holidays)
    return granted.has(PERMISSIONS.imports)
  }
  const selectedTrackerSection = route.section as ProjectTrackerAdminSection
  const selectedSectionAllowed = canOpenSection(selectedTrackerSection)
  const Icon = activeModule.icon

  function blockUnauthorizedNavigation(
    event: MouseEvent<HTMLAnchorElement>,
    allowed: boolean,
  ) {
    if (!allowed) event.preventDefault()
  }

  return (
    <main className="portal-main admin-main" id="main-content">
      <a className="admin-back-link" href="#/"><ArrowLeft size={15} /> Back to Applications</a>
      <header className="admin-page-head">
        <div>
          <span className="kicker">Administration</span>
          <h1>Hub Admin</h1>
          <p>Manage module-owned settings from one controlled workspace.</p>
        </div>
        <span className="admin-controlled-badge"><ShieldCheck size={15} /> Permission controlled</span>
      </header>

      <nav className="admin-module-tabs" role="tablist" aria-label="Admin modules" onKeyDown={(event) => handleTabKeys(event, activeModule.href)}>
        {MODULES.map((module) => {
          const ModuleIcon = module.icon
          const selected = module.key === route.module
          return (
            <a role="tab" id={`admin-module-tab-${module.key}`} aria-selected={selected} aria-controls="admin-module-panel" tabIndex={selected ? 0 : -1} className={selected ? 'active' : ''} href={module.href} key={module.key}>
              <ModuleIcon size={18} aria-hidden="true" />
              <span><strong>{module.label}</strong><small>{module.description}</small></span>
            </a>
          )
        })}
      </nav>

      <section className="admin-module-panel" id="admin-module-panel" role="tabpanel" aria-labelledby={`admin-module-tab-${route.module}`}>
        <header className="admin-module-head">
          <span className="admin-module-icon"><Icon size={23} aria-hidden="true" /></span>
          <div><span className="kicker">Module administration</span><h2 ref={panelHeadingRef} tabIndex={-1}>{activeModule.label}</h2><p>{activeModule.description}</p></div>
          {activeModule.openUrl && <a className="ghost-button" href={activeModule.openUrl} target="_top">Open module <ExternalLink size={15} /></a>}
        </header>

        {route.module === 'project-tracker' && (
          <nav className="admin-section-tabs" role="tablist" aria-label="Project Tracker admin sections" onKeyDown={(event) => handleTabKeys(event, `#/admin/project-tracker/${route.section}`)}>
            {PROJECT_TRACKER_SECTIONS.map((section) => {
              const SectionIcon = section.icon
              const selected = section.key === route.section
              const allowed = !permissionsLoading && canOpenSection(section.key)
              return (
                <a
                  key={section.key}
                  role="tab"
                  id={`admin-section-tab-${section.key}`}
                  aria-selected={selected}
                  aria-disabled={!allowed}
                  aria-controls="admin-section-panel"
                  tabIndex={selected && allowed ? 0 : -1}
                  className={`${selected ? 'active' : ''} ${allowed ? '' : 'disabled'}`.trim()}
                  href={`#/admin/project-tracker/${section.key}`}
                  onClick={(event) => blockUnauthorizedNavigation(event, allowed)}
                >
                  <SectionIcon size={15} aria-hidden="true" /> {section.label}
                </a>
              )
            })}
          </nav>
        )}

        <div id="admin-section-panel" role={route.module === 'project-tracker' ? 'tabpanel' : undefined} aria-labelledby={route.module === 'project-tracker' ? `admin-section-tab-${route.section}` : undefined}>
          {route.module === 'hub' && <HubOverview canPreviewAccess={currentPortalRole === 'Admin'} onPreviewAccess={onPreviewAccess} />}
          {route.module === 'project-tracker' && permissionsLoading && <div className="admin-loading" role="status">Checking Project Tracker permissions...</div>}
          {route.module === 'project-tracker' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'project-tracker' && !permissionsLoading && !permissionsError && !selectedSectionAllowed && <NoAccess detail="Your Project Tracker groups do not grant the permission required for this administration section." />}
          {route.module === 'project-tracker' && !permissionsLoading && !permissionsError && selectedSectionAllowed && (
            <>
              {route.section === 'access' && <AccessPanel currentAccountName={trackerUser?.accountName ?? currentAccountName} canManageUsers={canManageUsers} canManageGroups={canManageGroups} />}
              {route.section === 'calendar' && <WorkCalendarPanel />}
              {route.section === 'work-centers' && <WorkCentersPanel />}
              {route.section === 'holidays' && <HolidaysPanel />}
              {route.section === 'imports' && <ImportsPanel />}
            </>
          )}
          {route.module === 'engineering' && (
            <EngineeringAccessPanel currentAccountName={trackerUser?.accountName ?? currentAccountName}/>
          )}
          {route.module === 'estimating' && (
            <ModuleAccessPanel
              moduleKey="estimating"
              moduleName="Estimating"
              currentAccountName={trackerUser?.accountName ?? currentAccountName}
            />
          )}
        </div>
      </section>
    </main>
  )
}
