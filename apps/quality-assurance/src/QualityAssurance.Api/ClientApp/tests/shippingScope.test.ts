import assert from 'node:assert/strict'
import test from 'node:test'
import { normalizeShippingScope } from '../src/shippingScope.ts'

test('keeps only scopes allowed by the current permissions', () => {
  assert.equal(normalizeShippingScope('all', false, true), 'all')
  assert.equal(normalizeShippingScope('team', true, false), 'team')
  assert.equal(normalizeShippingScope('team', false, true), 'team')
  assert.equal(normalizeShippingScope('mine', false, false), 'mine')
})

test('falls back to mine for stale, unauthorized, or invalid scopes', () => {
  assert.equal(normalizeShippingScope('all', true, false), 'mine')
  assert.equal(normalizeShippingScope('team', false, false), 'mine')
  assert.equal(normalizeShippingScope('unexpected', true, true), 'mine')
  assert.equal(normalizeShippingScope(null, true, true), 'mine')
})
