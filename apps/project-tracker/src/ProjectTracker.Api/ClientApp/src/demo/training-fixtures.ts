import type {
  Dashboard,
  ProjectDetail,
  ProjectMetadataDraft,
  ProjectSummary,
  ProjectTask,
  User,
} from '../types.ts'

const taskDefaults: Omit<ProjectTask, 'id' | 'projectId' | 'sequence' | 'title' | 'workStation' | 'startDate' | 'originalStartDate' | 'endDate' | 'originalEndDate' | 'estimatedDuration' | 'actualDuration' | 'percentComplete' | 'status' | 'notes'> = {
  version: 1,
  externalTaskId: null,
  phase: null,
  dependencyTaskId: null,
  startDateLocked: false,
  percentCompleteManual: false,
  overtimeDays: [],
}

function operation(
  id: number,
  projectId: number,
  sequence: number,
  title: string,
  workStation: string,
  startDate: string,
  endDate: string,
  percentComplete: number,
  status: ProjectTask['status'],
  notes: string,
  dependencyTaskId: number | null = null,
): ProjectTask {
  const start = new Date(`${startDate}T00:00:00`)
  const finish = new Date(`${endDate}T00:00:00`)
  const duration = Math.max(1, Math.round((finish.getTime() - start.getTime()) / 86_400_000) + 1)
  return {
    ...taskDefaults,
    id,
    projectId,
    sequence,
    title,
    workStation,
    dependencyTaskId,
    startDate,
    originalStartDate: startDate,
    endDate,
    originalEndDate: endDate,
    estimatedDuration: duration,
    actualDuration: percentComplete === 1 ? duration : null,
    percentComplete,
    status,
    notes,
  }
}

export const VIEW_ONLY_TRAINING_USER: User = {
  accountName: 'training\\session',
  displayName: 'Project Tracker Trainee',
  isRegistered: false,
  isActive: true,
  groups: [],
  permissions: ['module.view'],
  canEdit: false,
  isAdmin: false,
  walkthroughEnabled: true,
  assistantEnabled: false,
  assistantName: 'Benny',
  preview: null,
}

export type TrainingNotification = {
  id: number
  projectId: number
  taskId: number
  actorDisplayName: string
  title: string
  body: string
  createdAtLabel: string
  kind: 'note' | 'schedule'
  read: boolean
}

export type TrainingActivityEntry = {
  id: number
  projectId: number
  action: 'created' | 'completed' | 'schedule'
  summary: string
  actorDisplayName: string
  changedAtLabel: string
  changes: readonly {
    field: string
    oldValue: string
    newValue: string
  }[]
}

export type TrainingChatMessage = {
  id: number
  projectId: number
  authorDisplayName: string
  createdAtLabel: string
  body: string
}

export const TRAINING_NOTIFICATIONS: readonly TrainingNotification[] = [
  {
    id: 8101,
    projectId: 9002,
    taskId: 9202,
    actorDisplayName: 'Production Control',
    title: 'DEMO-1002 · Final Inspection',
    body: 'First article inspection is scheduled for Sep 21.',
    createdAtLabel: '12 min ago',
    kind: 'schedule',
    read: false,
  },
  {
    id: 8102,
    projectId: 9001,
    taskId: 9103,
    actorDisplayName: 'Jamie Lee',
    title: 'DEMO-1001 · CNC Machining',
    body: 'Machine time is running two days behind the original plan.',
    createdAtLabel: '34 min ago',
    kind: 'note',
    read: false,
  },
]

