import assert from 'node:assert/strict'
import test from 'node:test'

import {
  ARDA_STATUS_OPTIONS,
  loadPersonalQuoteStats,
  refreshPersonalQuoteAssignments,
  statusSetLabel,
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

test('status set label shows the exact server date and time', () => {
  assert.equal(statusSetLabel(null, 'UTC'), 'Not set')
  assert.equal(statusSetLabel('2026-09-03T12:15:00.000Z', 'UTC'), 'Sep 3, 2026, 12:15 PM')
  assert.equal(statusSetLabel('not-a-date', 'UTC'), 'Unknown')
})

test('dashboard statistics load from the personal quote workflow endpoint', async () => {
  const originalFetch = globalThis.fetch
  let request = ''
  globalThis.fetch = async (input) => {
    request = String(input)
    return new Response(JSON.stringify({
      averageCompletionWorkdays: 4.5,
      completionSampleSize: 12,
      wonCount: 7,
      lostCount: 2,
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  try {
    assert.deepEqual(await loadPersonalQuoteStats(), {
      averageCompletionWorkdays: 4.5,
      completionSampleSize: 12,
      wonCount: 7,
      lostCount: 2,
    })
    assert.equal(request, '/api/quote-workflow/stats')
  } finally {
    globalThis.fetch = originalFetch
  }
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
      url: '/api/quote-workflow/refresh?includeCompleted=true',
      method: 'POST',
    })
  } finally {
    globalThis.fetch = originalFetch
  }
})
