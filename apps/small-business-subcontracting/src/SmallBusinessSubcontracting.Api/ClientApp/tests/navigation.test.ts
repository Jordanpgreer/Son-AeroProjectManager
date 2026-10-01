import assert from 'node:assert/strict'
import test from 'node:test'
import { firstAccessiblePage, routeFromHash } from '../src/navigation.ts'

test('maps page hashes and preserves vendor deep links', () => {
  assert.deepEqual(routeFromHash('#/compliance'), { page: 'compliance', vendorId: null })
  assert.deepEqual(routeFromHash('#/vendors/42'), { page: 'vendors', vendorId: 42 })
  assert.deepEqual(routeFromHash('#/vendors'), { page: 'vendors', vendorId: null })
})

test('unknown routes fail safely to the vendor register', () => {
  assert.deepEqual(routeFromHash('#/compliance-archive'), { page: 'vendors', vendorId: null })
  assert.deepEqual(routeFromHash('#/vendors/not-a-number'), { page: 'vendors', vendorId: null })
})

test('selects the first page granted by permissions', () => {
  assert.equal(firstAccessiblePage(['small-business-subcontracting.vendors.view']), 'vendors')
  assert.equal(firstAccessiblePage(['small-business-subcontracting.dashboard.view']), 'compliance')
  assert.equal(firstAccessiblePage([]), null)
})
