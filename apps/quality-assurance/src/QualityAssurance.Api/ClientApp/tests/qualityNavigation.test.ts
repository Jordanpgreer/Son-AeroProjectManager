import assert from 'node:assert/strict'
import test from 'node:test'
import {
  canViewQualityPage,
  firstAccessibleQualityPage,
  routeFromHash,
} from '../src/qualityNavigation.ts'

test('dashboard and shipping status can be granted independently', () => {
  const dashboardOnly = ['quality-assurance.dashboard.view']

  assert.equal(canViewQualityPage(dashboardOnly, 'dashboard'), true)
  assert.equal(canViewQualityPage(dashboardOnly, 'shipping-status'), false)
  assert.equal(firstAccessibleQualityPage(dashboardOnly), 'dashboard')
  assert.equal(firstAccessibleQualityPage(['quality-assurance.shipments.view']), 'shipping-status')
  assert.equal(firstAccessibleQualityPage([]), null)
})

test('shipping deep links resolve to the protected shipping page', () => {
  assert.equal(routeFromHash('#/shipping-status?shipment=17'), 'shipping-status')
  assert.equal(routeFromHash('#/dashboard'), 'dashboard')
})
