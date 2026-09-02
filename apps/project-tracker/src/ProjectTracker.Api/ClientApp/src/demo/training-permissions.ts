import { permissionKeys } from '../permissions.ts'

export const VIEW_ONLY_PERMISSIONS = [permissionKeys.moduleView] as const

export const PROJECT_FIELD_TRAINING_LABELS = {
  [permissionKeys.projectEditProgramName]: 'part number',
  [permissionKeys.projectEditProgramManager]: 'contact lead',
  [permissionKeys.projectEditEngineer]: 'engineer',
  [permissionKeys.projectEditCustomerName]: 'customer',
  [permissionKeys.projectEditSalesOrderNumber]: 'sales order',
  [permissionKeys.projectEditJobNumber]: 'job number',
  [permissionKeys.projectEditQuantities]: 'required and job quantities',
  [permissionKeys.projectEditExternalLinks]: 'SO and job links',
} as const

export const OPERATION_FIELD_TRAINING_LABELS = {
  [permissionKeys.taskEditTitle]: 'operation name',
  [permissionKeys.taskEditWorkStation]: 'work station',
  [permissionKeys.taskEditDependency]: 'dependency',
  [permissionKeys.taskEditStartDateLocked]: 'start lock',
  [permissionKeys.taskEditStartDate]: 'start date',
  [permissionKeys.taskEditEndDate]: 'end date',
  [permissionKeys.taskEditOriginalStartDate]: 'original start',
  [permissionKeys.taskEditOriginalEndDate]: 'original end',
  [permissionKeys.taskEditEstimatedDuration]: 'duration',
  [permissionKeys.taskEditActualDuration]: 'original duration',
  [permissionKeys.taskEditPercentComplete]: 'completion',
  [permissionKeys.taskEditNotes]: 'notes',
  [permissionKeys.taskEditOvertimeDays]: 'overtime dates',
  [permissionKeys.taskReorder]: 'operation sequence',
} as const

export const ADMIN_TRAINING_LABELS = {
  [permissionKeys.settingsWorkCalendarManage]: 'work calendar',
  [permissionKeys.settingsHolidaysManage]: 'holidays',
  [permissionKeys.settingsWorkCentersManage]: 'work centers',
  [permissionKeys.settingsWorkCentersImport]: 'work-center imports',
  [permissionKeys.importManage]: 'controlled imports',
  [permissionKeys.accessManageUsers]: 'registered users',
  [permissionKeys.accessManageGroups]: 'permission groups',
} as const

export const TRAINING_PERMISSION_COVERAGE: Record<string, string> = {
  [permissionKeys.moduleView]: 'page-tours',
  [permissionKeys.projectCreate]: 'page-tour-static',
  [permissionKeys.projectEditPriority]: 'page-tour-static',
  [permissionKeys.projectComplete]: 'page-tour-static',
  [permissionKeys.projectArchive]: 'page-tour-static',
  [permissionKeys.projectReopen]: 'past-actions',
  [permissionKeys.archivedRestore]: 'past-actions',
  [permissionKeys.archivedDelete]: 'past-actions',
  [permissionKeys.projectActivityView]: 'page-tour-static',
  [permissionKeys.taskCreate]: 'project-edit-overview',
  [permissionKeys.taskDelete]: 'project-edit-overview',
  [permissionKeys.operationScheduleConfirm]: 'page-tour-static',
  ...Object.fromEntries(Object.keys(PROJECT_FIELD_TRAINING_LABELS).map((key) => [key, 'project-edit-overview'])),
  ...Object.fromEntries(Object.keys(OPERATION_FIELD_TRAINING_LABELS).map((key) => [key, 'project-edit-overview'])),
  ...Object.fromEntries(Object.keys(ADMIN_TRAINING_LABELS).map((key) => [key, 'admin-page-not-in-scope'])),
}

export function grantedLabels(
  permissions: readonly string[],
  labels: Record<string, string>,
) {
  const granted = new Set(permissions.map((permission) => permission.toLocaleLowerCase('en-US')))
  return Object.entries(labels)
    .filter(([permission]) => granted.has(permission.toLocaleLowerCase('en-US')))
    .map(([, label]) => label)
}

export function formatTrainingList(values: readonly string[]) {
  if (values.length === 0) return ''
  if (values.length === 1) return values[0]!
  if (values.length === 2) return `${values[0]} and ${values[1]}`
  return `${values.slice(0, -1).join(', ')}, and ${values.at(-1)}`
}
