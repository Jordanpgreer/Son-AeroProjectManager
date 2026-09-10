import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, ChevronDown, ChevronRight, Menu, ShieldCheck } from 'lucide-react'
import {
  ADMIN_MODULES, ARDA_ACCESS_SECTIONS, ENGINEERING_SECTIONS,
  PROJECT_TRACKER_SECTIONS, QUALITY_SECTIONS,
} from './adminNavigationModel'
import type { AdminModuleKey, ProjectTrackerAdminSection } from './types'

interface AdminNavigationProps {
  selected: AdminModuleKey
  section: string
  canSeeAdminOnly: boolean
  canSeeBenny: boolean
  canManageGroups: boolean
  canManageUsers: boolean
  canPreviewAccess: boolean
  canOpenTrackerSection: (section: ProjectTrackerAdminSection) => boolean
  canManageQualityRules: boolean
}

export default function AdminNavigation(props: AdminNavigationProps) {
  const [expanded, setExpanded] = useState(false)
  const sidebarRef = useRef<HTMLElement>(null)
  const toggleRef = useRef<HTMLButtonElement>(null)
  const { selected, section, canSeeAdminOnly, canSeeBenny } = props
  const modules = ADMIN_MODULES.filter((module) => module.key === 'benny'
    ? canSeeBenny : canSeeAdminOnly || !module.adminOnly)

  useEffect(() => {
    sidebarRef.current?.querySelector('[aria-current="page"]')?.scrollIntoView({ block: 'nearest' })
  }, [selected, section, expanded])

  function linksFor(module: typeof ADMIN_MODULES[number]) {
    if (module.key === 'access') return ARDA_ACCESS_SECTIONS.map((item) => ({
      ...item, allowed: item.key === 'preview' ? props.canPreviewAccess
        : item.key === 'groups' ? props.canManageGroups : props.canManageUsers,
    }))
    if (module.key === 'project-tracker') return PROJECT_TRACKER_SECTIONS.map((item) => ({
      ...item, href: `#/admin/project-tracker/${item.key}`, allowed: props.canOpenTrackerSection(item.key),
    }))
    if (module.key === 'engineering') return ENGINEERING_SECTIONS.map((item) => ({
      ...item, href: `#/admin/engineering/${item.key}`, allowed: true,
    }))
    if (module.key === 'quality-assurance') return QUALITY_SECTIONS.map((item) => ({
      ...item, href: `#/admin/quality-assurance/${item.key}`, allowed: props.canManageQualityRules,
    }))
    return [{ key: module.href.split('/').at(-1)!, label: module.label, icon: module.icon, href: module.href, allowed: true }]
  }

  return (
    <aside className={`admin-sidebar${expanded ? ' is-expanded' : ''}`} ref={sidebarRef} aria-label="Administration navigation" onKeyDown={(event) => {
      if (event.key === 'Escape' && expanded) {
        setExpanded(false)
        toggleRef.current?.focus()
      }
    }}>
      <div className="admin-sidebar-heading">
        <span className="admin-sidebar-mark"><ShieldCheck size={20} aria-hidden="true" /></span>
        <div><strong>Arda Admin</strong><span>Administration workspace</span></div>
        <button ref={toggleRef} type="button" className="admin-nav-toggle" aria-label={expanded ? 'Close admin navigation' : 'Open admin navigation'} aria-expanded={expanded} aria-controls="admin-sidebar-body" onClick={() => setExpanded(!expanded)}>
          {expanded ? <ChevronDown size={20} /> : <Menu size={20} />}
        </button>
      </div>
      <div className="admin-sidebar-body" id="admin-sidebar-body">
        <a className="admin-sidebar-back" href="#/"><ArrowLeft size={15} aria-hidden="true" /> Applications</a>
        <nav aria-label="Admin pages">
          {modules.map((module) => {
            const links = linksFor(module)
            const grouped = ['access', 'project-tracker', 'engineering', 'quality-assurance'].includes(module.key)
            const groupLabel = module.sidebarGroup ?? (grouped ? module.label : undefined)
            return (
              <div className="admin-nav-group" key={module.key}>
                {groupLabel && <p className="admin-nav-group-label" id={`admin-nav-group-${module.key}`}>{groupLabel}</p>}
                <ul aria-labelledby={groupLabel ? `admin-nav-group-${module.key}` : undefined}>
                  {links.map((item) => {
                    const Icon = item.icon
                    const active = module.key === selected && item.key === section
                    return <li key={item.key}>
                      <a href={item.allowed ? item.href : undefined} aria-disabled={!item.allowed || undefined} aria-current={active ? 'page' : undefined} className={`admin-nav-link${active ? ' active' : ''}`} onClick={() => {
                        if (item.allowed) {
                          setExpanded(false)
                          if (active) document.getElementById('admin-workspace-title')?.focus()
                        }
                      }}>
                        <Icon size={17} aria-hidden="true" /><span>{item.label}</span>
                        {active && <ChevronRight size={14} className="admin-nav-current" aria-hidden="true" />}
                      </a>
                    </li>
                  })}
                </ul>
              </div>
            )
          })}
        </nav>
        <div className="admin-sidebar-footer"><ShieldCheck size={14} aria-hidden="true" /><span>Permission-controlled access</span></div>
      </div>
    </aside>
  )
}
