import { useEffect, useMemo, useRef, useState } from 'react'
import { ExternalLink, LockKeyhole } from 'lucide-react'
import AccessPanel from './AccessPanel'
import BennySettingsPanel from './BennySettingsPanel'
import AccessPreviewPanel from './AccessPreviewPanel'
import AdminNavigation from './AdminNavigation'
import { ADMIN_MODULES, ARDA_ACCESS_SECTIONS, PROJECT_TRACKER_SECTIONS, ENGINEERING_SECTIONS, QUALITY_SECTIONS } from './adminNavigationModel'
import EngineeringStoragePanel from './EngineeringStoragePanel'
import EstimatorSettingsPanel from './EstimatorSettingsPanel'
import EstimatingImportAccessPanel from './EstimatingImportAccessPanel'
import IntegrationCredentialsPanel from './IntegrationCredentialsPanel'
import ApiCustomizerPanel from './ApiCustomizerPanel'
import QualityAssignmentRulesPanel from './QualityAssignmentRulesPanel'
import RaidLogPanel from './RaidLogPanel'
import WalkthroughSettingsPanel from './WalkthroughSettingsPanel'
import { toErrorMessage, trackerApi } from './api'
import { ImportsPanel } from './ProjectTrackerDataPanels'
import {
  HolidaysPanel,
  WorkCalendarPanel,
  WorkCentersPanel,
} from './ProjectTrackerSettingsPanels'
import type {
  AdminModuleKey,
  ArdaAccessSection,
  ProjectTrackerAdminSection,
  ProjectTrackerUser,
  AdminAccessPreviewTarget,
} from './types'
import './admin.css'
import './admin-responsive.css'
import './access-management.css'
import './admin-workspace.css'

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

