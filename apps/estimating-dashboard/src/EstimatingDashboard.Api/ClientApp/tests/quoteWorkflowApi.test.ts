import assert from 'node:assert/strict'
import test from 'node:test'

import {
  ARDA_STATUS_OPTIONS,
  refreshPersonalQuoteAssignments,
  statusAgeLabel,
} from '../src/quoteWorkflowApi.ts'

test('Arda workflow status options stay distinct from Fulcrum statuses', () => {
  assert.deepEqual(ARDA_STATUS_OPTIONS, [
    'Untouched',
    'In progress',
    'RFQ Sent',
    'Ready for Review',
    'Complete',
    'On Hold',
  ])
})

test('status age is calculated from the server status-change timestamp', () => {
  const now = new Date('2026-09-03T18:00:00.000Z')
  assert.equal(statusAgeLabel(null, now), 'Not set')
  assert.equal(statusAgeLabel('2026-09-03T12:00:00.000Z', now), 'Set today')
  assert.equal(statusAgeLabel('2026-09-02T12:00:00.000Z', now), 'Set 1 day ago')
  assert.equal(statusAgeLabel('2026-08-29T12:00:00.000Z', now), 'Set 5 days ago')
})

test('dashboard refresh performs a server-side assignment refresh', async () => {
  const originalFetch = globalThis.fetch
  let request: { url: string, method?: string } | null = null
  globalThis.fetch = async (input, init) => {
    request = { url: String(input), method: init?.method }
    return new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  try {
    assert.deepEqual(await refreshPersonalQuoteAssignments(), [])
    assert.deepEqual(request, {
      url: '/api/quote-workflow/refresh',
      method: 'POST',
    })
  } finally {
    globalThis.fetch = originalFetch
  }
})
