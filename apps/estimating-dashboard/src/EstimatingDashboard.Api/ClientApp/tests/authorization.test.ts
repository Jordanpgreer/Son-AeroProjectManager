import assert from 'node:assert/strict'
import test from 'node:test'

import {
  estimatingPermissions,
  hasEstimatingPermission,
  type EstimatingMe,
} from '../src/authorization.ts'

const viewer: EstimatingMe = {
  accountName: 'SONAERO\\viewer',
  displayName: 'Viewer',
  moduleKey: 'estimating',
  role: 'Viewer',
  permissions: [
    estimatingPermissions.view,
    estimatingPermissions.calculate,
  ],
}

test('viewer can calculate but cannot persist quote changes', () => {
  assert.equal(
    hasEstimatingPermission(viewer, estimatingPermissions.calculate),
    true,
  )
  assert.equal(
    hasEstimatingPermission(viewer, estimatingPermissions.manageQuotes),
    false,
  )
  assert.equal(
    hasEstimatingPermission(viewer, estimatingPermissions.manageInputs),
    false,
  )
})

test('missing user has no Estimating permissions', () => {
  assert.equal(
    hasEstimatingPermission(null, estimatingPermissions.view),
    false,
  )
})
