import assert from 'node:assert/strict'
import test from 'node:test'

import { ARDA_STATUS_OPTIONS, statusAgeLabel } from '../src/quoteWorkflowApi.ts'

test('Arda workflow status options stay distinct from Fulcrum statuses', () => {
  assert.deepEqual(ARDA_STATUS_OPTIONS, [
    'Not started',
    'In progress',
    'Waiting on information',
    'Ready for review',
    'Complete',
    'On hold',
  ])
})

test('status age is calculated from the server status-change timestamp', () => {
  const now = new Date('2026-09-03T18:00:00.000Z')
  assert.equal(statusAgeLabel(null, now), 'Not set')
  assert.equal(statusAgeLabel('2026-09-03T12:00:00.000Z', now), 'Set today')
  assert.equal(statusAgeLabel('2026-09-02T12:00:00.000Z', now), 'Set 1 day ago')
  assert.equal(statusAgeLabel('2026-08-29T12:00:00.000Z', now), 'Set 5 days ago')
})
