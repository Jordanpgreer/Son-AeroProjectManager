import test from 'node:test'
import assert from 'node:assert/strict'
import { activityDescription, editableNoteId, matchingMoveThreads, nextMenuIndex } from '../src/vendor-quotes/lifecycleModel.ts'
import { editNote, moveEmail, removeEmail, restoreNote } from '../src/vendor-quotes/lifecycleApi.ts'
import type { QuoteActivity, VendorActivity, VendorRequest } from '../src/vendor-quotes/types.ts'

test('only explicit internal notes with canonical quote or thread identifiers offer mutations', () => {
  assert.equal(editableNoteId({ kind: 'note', id: 'quote-31' } as QuoteActivity), 'quote-31')
  assert.equal(editableNoteId({ kind: 'note', id: 12, activityId: 'thread-12' } as VendorActivity), 'thread-12')
  for (const kind of ['note-edited', 'note-removed', 'note-restored', 'status-changed', 'email-imported'])
    assert.equal(editableNoteId({ kind, id: 'quote-31' } as QuoteActivity), null)
  for (const id of ['workflow-12', 'thread-0', 'quote-../31', '31'])
    assert.equal(editableNoteId({ kind: 'note', id } as QuoteActivity), null)
  assert.equal(editableNoteId({ kind: 'note', id: 12 } as VendorActivity), null)
})

test('moving an email offers its actual counterparty and internal threads only', () => {
  const threads = [{ id: 1, vendorEmail: 'quotes@siliconeprime.example' }, { id: 2, vendorEmail: 'other@example.com' }, { id: 3, vendorEmail: '' }] as VendorRequest[]
  assert.deepEqual(matchingMoveThreads({ vendorEmail: ' Quotes@SiliconePrime.example ' }, threads).map(item => item.id), [1, 3])
  assert.deepEqual(matchingMoveThreads({}, threads).map(item => item.id), [3])
})

test('generated lifecycle audit identifiers are hidden without changing notes or original email subjects', () => {
  assert.equal(activityDescription({ kind: 'email-moved', text: 'Email moved (#19): Vendor reference (#77)' }), 'Email moved: Vendor reference (#77)')
  assert.equal(activityDescription({ kind: 'email-moved', text: 'Email moved here (#19): Pricing' }), 'Email moved here: Pricing')
  assert.equal(activityDescription({ kind: 'email-removed', text: 'Email removed from Arda (#19)' }), 'Email removed from Arda')
  assert.equal(activityDescription({ kind: 'email-restored', text: 'Email restored to Arda (#19)' }), 'Email restored to Arda')
  assert.equal(activityDescription({ kind: 'note-edited', text: 'Internal note edited (thread-12)' }), 'Internal note edited')
  const note = 'Email removed from Arda (#19)\nInternal note edited (thread-12)'
  assert.equal(activityDescription({ kind: 'note', text: note }), note)
  assert.equal(activityDescription({ kind: 'email-imported', text: 'Quote (#19)' }), 'Quote (#19)')
})

test('record menus support wrapping arrow navigation and first/last keyboard shortcuts', () => {
  assert.equal(nextMenuIndex(0, 'ArrowUp', 2), 1)
  assert.equal(nextMenuIndex(1, 'ArrowDown', 2), 0)
  assert.equal(nextMenuIndex(-1, 'ArrowUp', 2), 1)
  assert.equal(nextMenuIndex(1, 'Home', 2), 0)
  assert.equal(nextMenuIndex(0, 'End', 2), 1)
  assert.equal(nextMenuIndex(0, 'ArrowDown', 0), -1)
})

test('email moves send both quote versions and the explicit destination without editing the email', async context => {
  const result = { quote: { quoteHistoryId: 1, version: 9 } }
  context.mock.method(globalThis, 'fetch', async (url: string, options: RequestInit) => {
    assert.equal(url, '/api/quote-status/1/messages/7/move')
    assert.equal(options.method, 'POST')
    assert.equal(options.credentials, 'include')
    assert.equal((options.headers as Record<string, string>)['X-Arda-Request'], 'vendor-quotes')
    assert.deepEqual(JSON.parse(String(options.body)), { expectedVersion: 8, targetQuoteHistoryId: 2, targetExpectedVersion: 5, requestId: null })
    return Response.json(result)
  })
  assert.deepEqual(await moveEmail(1, 7, { expectedVersion: 8, targetQuoteHistoryId: 2, targetExpectedVersion: 5, requestId: null }), result)
})

test('note edits and restores retain canonical identities, text and source version', async context => {
  const requests: { url: string; method: string | undefined; body: unknown }[] = []
  context.mock.method(globalThis, 'fetch', async (url: string, options: RequestInit) => {
    requests.push({ url, method: options.method, body: JSON.parse(String(options.body)) })
    return Response.json({ quote: { version: 10 } })
  })
  await editNote(1, 'thread-12', 8, 'First line\nSecond line')
  await restoreNote(1, 'quote-9', 9)
  assert.deepEqual(requests, [
    { url: '/api/quote-status/1/notes/thread-12', method: 'PUT', body: { expectedVersion: 8, text: 'First line\nSecond line' } },
    { url: '/api/quote-status/1/notes/quote-9/restore', method: 'POST', body: { expectedVersion: 9 } },
  ])
})

test('a lifecycle version conflict surfaces the server recovery message and is never retried automatically', async context => {
  let attempts = 0
  context.mock.method(globalThis, 'fetch', async () => { attempts++; return Response.json({ message: 'This quote changed. Refresh and try again.' }, { status: 409 }) })
  await assert.rejects(removeEmail(1, 7, 8), /This quote changed\. Refresh and try again\./)
  assert.equal(attempts, 1)
})
