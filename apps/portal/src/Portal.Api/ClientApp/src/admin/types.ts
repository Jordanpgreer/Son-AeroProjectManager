export type AdminModuleKey =
  | 'access'
  | 'project-tracker'
  | 'engineering'
  | 'estimating'
  | 'quality-assurance'

export type ArdaAccessSection =
  | 'groups'
  | 'people'
  | 'preview'

export type ProjectTrackerAdminSection =
  | 'walkthrough'
  | 'calendar'
  | 'work-centers'
  | 'holidays'
  | 'imports'

export type EngineeringAdminSection =
  | 'file-storage'

export type QualityAdminSection =
  | 'assignment-rules'

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
  moduleKey: string
  moduleName: string
}

export interface AccessOverview {
  users: RegisteredUser[]
  groups: AccessGroup[]
  permissions: PermissionDefinition[]
}

export interface AdminPreviewApplication {
  id: string
  name: string
  description: string
  category: string
  icon: string
  url: string
  order: number
  status: 'active' | 'comingSoon' | 'maintenance'
  hasPreview: boolean
}

export interface AdminAccessPreviewTarget {
  key: string
  kind: 'user' | 'group'
  title: string
  subtitle: string
  role: string
  applications: AdminPreviewApplication[]
}

export interface AdminAccessPreviewOverview {
  users: AdminAccessPreviewTarget[]
  groups: AdminAccessPreviewTarget[]
}

export interface AdminAccessPreviewLaunch {
  actionUrl: string
  token: string
  expiresAt: string
}

export interface EngineeringStorageOverview {
  rootPath: string
  configured: boolean
  isNetworkPath: boolean
  available: boolean
  writable: boolean
  message: string
  designAuthorities: string[]
  previousRootCount: number
  updatedAt: string | null
  updatedBy: string | null
  canManageStorage: boolean
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

export interface WorkCenter {
  id: number
  name: string
}

export interface EstimatorSetting {
  estimator: string
  isActive: boolean
  isExplicitlyConfigured: boolean
  updatedAt: string | null
  updatedBy: string | null
}

export interface EstimatorSettingsOverview {
  estimators: EstimatorSetting[]
}

export interface WorkCenterImportResult {
  addedCount: number
  skippedCount: number
  addedNames: string[]
  skippedNames: string[]
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

export interface ImportIssue {
  sheet: string
  row: number
  column: string | null
  message: string
}

export interface ImportChange {
  sheet: string
  row: number
  recordKey: string
  changeType: 'Added' | 'Modified'
  field: string
  currentValue: string | null
  uploadedValue: string | null
}

export interface ImportValidationResult {
  reviewId: string
  expiresAt: string
  fileName: string
  projectRows: number
  operationRows: number
  projectsAdded: number
  projectsUpdated: number
  operationsAdded: number
  operationsUpdated: number
  changeCount: number
  errors: ImportIssue[]
  changes: ImportChange[]
  reviewWorkbookUrl: string
  canConfirm: boolean
  workbookFormat: string
  projectsRequiringCompletion: number
}

export interface ImportApplyResult {
  projectsAdded: number
  projectsUpdated: number
  operationsAdded: number
  operationsUpdated: number
  changeCount: number
}

export interface QualityDirectoryGroup {
  id: number
  name: string
  description: string | null
  activeUserCount: number
}

export interface QualityDirectoryUser {
  id: number
  accountName: string
  displayName: string
  groupIds: number[]
}

export interface QualityAssignmentOptions {
  groups: QualityDirectoryGroup[]
  users: QualityDirectoryUser[]
}

export interface QualityAssignmentRule {
  id: number
  name: string
  isEnabled: boolean
  priority: number
  matchField: 'Customer' | 'TaskType'
  matchOperator: 'Equals' | 'Contains' | 'StartsWith'
  matchValue: string
  targetGroupId: number
  targetGroupName: string
  assignmentMode: 'GroupOnly' | 'SpecificUser' | 'LeastLoaded'
  targetUserId: number | null
  targetDisplayName: string | null
  version: number
  updatedAt: string
  updatedBy: string
}
