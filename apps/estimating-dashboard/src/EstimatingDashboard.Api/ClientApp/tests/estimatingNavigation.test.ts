import assert from 'node:assert/strict'
import test from 'node:test'
import { estimatingPermissions } from '../src/authorization.ts'
import {
  canViewEstimatingPage,
  firstAccessibleEstimatingPage,
  pageFromHash,
} from '../src/estimatingNavigation.ts'

test('page permissions are independent and preserve navigation order', () => {
  const permissions = [estimatingPermissions.calculatorView, estimatingPermissions.viewHistory]

  assert.equal(canViewEstimatingPage(permissions, 'quotes'), false)
  assert.equal(canViewEstimatingPage(permissions, 'calculator'), true)
  assert.equal(canViewEstimatingPage(permissions, 'history'), true)
  assert.equal(firstAccessibleEstimatingPage(permissions), 'calculator')
  assert.equal(firstAccessibleEstimatingPage([]), null)
})

test('hash routes retain calculator compatibility without granting access implicitly', () => {
  assert.equal(pageFromHash('#/quote-status?quote=42'), 'quote-status')
  assert.equal(pageFromHash('#/fulcrum-builder'), 'calculator')
  assert.equal(pageFromHash('#/not-a-page'), null)
})
