import type { AccessGroup } from './types'

export const estimatingImportPermissions = {
  moduleView: 'estimating.view',
  historyView: 'estimating.history.view',
  historyImport: 'estimating.history.import',
} as const

export function hasEstimatingHistoryImport(group: AccessGroup) {
  return group.permissions.includes(estimatingImportPermissions.historyImport)
}

export function canGrantEstimatingHistoryImport(group: AccessGroup) {
  return group.permissions.includes(estimatingImportPermissions.moduleView)
    && group.permissions.includes(estimatingImportPermissions.historyView)
}
