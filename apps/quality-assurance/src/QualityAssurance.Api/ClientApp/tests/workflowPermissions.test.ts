import assert from 'node:assert/strict'
import test from 'node:test'
import { canRunQualityAction } from '../src/workflowPermissions.ts'
import type { QualityWorkflowAction } from '../src/workflowPermissions.ts'

const actions: QualityWorkflowAction[] = [
  'shipment-created', 'shipment-imported', 'shipment-updated',
  'assignment-changed', 'qa-completed', 'shipment-shipped',
]

test('workflow restrictions never grant a permission the person does not already have', () => {
  for (const action of actions) {
    assert.equal(canRunQualityAction({}, action, false), false)
    assert.equal(canRunQualityAction({ workflowRestrictedActions: [] }, action, false), false)
    assert.equal(canRunQualityAction({ workflowRestrictedActions: [action] }, action, false), false)
  }
})

test('older access responses preserve existing permitted QA actions', () => {
  for (const action of actions) assert.equal(canRunQualityAction({}, action, true), true)
})

test('each restricted action is hidden independently of the other QA actions', () => {
  for (const restricted of actions) {
    const user = { workflowRestrictedActions: [restricted] }
    for (const action of actions) assert.equal(canRunQualityAction(user, action, true), action !== restricted)
  }
})

test('a refreshed access response immediately changes action eligibility', () => {
  const allowed = { workflowRestrictedActions: [] }
  const blocked = { workflowRestrictedActions: ['assignment-changed', 'qa-completed'] }
  assert.equal(canRunQualityAction(allowed, 'assignment-changed', true), true)
  assert.equal(canRunQualityAction(blocked, 'assignment-changed', true), false)
  assert.equal(canRunQualityAction(blocked, 'qa-completed', true), false)
  assert.equal(canRunQualityAction(blocked, 'shipment-created', true), true)
})
