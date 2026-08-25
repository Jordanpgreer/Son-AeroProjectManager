import { useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent, MouseEvent } from 'react'
import {
  ArrowLeft,
  Boxes,
  CalendarDays,
  Calculator,
  ClipboardCheck,
  Database,
  ExternalLink,
  Factory,
  FolderTree,
  GanttChart,
  GraduationCap,
  LockKeyhole,
  Settings2,
  ShieldCheck,
  UploadCloud,
  Users,
  Waypoints,
} from 'lucide-react'
import AccessPanel from './AccessPanel'
import AccessPreviewPanel from './AccessPreviewPanel'
import EngineeringStoragePanel from './EngineeringStoragePanel'
import EstimatorSettingsPanel from './EstimatorSettingsPanel'
import QualityAssignmentRulesPanel from './QualityAssignmentRulesPanel'
import WalkthroughSettingsPanel from './WalkthroughSettingsPanel'
import { toErrorMessage, trackerApi } from './api'
import { resolveModuleApplicationUrl } from './moduleUrls'
import { ImportsPanel } from './ProjectTrackerDataPanels'
import {
  HolidaysPanel,
  WorkCalendarPanel,
  WorkCentersPanel,
} from './ProjectTrackerSettingsPanels'
import type {
  AdminModuleKey,
  EngineeringAdminSection,
  ProjectTrackerAdminSection,
  QualityAdminSection,
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
  workCenterImports: 'settings.workCenters.import',
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
    key: 'access',
    label: 'Access',
    description: 'Shared users, groups, and module permissions',
    icon: Users,
    href: '#/admin/access',
  },
  {
    key: 'hub',
    label: 'Arda',
    description: 'Administration directory and module ownership',
    icon: Boxes,
    href: '#/admin/hub/overview',
  },
  {
    key: 'project-tracker',
    label: 'Project Tracker',
    description: 'Scheduling references, recovery, and imports',
    icon: GanttChart,
    href: '#/admin/project-tracker/calendar',
    openUrl: resolveModuleApplicationUrl(window.location, 5135),
  },
  {
    key: 'engineering',
    label: 'Engineering',
    description: 'Engineering module administration',
    icon: Database,
    href: '#/admin/engineering/file-storage',
    openUrl: resolveModuleApplicationUrl(window.location, 5150),
  },
  {
    key: 'estimating',
    label: 'Estimating',
    description: 'Estimating module administration',
    icon: Calculator,
    href: '#/admin/estimating/overview',
    openUrl: resolveModuleApplicationUrl(window.location, 5160),
  },
  {
    key: 'quality-assurance',
    label: 'Quality Assurance',
    description: 'Quality module administration',
    icon: ClipboardCheck,
    href: '#/admin/quality-assurance/assignment-rules',
    openUrl: resolveModuleApplicationUrl(window.location, 5170),
  },
]

const PROJECT_TRACKER_SECTIONS: {
  key: ProjectTrackerAdminSection
  label: string
  icon: typeof Settings2
}[] = [
  { key: 'walkthrough', label: 'Onboarding', icon: GraduationCap },
  { key: 'calendar', label: 'Work Calendar', icon: CalendarDays },
  { key: 'work-centers', label: 'Work Centers', icon: Factory },
  { key: 'holidays', label: 'Holidays', icon: CalendarDays },
  { key: 'imports', label: 'Imports', icon: UploadCloud },
]

const ENGINEERING_SECTIONS: {
  key: EngineeringAdminSection
  label: string
  icon: typeof Settings2
}[] = [
  { key: 'file-storage', label: 'File Storage', icon: FolderTree },
]

const QUALITY_SECTIONS: {
  key: QualityAdminSection
  label: string
  icon: typeof Settings2
}[] = [
  { key: 'assignment-rules', label: 'Assignment Rules', icon: Waypoints },
]

