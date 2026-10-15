import assert from 'node:assert/strict'
import test from 'node:test'

import { formatCompletionDays } from '../src/format.ts'

test('formatCompletionDays formats singular and fractional completion times', () => {
  assert.equal(formatCompletionDays(1), '1 day')
  assert.equal(formatCompletionDays(2.25), '2.3 days')
})

test('formatCompletionDays handles missing values', () => {
  assert.equal(formatCompletionDays(null), 'Not recorded')
  assert.equal(formatCompletionDays(undefined), 'Not recorded')
})
