export type EstimatingRole = 'Viewer' | 'Editor' | 'Admin'

export interface EstimatingMe {
  accountName: string
  displayName: string
  moduleKey: 'estimating'
  role: EstimatingRole
  permissions: string[]
  isPreview: boolean
  previewActorAccountName: string | null
  previewTargetKey: string | null
  previewTargetTitle: string | null
}

export const estimatingPermissions = {
  view: 'estimating.view',
  calculate: 'estimating.calculate',
  manageQuotes: 'estimating.quotes.manage',
  manageInputs: 'estimating.inputs.manage',
  administerRates: 'estimating.rates.admin',
  administerSettings: 'estimating.settings.admin',
  viewHistory: 'estimating.history.view',
  importHistory: 'estimating.history.import',
  manageHistory: 'estimating.history.manage',
} as const

export function hasEstimatingPermission(
  me: EstimatingMe | null,
  permission: string,
) {
  return Boolean(me?.permissions.includes(permission))
}
