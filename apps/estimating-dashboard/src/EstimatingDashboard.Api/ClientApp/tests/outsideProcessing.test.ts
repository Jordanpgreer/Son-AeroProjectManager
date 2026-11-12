import assert from 'node:assert/strict'
import test from 'node:test'
import { createOutsideProcessingDraft, loadOutsideProcessing } from '../src/vendor-quotes/outsideProcessingApi.ts'

test('outside processing loads with authentication, cancellation and the request header', async () => {
  const original = globalThis.fetch
  const signal = new AbortController().signal
  let request: { path: string; init?: RequestInit } | undefined
  globalThis.fetch = async (path, init) => {
    request = { path: String(path), init }
    return Response.json({ quoteHistoryId: 17, operations: [], warnings: ['Confirm quoted quantities.'] })
  }
  try {
    const result = await loadOutsideProcessing(17, signal)
    assert.equal(result.quoteHistoryId, 17)
    assert.deepEqual(result.warnings, ['Confirm quoted quantities.'])
    assert.equal(request?.path, '/api/quote-status/17/outside-processing')
    assert.equal(request?.init?.credentials, 'include')
    assert.equal(request?.init?.signal, signal)
    assert.equal(new Headers(request?.init?.headers).get('X-Arda-Request'), 'vendor-quotes')
  } finally { globalThis.fetch = original }
})

test('outside processing draft requests encode the operation as one segment and return the email', async () => {
  const original = globalThis.fetch
  let request: { path: string; init?: RequestInit } | undefined
  globalThis.fetch = async (path, init) => {
    request = { path: String(path), init }
    return new Response('X-Unsent: 1\r\nSubject: Quote 17 - Plating\r\n\r\nDraft', { headers: { 'Content-Type': 'message/rfc822; charset=utf-8' } })
  }
  try {
    const blob = await createOutsideProcessingDraft(17, 'part/operation?revision=A')
    assert.equal(request?.path, '/api/quote-status/17/outside-processing/part%2Foperation%3Frevision%3DA/draft')
    assert.equal(request?.init?.method, 'POST')
    assert.equal(request?.init?.credentials, 'include')
    assert.equal(new Headers(request?.init?.headers).get('X-Arda-Request'), 'vendor-quotes')
    assert.match(await blob.text(), /X-Unsent: 1/)
  } finally { globalThis.fetch = original }
})

test('outside processing preserves server permission and stale-review failures', async () => {
  const original = globalThis.fetch
  try {
    for (const status of [403, 409]) {
      globalThis.fetch = async () => Response.json({ code: 'OutsideProcessingBlocked', message: 'Review the current routing before preparing a draft.' }, { status })
      await assert.rejects(createOutsideProcessingDraft(17, 'operation-1'), {
        status, code: 'OutsideProcessingBlocked', message: 'Review the current routing before preparing a draft.',
      })
    }
  } finally { globalThis.fetch = original }
})

test('an expired session or unexpected HTML never becomes an outside processing email download', async () => {
  const original = globalThis.fetch
  try {
    globalThis.fetch = async () => new Response('<html>Sign in</html>', { status: 401, headers: { 'Content-Type': 'text/html' } })
    await assert.rejects(createOutsideProcessingDraft(17, 'operation-1'), { status: 401 })
    globalThis.fetch = async () => new Response('<html>Sign in</html>', { headers: { 'Content-Type': 'text/html' } })
    await assert.rejects(createOutsideProcessingDraft(17, 'operation-1'), /The Outlook draft was not returned/)
  } finally { globalThis.fetch = original }
})
