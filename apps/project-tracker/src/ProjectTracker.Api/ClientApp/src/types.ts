export type ProjectStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
export type TaskStatus = 'NotStarted' | 'OnTrack' | 'Behind' | 'Complete'
export type Screen = 'dashboard' | 'project' | 'calendar' | 'pastProjects' | 'settings' | 'import'
export const screens: Screen[] = ['dashboard', 'project', 'calendar', 'pastProjects', 'settings', 'import']
export type DayOfWeekName = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday'

export type User = {
  accountName: string
  displayName: string
  isRegistered: boolean
  isActive: boolean
  groups: string[]
  permissions: string[]
  canEdit: boolean
  isAdmin: boolean
}

export type Dashboard = {
  activeProjects: number
  onTrackProjects: number
  behindProjects: number
  averageProgress: number
  nearestDelivery: string | null
  projects: ProjectSummary[]
}

export type ProjectNote = {
  note: string
  step: string
  at: string
}

export type DashboardSortField = 'priority' | 'target' | 'schedule' | 'notes'
export type DashboardSort = { field: DashboardSortField; dir: 'asc' | 'desc' }

export type ProjectSummary = {
  id: number
  version: number
  programName: string
  programManager: string | null
  engineer: string | null
  customerName: string | null
  salesOrderNumber: string | null
  jobNumber?: string | null
  currentTask: string | null
  priorityRank: number | null
  progress: number
  targetDelivery: string | null
  finalCompletionDate: string | null
  daysLeft: number | null
  daysBehind: number | null
  status: ProjectStatus
  taskCount: number
  behindTaskCount: number
  recentNote: ProjectNote | null
  plannedStart?: string | null
  plannedFinish?: string | null
  actualStart?: string | null
  actualFinish?: string | null
  scheduleVarianceDays?: number | null
  schedulePerformance?: string | null
}

export type ProjectDetail = {
  id: number
  version: number
  programName: string
  programManager: string | null
  engineer: string | null
  customerName: string | null
  salesOrderNumber: string | null
  jobNumber?: string | null
  currentTask: string | null
  programStart: string | null
  targetDelivery: string | null
  completedOn: string | null
  progress: number
  status: ProjectStatus
  daysBehind: number | null
  tasks: ProjectTask[]
  plannedStart?: string | null
  plannedFinish?: string | null
  actualStart?: string | null
  actualFinish?: string | null
  scheduleVarianceDays?: number | null
  schedulePerformance?: string | null
}

export type ProjectMetadataDraft = {
  programManager: string
  engineer: string
  customerName: string
  salesOrderNumber: string
  jobNumber: string
}

export type ProjectVersion = {
  id: number
  version: number
  updatedAt: string
}

export type ProjectTask = {
  id: number
  version: number
  projectId: number
  sequence: number
  externalTaskId: string | null
  title: string
  phase: string | null
  workStation: string | null
  dependencyTaskId: number | null
  startDate: string | null
  startDateLocked: boolean
  originalStartDate: string | null
  endDate: string | null
  originalEndDate: string | null
  estimatedDuration: number | null
  actualDuration: number | null
  percentComplete: number
  percentCompleteManual: boolean
  status: TaskStatus
  notes: string | null
  overtimeDays: TaskOvertimeDay[]
}

export type TaskOvertimeDay = {
  id: number
  date: string
  note: string | null
}

export type Holiday = {
  id: number
  date: string
  name: string
}

export type WorkCenter = {
  id: number
  name: string
}

export type ScheduleSettings = {
  workingDays: DayOfWeekName[]
  updatedAt: string
}

export type TaskForm = {
  id?: number
  version: number
  sequence: number
  externalTaskId: string
  title: string
  phase: string
  workStation: string
  dependencyTaskId: string
  startDate: string
  startDateLocked: boolean
  originalStartDate: string
  endDate: string
  originalEndDate: string
  estimatedDuration: string
  actualDuration: string
  percentComplete: string
  percentCompleteManual: boolean
  notes: string
  overtimeDays: TaskOvertimeDay[]
}

export type ProjectConfirmation = 'complete' | 'delete' | 'reopen'

export type ConcurrencyConflict = {
  code: 'ConcurrencyConflict'
  message: string
  resourceType: string
  resourceId: number
}

export type ProjectAuditChange = {
  field: string
  oldValue: string | null
  newValue: string | null
}

export type RegisteredUser = {
  id: number
  accountName: string
  displayName: string
  isActive: boolean
  lastSeenAt: string
  groupIds: number[]
}

export type AccessGroup = {
  id: number
  name: string
  description: string | null
  isSystemGroup: boolean
  permissions: string[]
  userCount: number
}

export type PermissionDefinition = {
  key: string
  label: string
  description: string
  category: string
}

export type AccessOverview = {
  users: RegisteredUser[]
  groups: AccessGroup[]
  permissions: PermissionDefinition[]
}

export type ArchivedProject = {
  id: number
  version: number
  programName: string
  customerName: string | null
  salesOrderNumber: string | null
  deletedAt: string
  deletedByDisplayName: string | null
}

export type ProjectAuditEntry = {
  id: number
  projectId: number
  projectTaskId: number | null
  action: string
  summary: string
  changes: ProjectAuditChange[]
  changedByAccountName: string
  changedByDisplayName: string
  changedAt: string
}

export type ProjectCreateRequest = {
  programName: string
  programManager: string | null
  customerName: string | null
  salesOrderNumber: string | null
  jobNumber: string | null
  programStart: string | null
  templateProjectId: number | null
}

export type GanttItem = {
  task: ProjectTask
  startMs: number
  endMs: number
  projected: boolean
  left: number
  width: number
}

export type ProjectMessage = {
  id: number
  projectId: number
  authorAccountName: string
  authorDisplayName: string
  body: string
  createdAt: string
}

export type MentionableUser = {
  accountName: string
  displayName: string
  mentionHandle: string
}

export type CalOp = {
  projectId: number
  taskId: number
  programName: string
  workStation: string | null
  taskTitle: string
  status: TaskStatus
  projected: boolean
  conflict: boolean
  completedProject: boolean
}

export type OperationDependent = {
  id: number
  sequence: number
  title: string
}

export type MentionNotificationKind = 'ProjectChatMention' | 'OperationNoteMention'

export type MentionNotification = {
  id: number
  kind: MentionNotificationKind
  projectId: number
  projectName: string
  projectTaskId: number | null
  operationName: string | null
  actorAccountName: string
  actorDisplayName: string
  title: string
  bodyPreview: string
  createdAt: string
  readAt: string | null
}

export type CalendarMilestoneKind = 'start' | 'finish'

export type CalendarMilestone = {
  projectId: number
  taskId: number
  programName: string
  workStation: string | null
  taskTitle: string
  status: TaskStatus
  projected: boolean
  completedProject: boolean
  kind: CalendarMilestoneKind
}

export const emptyDashboard: Dashboard = {
  activeProjects: 0,
  onTrackProjects: 0,
  behindProjects: 0,
  averageProgress: 0,
  nearestDelivery: null,
  projects: [],
}

export const dayMs = 86_400_000

export const defaultScheduleSettings: ScheduleSettings = {
  workingDays: ['Monday', 'Tuesday', 'Wednesday', 'Thursday'],
  updatedAt: '',
}
