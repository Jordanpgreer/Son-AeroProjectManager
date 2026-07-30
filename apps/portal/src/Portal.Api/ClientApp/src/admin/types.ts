export type AdminModuleKey =
  | 'hub'
  | 'project-tracker'
  | 'engineering'
  | 'estimating'

export type ProjectTrackerAdminSection =
  | 'access'
  | 'calendar'
  | 'work-centers'
  | 'holidays'
  | 'archived'
  | 'imports'

export type DayOfWeekName =
  | 'Sunday'
  | 'Monday'
  | 'Tuesday'
  | 'Wednesday'
  | 'Thursday'
  | 'Friday'
  | 'Saturday'

export interface RegisteredUser {
  id: number
  accountName: string
  displayName: string
  isActive: boolean
  lastSeenAt: string
  groupIds: number[]
}

export interface AccessGroup {
  id: number
  name: string
  description: string | null
  isSystemGroup: boolean
  permissions: string[]
  userCount: number
}

export interface PermissionDefinition {
  key: string
  label: string
  description: string
  category: string
}

export interface AccessOverview {
  users: RegisteredUser[]
  groups: AccessGroup[]
  permissions: PermissionDefinition[]
}

export interface ProjectTrackerUser {
  accountName: string
  displayName: string
  isRegistered: boolean
  isActive: boolean
  groups: string[]
  permissions: string[]
  canEdit: boolean
  isAdmin: boolean
}

export interface ModuleAccessRole {
  role: 'Viewer' | 'Editor' | 'Admin'
  permissions: PermissionDefinition[]
}

export interface ModuleAccessCatalogEntry {
  key: string
  name: string
  roles: ModuleAccessRole[]
}

export interface UserModuleAccess {
  moduleKey: string
  enabled: boolean
  role: 'Viewer' | 'Editor' | 'Admin' | null
  permissions: string[]
  updatedAt: string | null
}

export interface ModuleAccessUser {
  userId: number
  accountName: string
  displayName: string
  isActive: boolean
  modules: UserModuleAccess[]
}

export interface WorkCenter {
  id: number
  name: string
}

export interface Holiday {
  id: number
  date: string
  name: string
}

export interface ScheduleSettings {
  workingDays: DayOfWeekName[]
  updatedAt: string
}

export interface ArchivedProject {
  id: number
  version: number
  programName: string
  customerName: string | null
  salesOrderNumber: string | null
  deletedAt: string
  deletedByDisplayName: string | null
}

export interface ImportResult {
  projectCount: number
  taskCount: number
  holidayCount: number
}