export const TRAINING_ACTIVITY: readonly TrainingActivityEntry[] = [
  {
    id: 8201,
    projectId: 9001,
    action: 'schedule',
    summary: 'CNC Machining schedule updated',
    actorDisplayName: 'Jamie Lee',
    changedAtLabel: 'Today at 9:42 AM',
    changes: [
      { field: 'Finish date', oldValue: 'Sep 5, 2026', newValue: 'Sep 8, 2026' },
      { field: 'Status', oldValue: 'On Track', newValue: 'Behind' },
    ],
  },
  {
    id: 8202,
    projectId: 9001,
    action: 'completed',
    summary: 'Material Preparation completed',
    actorDisplayName: 'Morgan Reed',
    changedAtLabel: 'Yesterday at 3:18 PM',
    changes: [{ field: 'Progress', oldValue: '75%', newValue: '100%' }],
  },
  {
    id: 8203,
    projectId: 9001,
    action: 'created',
    summary: 'Project DEMO-1001 created',
    actorDisplayName: 'Alex Morgan',
    changedAtLabel: 'Aug 21 at 8:06 AM',
    changes: [],
  },
  {
    id: 8211,
    projectId: 9002,
    action: 'schedule',
    summary: 'Final Inspection scheduled',
    actorDisplayName: 'Production Control',
    changedAtLabel: 'Today at 10:14 AM',
    changes: [{ field: 'Start date', oldValue: 'Sep 18, 2026', newValue: 'Sep 21, 2026' }],
  },
  {
    id: 8212,
    projectId: 9002,
    action: 'completed',
    summary: 'Production Planning completed',
    actorDisplayName: 'Taylor Chen',
    changedAtLabel: 'Aug 20 at 4:31 PM',
    changes: [{ field: 'Progress', oldValue: '80%', newValue: '100%' }],
  },
  {
    id: 8221,
    projectId: 9003,
    action: 'created',
    summary: 'Project DEMO-1003 created',
    actorDisplayName: 'Jordan Bell',
    changedAtLabel: 'Aug 19 at 11:23 AM',
    changes: [],
  },
  {
    id: 8231,
    projectId: 8998,
    action: 'completed',
    summary: 'Project completed one day early',
    actorDisplayName: 'Alex Morgan',
    changedAtLabel: 'Aug 13 at 2:47 PM',
    changes: [{ field: 'Project status', oldValue: 'On Track', newValue: 'Complete' }],
  },
]

export const TRAINING_CHAT_MESSAGES: readonly TrainingChatMessage[] = [
  {
    id: 8301,
    projectId: 9001,
    authorDisplayName: 'Morgan Reed',
    createdAtLabel: 'Yesterday at 2:14 PM',
    body: 'Material is staged at Mill 03. The next operation can begin as soon as the machine is released.',
  },
  {
    id: 8302,
    projectId: 9001,
    authorDisplayName: 'Jamie Lee',
    createdAtLabel: 'Today at 8:57 AM',
    body: '@AlexMorgan CNC setup is complete. I updated the finish date so the schedule reflects the current queue.',
  },
  {
    id: 8303,
    projectId: 9001,
    authorDisplayName: 'Alex Morgan',
    createdAtLabel: 'Today at 9:05 AM',
    body: 'Thanks. I will review the two-day variance with Production Control this morning.',
  },
  {
    id: 8311,
    projectId: 9002,
    authorDisplayName: 'Taylor Chen',
    createdAtLabel: 'Today at 9:20 AM',
    body: 'The first-article inspection packet is ready for Quality.',
  },
  {
    id: 8312,
    projectId: 9002,
    authorDisplayName: 'Production Control',
    createdAtLabel: 'Today at 9:34 AM',
    body: '@MorganReed Quality confirmed the Sep 21 inspection slot.',
  },
  {
    id: 8321,
    projectId: 9003,
    authorDisplayName: 'Casey Park',
    createdAtLabel: 'Aug 20 at 1:08 PM',
    body: 'Engineering review materials are attached to the training job record.',
  },
  {
    id: 8331,
    projectId: 8998,
    authorDisplayName: 'Jamie Lee',
    createdAtLabel: 'Aug 13 at 3:02 PM',
    body: 'Final records are complete. Nice work delivering this project a day early.',
  },
]

