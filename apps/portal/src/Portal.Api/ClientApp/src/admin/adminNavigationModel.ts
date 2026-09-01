import {
  Calculator,
  ClipboardCheck,
  Database,
  Eye,
  GanttChart,
  KeyRound,
  Settings2,
  ShieldCheck,
  Users,
} from 'lucide-react'
import { resolveModuleApplicationUrl } from './moduleUrls'
import type { AdminModuleKey, ArdaAccessSection } from './types'

export const ADMIN_MODULES: {
  key: AdminModuleKey
  label: string
  description: string
  icon: typeof Settings2
  href: string
  openUrl?: string
}[] = [
  {
    key: 'access',
    label: 'Arda Access',
    description: 'People, permission groups, and access preview',
    icon: Users,
    href: '#/admin/access',
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
    key: 'integrations',
    label: 'API Keys',
    description: 'Protected credentials for connected systems',
    icon: KeyRound,
    href: '#/admin/integrations/api-keys',
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

export const ARDA_ACCESS_SECTIONS: {
  key: ArdaAccessSection
  label: string
  icon: typeof Settings2
  href: string
}[] = [
  { key: 'groups', label: 'Permission groups', icon: ShieldCheck, href: '#/admin/access' },
  { key: 'people', label: 'People', icon: Users, href: '#/admin/access/people' },
  { key: 'preview', label: 'Access preview', icon: Eye, href: '#/admin/access/preview' },
]
