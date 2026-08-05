export const permissionKeys = {
  projectCreate: 'project.create',
  projectEditProgramName: 'project.edit.programName',
  projectEditProgramManager: 'project.edit.programManager',
  projectEditEngineer: 'project.edit.engineer',
  projectEditCustomerName: 'project.edit.customerName',
  projectEditSalesOrderNumber: 'project.edit.salesOrderNumber',
  projectEditJobNumber: 'project.edit.jobNumber',
  projectEditPriority: 'project.edit.priority',
  projectComplete: 'project.complete',
  projectReopen: 'project.reopen',
  projectArchive: 'project.archive',
  archivedRestore: 'archived.restore',
  taskCreate: 'task.create',
  taskDelete: 'task.delete',
  taskEditTitle: 'task.edit.title',
  taskEditWorkStation: 'task.edit.workStation',
  taskEditDependency: 'task.edit.dependency',
  taskEditStartDateLocked: 'task.edit.startDateLocked',
  taskEditStartDate: 'task.edit.startDate',
  taskEditEndDate: 'task.edit.endDate',
  taskEditOriginalStartDate: 'task.edit.originalStartDate',
  taskEditOriginalEndDate: 'task.edit.originalEndDate',
  taskEditEstimatedDuration: 'task.edit.estimatedDuration',
  taskEditActualDuration: 'task.edit.actualDuration',
  taskEditPercentComplete: 'task.edit.percentComplete',
  taskEditNotes: 'task.edit.notes',
  taskEditOvertimeDays: 'task.edit.overtimeDays',
  taskReorder: 'task.edit.sequence',
} as const

export const projectMetadataEditPermissions = [
  permissionKeys.projectEditProgramName,
  permissionKeys.projectEditProgramManager,
  permissionKeys.projectEditEngineer,
  permissionKeys.projectEditCustomerName,
  permissionKeys.projectEditSalesOrderNumber,
  permissionKeys.projectEditJobNumber,
]

export const taskFieldEditPermissions = [
  permissionKeys.taskEditTitle,
  permissionKeys.taskEditWorkStation,
  permissionKeys.taskEditDependency,
  permissionKeys.taskEditStartDateLocked,
  permissionKeys.taskEditStartDate,
  permissionKeys.taskEditEndDate,
  permissionKeys.taskEditOriginalStartDate,
  permissionKeys.taskEditOriginalEndDate,
  permissionKeys.taskEditEstimatedDuration,
  permissionKeys.taskEditActualDuration,
  permissionKeys.taskEditPercentComplete,
  permissionKeys.taskEditNotes,
  permissionKeys.taskEditOvertimeDays,
  permissionKeys.taskReorder,
]

export function hasPermission(permissions: readonly string[], permission: string) {
  return permissions.includes(permission)
}

export function hasAnyPermission(permissions: readonly string[], required: readonly string[]) {
  return required.some((permission) => permissions.includes(permission))
}
