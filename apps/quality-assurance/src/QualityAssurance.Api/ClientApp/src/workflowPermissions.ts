import type { QualityAssuranceUser } from './types'

export type QualityWorkflowAction =
  | 'shipment-created'
  | 'shipment-imported'
  | 'shipment-updated'
  | 'assignment-changed'
  | 'qa-completed'
  | 'shipment-shipped'

export function canRunQualityAction(
  user: Pick<QualityAssuranceUser, 'workflowRestrictedActions'>,
  action: QualityWorkflowAction,
  existingPermission: boolean,
): boolean {
  return existingPermission && !(user.workflowRestrictedActions ?? []).includes(action)
}