function parseRoute(hash = window.location.hash): AdminRoute {
  const path = hash.replace(/^#\/?/, '').split('?')[0]
  const [, rawModule, rawSection] = path.split('/')
  const requestedModule = rawModule === 'hub' ? 'access' : rawModule
  const module = ADMIN_MODULES.some((candidate) => candidate.key === requestedModule)
    ? requestedModule as AdminModuleKey
    : 'access'
  const validAccessSection = ARDA_ACCESS_SECTIONS.some(
    (candidate) => candidate.key === rawSection,
  )
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
    section: module === 'access'
      ? rawModule === 'hub'
        ? 'preview'
        : validAccessSection
          ? rawSection
          : 'groups'
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
              : module === 'raid-log'
                ? 'board'
              : module === 'integrations'
                ? 'api-keys'
              : module === 'benny'
                ? 'settings'
              : 'overview',
  }
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

function AccessPreviewOverview({
  onPreviewAccess,
}: {
  onPreviewAccess: (target: AdminAccessPreviewTarget) => void
}) {
  return (
    <section className="admin-surface admin-hub-overview" aria-label="Hub access preview">
      <AccessPreviewPanel onPreview={onPreviewAccess} />
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
  const activeModule = ADMIN_MODULES.find((module) => module.key === route.module) ?? ADMIN_MODULES[0]
  const panelHeadingRef = useRef<HTMLHeadingElement>(null)
  const [trackerUser, setTrackerUser] = useState<ProjectTrackerUser | null>(null)
  const [permissionsLoading, setPermissionsLoading] = useState(true)
  const [permissionsError, setPermissionsError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    setPermissionsLoading(true)
    setPermissionsError(null)
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
  }, [route.module])

  useEffect(() => {
    const canonical = route.module === 'access'
      ? route.section === 'preview'
        ? '#/admin/access/preview'
        : route.section === 'people'
          ? '#/admin/access/people'
          : '#/admin/access'
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
  const canPreviewAccess = currentPortalRole === 'Admin'
  const canManageQualityRules = granted.has('quality-assurance.rules.manage')
  const canManageEstimatingSettings = granted.has('estimating.settings.admin')
  const canManageEstimatingImportAccess = canManageGroups
  const canManageWorkCenters = granted.has(PERMISSIONS.workCenters)
  const canImportWorkCenters = granted.has(PERMISSIONS.workCenterImports)
  const isAdministrator = trackerUser?.groups.some(
    (group) => group.toLowerCase() === 'administrators',
  ) ?? false
  // Mirrors the API's ManageWalkthrough policy (Administrators group +
  // access.manageGroups). Module admins satisfy neither, so Benny's tab is
  // hidden from them and the panel refuses to render if they guess the URL.
  const canAdministerBenny = isAdministrator && canManageGroups
  const canOpenSection = (section: ProjectTrackerAdminSection) => {
    if (section === 'walkthrough') return isAdministrator && canManageGroups
    if (section === 'calendar') return granted.has(PERMISSIONS.calendar)
    if (section === 'work-centers') return canManageWorkCenters || canImportWorkCenters
    if (section === 'holidays') return granted.has(PERMISSIONS.holidays)
    return isAdministrator && granted.has(PERMISSIONS.imports)
  }
  const selectedTrackerSection = route.section as ProjectTrackerAdminSection
  const selectedAccessSection = route.section as ArdaAccessSection
  const selectedSectionAllowed = canOpenSection(selectedTrackerSection)
  const firstAllowedTrackerSection = permissionsLoading || permissionsError
    ? undefined
    : PROJECT_TRACKER_SECTIONS.find((section) => canOpenSection(section.key))?.key
  const selectedAccessSectionAllowed = selectedAccessSection === 'preview'
    ? canPreviewAccess
    : !permissionsLoading && !permissionsError && (selectedAccessSection === 'groups' ? canManageGroups : canManageUsers)
  const firstAllowedAccessSection: ArdaAccessSection | undefined = permissionsLoading || permissionsError
    ? undefined
    : canManageGroups
      ? 'groups'
      : canManageUsers
        ? 'people'
        : canPreviewAccess
          ? 'preview'
          : undefined
  const activeSection = (route.module === 'access' ? ARDA_ACCESS_SECTIONS
    : route.module === 'project-tracker' ? PROJECT_TRACKER_SECTIONS
    : route.module === 'engineering' ? ENGINEERING_SECTIONS
    : route.module === 'quality-assurance' ? QUALITY_SECTIONS : []).find((section) => section.key === route.section)
  const pageTitle = activeSection?.label ?? activeModule.label
  const permissionsReady = !permissionsLoading && !permissionsError

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

  useEffect(() => {
    if (route.module !== 'access'
      || permissionsLoading
      || permissionsError
      || selectedAccessSectionAllowed
      || !firstAllowedAccessSection) return

    window.location.replace(firstAllowedAccessSection === 'preview'
      ? '#/admin/access/preview'
      : firstAllowedAccessSection === 'people'
        ? '#/admin/access/people'
        : '#/admin/access')
  }, [
    firstAllowedAccessSection,
    permissionsError,
    permissionsLoading,
    route.module,
    selectedAccessSectionAllowed,
  ])

  return (
    <main className="portal-main admin-main" id="main-content">
      <AdminNavigation
        selected={route.module}
        section={route.section}
        canSeeAdminOnly={currentPortalRole === 'Admin'}
        canSeeBenny={permissionsReady && canAdministerBenny}
        canManageGroups={permissionsReady && canManageGroups}
        canManageUsers={permissionsReady && canManageUsers}
        canPreviewAccess={canPreviewAccess}
        canOpenTrackerSection={(section) => permissionsReady && canOpenSection(section)}
        canManageQualityRules={permissionsReady && canManageQualityRules}
      />
      <section className="admin-module-panel" aria-labelledby="admin-workspace-title">
        <header className="admin-workspace-head">
          <div>
            <p className="admin-breadcrumb">Administration <span aria-hidden="true">/</span> {activeModule.label}</p>
            <h1 id="admin-workspace-title" ref={panelHeadingRef} tabIndex={-1}>{pageTitle}</h1>
            <p className="admin-workspace-description">{activeModule.description}</p>
          </div>
          {activeModule.openUrl && <a className="ghost-button" href={activeModule.openUrl} target="_top">Open module <ExternalLink size={15} aria-hidden="true" /></a>}
        </header>
        <div id="admin-section-panel" className="admin-workspace-content">
          {route.module === 'access' && selectedAccessSection !== 'preview' && permissionsLoading && <div className="admin-loading" role="status">Checking Access permissions...</div>}
          {route.module === 'access' && selectedAccessSection !== 'preview' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'access' && selectedAccessSection !== 'preview' && !permissionsLoading && !permissionsError && !selectedAccessSectionAllowed && <NoAccess detail={`Your groups do not grant permission to manage ${selectedAccessSection === 'people' ? 'people' : 'permission groups'}.`} />}
          {route.module === 'access' && selectedAccessSection !== 'preview' && !permissionsLoading && !permissionsError && selectedAccessSectionAllowed && (
            <AccessPanel
              currentAccountName={trackerUser?.accountName ?? currentAccountName}
              canManageUsers={canManageUsers}
              canManageGroups={canManageGroups}
              view={selectedAccessSection === 'people' ? 'people' : 'groups'}
            />
          )}
          {route.module === 'access' && selectedAccessSection === 'preview' && !canPreviewAccess && <NoAccess detail="Access preview requires the Arda Administrator role." />}
          {route.module === 'access' && selectedAccessSection === 'preview' && canPreviewAccess && <AccessPreviewOverview onPreviewAccess={onPreviewAccess} />}
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
          {route.module === 'quality-assurance' && !permissionsLoading && !permissionsError && !canManageQualityRules && <NoAccess detail="Your groups do not grant permission to manage Quality workflows." />}
          {route.module === 'quality-assurance' && !permissionsLoading && !permissionsError && canManageQualityRules && <QualityAssignmentRulesPanel />}
          {route.module === 'raid-log' && currentPortalRole !== 'Admin' && <NoAccess detail="The RAID Log is available only to Arda administrators." />}
          {route.module === 'raid-log' && currentPortalRole === 'Admin' && <RaidLogPanel currentAccountName={trackerUser?.accountName ?? currentAccountName} />}
          {route.module === 'estimating' && permissionsLoading && <div className="admin-loading" role="status">Checking Estimating permissions...</div>}
          {route.module === 'estimating' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'estimating' && !permissionsLoading && !permissionsError && !canManageEstimatingSettings && !canManageEstimatingImportAccess && <NoAccess detail="Your groups do not grant permission to administer Estimating settings or group import access." />}
          {route.module === 'estimating' && !permissionsLoading && !permissionsError && (canManageEstimatingSettings || canManageEstimatingImportAccess) && (
            <div className="estimating-admin-stack">
              {canManageEstimatingImportAccess && <EstimatingImportAccessPanel />}
              {canManageEstimatingSettings
                ? <EstimatorSettingsPanel />
                : <p className="admin-readonly-note">Active estimator settings require the Administer Estimating Settings permission.</p>}
            </div>
          )}
          {route.module === 'benny' && permissionsLoading && <div className="admin-loading" role="status">Checking Benny permissions...</div>}
          {route.module === 'benny' && !permissionsLoading && permissionsError && <NoAccess detail={permissionsError} />}
          {route.module === 'benny' && !permissionsLoading && !permissionsError && !canAdministerBenny && <NoAccess detail="Benny administration requires the Arda Administrators group and the Manage Permission Groups permission." />}
          {route.module === 'benny' && !permissionsLoading && !permissionsError && canAdministerBenny && <BennySettingsPanel />}
          {route.module === 'integrations' && currentPortalRole !== 'Admin' && <NoAccess detail="API key management requires the Arda Administrator role." />}
          {route.module === 'integrations' && currentPortalRole === 'Admin' && <IntegrationCredentialsPanel />}
          {route.module === 'api-customizer' && currentPortalRole !== 'Admin' && <NoAccess detail="API Customizer is available only to Arda administrators." />}
          {route.module === 'api-customizer' && currentPortalRole === 'Admin' && <ApiCustomizerPanel />}
        </div>
      </section>
    </main>
  )
}