export const TRAINING_PROJECT_DETAILS: ProjectDetail[] = [
  {
    id: 9001,
    version: 1,
    programName: 'DEMO-1001',
    programManager: 'Alex Morgan',
    engineer: 'Jamie Lee',
    salesPerson: 'Morgan Reed',
    customerName: 'Training Customer',
    salesOrderNumber: 'TRAIN-2401',
    salesOrderUrl: null,
    jobNumber: 'TRAIN-J1001',
    jobUrl: null,
    requiredQuantity: 120,
    jobQuantity: 100,
    requiredQuantitySource: 'Fulcrum',
    jobQuantitySource: 'Fulcrum',
    quantityLastSyncProvider: 'Fulcrum',
    quantityLastSyncedAt: '2026-08-25T15:30:00Z',
    currentTask: 'CNC Machining',
    programStart: '2026-08-24',
    targetDelivery: '2026-09-18',
    completedOn: null,
    progress: 0.48,
    status: 'Behind',
    daysBehind: 2,
    plannedStart: '2026-08-24',
    plannedFinish: '2026-09-15',
    actualStart: '2026-08-24',
    actualFinish: null,
    scheduleVarianceDays: 2,
    schedulePerformance: 'Behind',
    requiresImportCompletion: false,
    missingImportFields: [],
    tasks: [
      operation(9101, 9001, 10, 'Engineering Review', 'Engineering', '2026-08-24', '2026-08-26', 1, 'Complete', 'Released for production.'),
      operation(9102, 9001, 20, 'Material Preparation', 'Saw', '2026-08-27', '2026-08-28', 1, 'Complete', 'Material staged at Mill 03.', 9101),
      operation(9103, 9001, 30, 'CNC Machining', 'Mill 03', '2026-08-31', '2026-09-08', 0.35, 'Behind', 'Machine time is running two days behind the original plan.', 9102),
      operation(9104, 9001, 40, 'Final Inspection', 'Quality', '2026-09-09', '2026-09-11', 0, 'NotStarted', 'Inspection plan is ready.', 9103),
      operation(9105, 9001, 50, 'Pack and Ship', 'Shipping', '2026-09-14', '2026-09-15', 0, 'NotStarted', 'Awaiting final inspection.', 9104),
    ],
  },
  {
    id: 9002,
    version: 1,
    programName: 'DEMO-1002',
    programManager: 'Morgan Reed',
    engineer: 'Taylor Chen',
    salesPerson: 'Alex Morgan',
    customerName: 'Sample Aerospace',
    salesOrderNumber: 'TRAIN-2402',
    salesOrderUrl: null,
    jobNumber: 'TRAIN-J1002',
    jobUrl: null,
    requiredQuantity: 50,
    jobQuantity: 50,
    requiredQuantitySource: 'Manual',
    jobQuantitySource: 'Manual',
    quantityLastSyncProvider: null,
    quantityLastSyncedAt: null,
    currentTask: 'Final Inspection',
    programStart: '2026-08-17',
    targetDelivery: '2026-10-02',
    completedOn: null,
    progress: 0.64,
    status: 'OnTrack',
    daysBehind: null,
    requiresImportCompletion: false,
    missingImportFields: [],
    tasks: [
      operation(9201, 9002, 10, 'Production Planning', 'Engineering', '2026-08-17', '2026-08-20', 1, 'Complete', 'Planning complete.'),
      operation(9203, 9002, 15, 'Engineering Release Support', 'Engineering', '2026-08-24', '2026-08-25', 0.2, 'OnTrack', 'Scheduled overlap included for capacity-review training.', 9201),
      operation(9202, 9002, 20, 'Final Inspection', 'Quality', '2026-09-21', '2026-09-24', 0.2, 'OnTrack', 'First article inspection scheduled.', 9201),
    ],
  },
  {
    id: 9003,
    version: 1,
    programName: 'DEMO-1003',
    programManager: 'Jordan Bell',
    engineer: 'Casey Park',
    salesPerson: 'Morgan Reed',
    customerName: 'Example Systems',
    salesOrderNumber: 'TRAIN-2403',
    salesOrderUrl: null,
    jobNumber: 'TRAIN-J1003',
    jobUrl: null,
    requiredQuantity: null,
    jobQuantity: null,
    requiredQuantitySource: null,
    jobQuantitySource: null,
    quantityLastSyncProvider: null,
    quantityLastSyncedAt: null,
    currentTask: 'Engineering Review',
    programStart: '2026-09-28',
    targetDelivery: '2026-10-16',
    completedOn: null,
    progress: 0.12,
    status: 'NotStarted',
    daysBehind: null,
    requiresImportCompletion: false,
    missingImportFields: [],
    tasks: [
      operation(9301, 9003, 10, 'Engineering Review', 'Engineering', '2026-09-28', '2026-09-30', 0, 'NotStarted', 'Training project has not started.'),
      operation(9302, 9003, 20, 'CNC Machining', 'Mill 03', '2026-10-01', '2026-10-09', 0, 'NotStarted', 'Scheduled after engineering review.', 9301),
    ],
  },
  {
    id: 8998,
    version: 1,
    programName: 'DEMO-0998',
    programManager: 'Alex Morgan',
    engineer: 'Jamie Lee',
    salesPerson: 'Morgan Reed',
    customerName: 'Training Customer',
    salesOrderNumber: 'TRAIN-2398',
    salesOrderUrl: null,
    jobNumber: 'TRAIN-J0998',
    jobUrl: null,
    requiredQuantity: 24,
    jobQuantity: 24,
    requiredQuantitySource: 'Manual',
    jobQuantitySource: 'Manual',
    quantityLastSyncProvider: null,
    quantityLastSyncedAt: null,
    currentTask: null,
    programStart: '2026-07-13',
    targetDelivery: '2026-08-14',
    completedOn: '2026-08-13',
    progress: 1,
    status: 'Complete',
    daysBehind: null,
    plannedStart: '2026-07-13',
    plannedFinish: '2026-08-14',
    actualStart: '2026-07-13',
    actualFinish: '2026-08-13',
    scheduleVarianceDays: -1,
    schedulePerformance: 'On time',
    requiresImportCompletion: false,
    missingImportFields: [],
    tasks: [operation(9081, 8998, 10, 'Completed Training Operation', 'Quality', '2026-08-10', '2026-08-13', 1, 'Complete', 'Completed one day ahead of target.')],
  },
]

