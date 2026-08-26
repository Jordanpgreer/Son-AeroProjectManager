import type { KeyboardEvent } from 'react'
import { ADMIN_MODULES, ARDA_ACCESS_SECTIONS } from './adminNavigationModel'
import type { AdminModuleKey, ArdaAccessSection } from './types'

export function AdminModuleTabs({
  selected,
  onKeyDown,
}: {
  selected: AdminModuleKey
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
}) {
  return (
    <nav className="admin-module-tabs" role="tablist" aria-label="Admin modules" onKeyDown={onKeyDown}>
      {ADMIN_MODULES.map((module) => {
        const ModuleIcon = module.icon
        const active = module.key === selected
        return (
          <a role="tab" id={`admin-module-tab-${module.key}`} aria-selected={active} aria-controls="admin-module-panel" tabIndex={active ? 0 : -1} className={active ? 'active' : ''} href={module.href} key={module.key}>
            <ModuleIcon size={18} aria-hidden="true" />
            <span><strong>{module.label}</strong><small>{module.description}</small></span>
          </a>
        )
      })}
    </nav>
  )
}

export function ArdaAccessTabs({
  selected,
  selectedAllowed,
  firstAllowed,
  canManageGroups,
  canManageUsers,
  canPreviewAccess,
  onKeyDown,
}: {
  selected: ArdaAccessSection
  selectedAllowed: boolean
  firstAllowed?: ArdaAccessSection
  canManageGroups: boolean
  canManageUsers: boolean
  canPreviewAccess: boolean
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
}) {
  return (
    <nav className="admin-section-tabs" role="tablist" aria-label="Arda Access sections" onKeyDown={onKeyDown}>
      {ARDA_ACCESS_SECTIONS.map((section) => {
        const SectionIcon = section.icon
        const active = section.key === selected
        const allowed = section.key === 'preview'
          ? canPreviewAccess
          : section.key === 'groups'
            ? canManageGroups
            : canManageUsers
        return (
          <a
            key={section.key}
            role="tab"
            id={`admin-access-section-tab-${section.key}`}
            aria-selected={active}
            aria-disabled={!allowed}
            aria-controls="admin-section-panel"
            tabIndex={allowed && (active || (!selectedAllowed && section.key === firstAllowed)) ? 0 : -1}
            className={`${active ? 'active' : ''} ${allowed ? '' : 'disabled'}`.trim()}
            href={section.href}
            onClick={(event) => { if (!allowed) event.preventDefault() }}
          >
            <SectionIcon size={15} aria-hidden="true" /> {section.label}
          </a>
        )
      })}
    </nav>
  )
}