function parseRoute(hash = window.location.hash): AdminRoute {
  const path = hash.replace(/^#\/?/, '').split('?')[0]
  const [, rawModule, rawSection] = path.split('/')
  const module = MODULES.some((candidate) => candidate.key === rawModule)
    ? rawModule as AdminModuleKey
    : 'access'
  const validTrackerSection = PROJECT_TRACKER_SECTIONS.some(
    (candidate) => candidate.key === rawSection,
  )
  const validEngineeringSection = ENGINEERING_SECTIONS.some(
    (candidate) => candidate.key === rawSection,
  )
  const validQualitySection = QUALITY_SECTIONS.some(
    (candidate) => candidate.key === rawSection,
  )
  return {
    module,
    section: module === 'access' || module === 'hub'
      ? 'overview'
      : module === 'project-tracker' && validTrackerSection
        ? rawSection
        : module === 'engineering' && validEngineeringSection
          ? rawSection
          : module === 'quality-assurance' && validQualitySection
            ? rawSection
          : module === 'project-tracker'
            ? 'calendar'
            : module === 'engineering'
              ? 'file-storage'
              : module === 'quality-assurance'
                ? 'assignment-rules'
              : 'overview',
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
  onPreviewWalkthrough,
}: {
  currentAccountName: string | null
  currentPortalRole: string | null
  onPreviewAccess: (target: AdminAccessPreviewTarget) => void
  onPreviewWalkthrough: (target: AdminAccessPreviewTarget) => Promise<void>
}) {
  const route = parseRoute()
  const activeModule = MODULES.find((module) => module.key === route.module) ?? MODULES[0]
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
    const canonical = route.module === 'access'
      ? '#/admin/access'
      : `#/admin/${route.module}/${route.section}`
    if (window.location.hash.split('?')[0] !== canonical) {
      window.history.replaceState(null, '', canonical)
    }
    document.title = `${activeModule.label} Admin · Arda`
    panelHeadingRef.current?.focus()
  }, [activeModule.label, route.module, route.section])

  const granted = useMemo(
    () => new Set(trackerUser?.permissions ?? []),
    [trackerUser],
  )
  const canManageUsers = granted.has(PERMISSIONS.manageUsers)
  const canManageGroups = granted.has(PERMISSIONS.manageGroups)
  const canOpenAccess = canManageUsers || canManageGroups
  const canManageQualityRules = granted.has('quality-assurance.rules.manage')
  const canManageEstimatingSettings = granted.has('estimating.settings.admin')
  const canManageWorkCenters = granted.has(PERMISSIONS.workCenters)
  const canImportWorkCenters = granted.has(PERMISSIONS.workCenterImports)
  const isAdministrator = trackerUser?.groups.some(
    (group) => group.toLowerCase() === 'administrators',
  ) ?? false
  const canOpenSection = (section: ProjectTrackerAdminSection) => {
    if (section === 'walkthrough') return isAdministrator && canManageGroups
    if (section === 'calendar') return granted.has(PERMISSIONS.calendar)
    if (section === 'work-centers') return canManageWorkCenters || canImportWorkCenters
    if (section === 'holidays') return granted.has(PERMISSIONS.holidays)
    return isAdministrator && granted.has(PERMISSIONS.imports)
  }
  const selectedTrackerSection = route.section as ProjectTrackerAdminSection
  const selectedSectionAllowed = canOpenSection(selectedTrackerSection)
  const firstAllowedTrackerSection = permissionsLoading || permissionsError
    ? undefined
    : PROJECT_TRACKER_SECTIONS.find((section) => canOpenSection(section.key))?.key
  const Icon = activeModule.icon

  useEffect(() => {
    if (route.module !== 'project-tracker'
      || permissionsLoading
      || permissionsError
      || selectedSectionAllowed
      || !firstAllowedTrackerSection) return

    window.location.replace(`#/admin/project-tracker/${firstAllowedTrackerSection}`)
  }, [
    firstAllowedTrackerSection,
    permissionsError,
    permissionsLoading,
    route.module,
    selectedSectionAllowed,
  ])

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
          <h1>Arda Admin</h1>
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
                  tabIndex={allowed && (selected || (!selectedSectionAllowed && section.key === firstAllowedTrackerSection)) ? 0 : -1}
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
        {route.module === 'engineering' && (
          <nav className="admin-section-tabs" role="tablist" aria-label="Engineering admin sections" onKeyDown={(event) => handleTabKeys(event, `#/admin/engineering/${route.section}`)}>
            {ENGINEERING_SECTIONS.map((section) => {
              const SectionIcon = section.icon
              const selected = section.key === route.section
              return (
                <a
                  key={section.key}
                  role="tab"
                  id={`admin-engineering-section-tab-${section.key}`}
                  aria-selected={selected}
                  aria-controls="admin-section-panel"
                  tabIndex={selected ? 0 : -1}
                  className={selected ? 'active' : ''}
                  href={`#/admin/engineering/${section.key}`}
                >
                  <SectionIcon size={15} aria-hidden="true"/> {section.label}
                </a>
              )
            })}
          </nav>
        )}
        {route.module === 'quality-assurance' && (
          <nav className="admin-section-tabs" role="tablist" aria-label="Quality Assurance admin sections" onKeyDown={(event) => handleTabKeys(event, `#/admin/quality-assurance/${route.section}`)}>
            {QUALITY_SECTIONS.map((section) => {
              const SectionIcon = section.icon
              const selected = section.key === route.section
              const allowed = !permissionsLoading && canManageQualityRules
              return (
                <a
                  key={section.key}
                  role="tab"
                  id={`admin-quality-section-tab-${section.key}`}
                  aria-selected={selected}
                  aria-disabled={!allowed}
                  aria-controls="admin-section-panel"
                  tabIndex={selected && allowed ? 0 : -1}
                  className={`${selected ? 'active' : ''} ${allowed ? '' : 'disabled'}`.trim()}
                  href={`#/admin/quality-assurance/${section.key}`}
                  onClick={(event) => blockUnauthorizedNavigation(event, allowed)}
                >
                  <SectionIcon size={15} aria-hidden="true" /> {section.label}
                </a>
              )
            })}
          </nav>
        )}

        <div
          id="admin-section-panel"
          role={route.module === 'project-tracker' || route.module === 'engineering' || route.module === 'quality-assurance' ? 'tabpanel' : undefined}
          aria-labelledby={route.module === 'project-tracker'
            ? `admin-section-tab-${route.section}`
            : route.module === 'engineering'
              ? `admin-engineering-section-tab-${route.section}`
              : route.module === 'quality-assurance'
                ? `admin-quality-section-tab-${route.section}`
              : undefined}
        >
          {route.module === 'access' && permissionsLoading && <div className="admin-loading" role="status">Checking Access permissions...</div>}
          {route.module === 'access' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'access' && !permissionsLoading && !permissionsError && !canOpenAccess && <NoAccess detail="Your groups do not grant permission to manage users or groups." />}
          {route.module === 'access' && !permissionsLoading && !permissionsError && canOpenAccess && (
            <AccessPanel
              currentAccountName={trackerUser?.accountName ?? currentAccountName}
              canManageUsers={canManageUsers}
              canManageGroups={canManageGroups}
            />
          )}
          {route.module === 'hub' && <HubOverview canPreviewAccess={currentPortalRole === 'Admin'} onPreviewAccess={onPreviewAccess} />}
          {route.module === 'project-tracker' && permissionsLoading && <div className="admin-loading" role="status">Checking Project Tracker permissions...</div>}
          {route.module === 'project-tracker' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'project-tracker' && !permissionsLoading && !permissionsError && !selectedSectionAllowed && <NoAccess detail="Your Project Tracker groups do not grant the permission required for this administration section." />}
          {route.module === 'project-tracker' && !permissionsLoading && !permissionsError && selectedSectionAllowed && (
            <>
              {route.section === 'walkthrough' && <WalkthroughSettingsPanel onPreviewWalkthrough={onPreviewWalkthrough} />}
              {route.section === 'calendar' && <WorkCalendarPanel />}
              {route.section === 'work-centers' && (
                <WorkCentersPanel
                  canManage={canManageWorkCenters}
                  canImport={canImportWorkCenters}
                />
              )}
              {route.section === 'holidays' && <HolidaysPanel />}
              {route.section === 'imports' && <ImportsPanel />}
            </>
          )}
          {route.module === 'engineering' && <EngineeringStoragePanel/>}
          {route.module === 'quality-assurance' && permissionsLoading && <div className="admin-loading" role="status">Checking Quality Assurance permissions...</div>}
          {route.module === 'quality-assurance' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'quality-assurance' && !permissionsLoading && !permissionsError && !canManageQualityRules && <NoAccess detail="Your groups do not grant permission to manage Quality assignment rules." />}
          {route.module === 'quality-assurance' && !permissionsLoading && !permissionsError && canManageQualityRules && <QualityAssignmentRulesPanel />}
          {route.module === 'estimating' && permissionsLoading && <div className="admin-loading" role="status">Checking Estimating permissions...</div>}
          {route.module === 'estimating' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'estimating' && !permissionsLoading && !permissionsError && !canManageEstimatingSettings && <NoAccess detail="Your groups do not grant permission to administer Estimating settings." />}
          {route.module === 'estimating' && !permissionsLoading && !permissionsError && canManageEstimatingSettings && <EstimatorSettingsPanel />}
        </div>
      </section>
    </main>
  )
}