function summary(project: ProjectDetail, priorityRank: number | null): ProjectSummary {
  return {
    id: project.id,
    version: project.version,
    programName: project.programName,
    programManager: project.programManager,
    engineer: project.engineer,
    salesPerson: project.salesPerson,
    customerName: project.customerName,
    salesOrderNumber: project.salesOrderNumber,
    salesOrderUrl: project.salesOrderUrl,
    jobNumber: project.jobNumber,
    jobUrl: project.jobUrl,
    requiredQuantity: project.requiredQuantity,
    jobQuantity: project.jobQuantity,
    requiredQuantitySource: project.requiredQuantitySource,
    jobQuantitySource: project.jobQuantitySource,
    currentTask: project.currentTask,
    priorityRank,
    progress: project.progress,
    targetDelivery: project.targetDelivery,
    finalCompletionDate: project.completedOn,
    daysLeft: project.status === 'Complete' ? null : project.id === 9001 ? 17 : project.id === 9002 ? 31 : 45,
    daysBehind: project.daysBehind,
    status: project.status,
    taskCount: project.tasks.length,
    behindTaskCount: project.tasks.filter((task) => task.status === 'Behind').length,
    recentNote: project.tasks[0]?.notes ? { note: project.tasks[0].notes, step: project.tasks[0].title, at: '2026-08-21T15:30:00Z' } : null,
    plannedStart: project.plannedStart,
    plannedFinish: project.plannedFinish,
    actualStart: project.actualStart,
    actualFinish: project.actualFinish,
    scheduleVarianceDays: project.scheduleVarianceDays,
    schedulePerformance: project.schedulePerformance,
  }
}

export const TRAINING_PROJECT_SUMMARIES = TRAINING_PROJECT_DETAILS.map((project, index) => summary(project, project.status === 'Complete' ? null : index + 1))

export const TRAINING_DASHBOARD: Dashboard = {
  activeProjects: 3,
  onTrackProjects: 1,
  behindProjects: 1,
  averageProgress: 0.41,
  nearestDelivery: '2026-09-18',
  projects: TRAINING_PROJECT_SUMMARIES,
}

export function trainingMetadata(project: ProjectDetail): ProjectMetadataDraft {
  return {
    programName: project.programName,
    programManager: project.programManager ?? '',
    engineer: project.engineer ?? '',
    salesPerson: project.salesPerson ?? '',
    customerName: project.customerName ?? '',
    salesOrderNumber: project.salesOrderNumber ?? '',
    salesOrderUrl: project.salesOrderUrl ?? '',
    jobNumber: project.jobNumber ?? '',
    jobUrl: project.jobUrl ?? '',
    requiredQuantity: project.requiredQuantity === null ? '' : String(project.requiredQuantity),
    jobQuantity: project.jobQuantity === null ? '' : String(project.jobQuantity),
  }
}
