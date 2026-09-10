import test from 'node:test'
import assert from 'node:assert/strict'
import { canAssignMessageToContact, isOverdue, isUpdateDirty, makeUpdate, mailtoVendor, quoteFromHash, syncState } from '../src/vendor-quotes/model.ts'
import type { VendorSync } from '../src/vendor-quotes/types.ts'

test('quote links match an entire positive numeric quote identifier', () => {
  assert.equal(quoteFromHash('#/quote-status?quote=4445'), 4445)
  assert.equal(quoteFromHash('#/quote-status?quote=44450'), 44450)
  assert.equal(quoteFromHash('#/quote-status?quote=4445abc'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=0'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=-4445'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=9007199254740992'), null)
  assert.equal(quoteFromHash('#/quote-status'), null)
})

test('a due date is overdue only after its local calendar day and while open', () => {
  const today = new Date(2026, 8, 10, 23, 59)
  assert.equal(isOverdue('2026-09-10T00:00:00', 'RFQ Sent', today), false)
  assert.equal(isOverdue('2026-09-09', 'Waiting on vendor', today), true)
  assert.equal(isOverdue('2026-09-09', 'Complete', today), false)
  assert.equal(isOverdue('2026-09-09', 'Accepted', today), false)
  assert.equal(isOverdue(null, 'In progress', today), false)
  assert.equal(isOverdue('invalid', 'In progress', today), false)
})

test('status-only and follow-up-only changes are protected as unsaved updates', () => {
  assert.equal(isUpdateDirty({ status: 'RFQ Sent', followUpDate: '', note: '' }, 'Untouched', null), true)
  assert.equal(isUpdateDirty({ status: 'Untouched', followUpDate: '2026-09-11', note: '' }, 'Untouched', null), true)
  assert.equal(isUpdateDirty({ status: 'Untouched', followUpDate: '', note: 'RFQ sent to SiliconePrime' }, 'Untouched', null), true)
  assert.equal(isUpdateDirty({ status: 'Untouched', followUpDate: '2026-09-11', note: '  ' }, 'Untouched', '2026-09-11T00:00:00'), false)
})

test('an internal update keeps the concurrency version, status and trimmed note together', () => {
  assert.deepEqual(makeUpdate({ status: 'Rates requested', followUpDate: '2026-09-11', note: ' RFQ sent to SiliconePrime\nAwaiting pricing. ' }, 7), {
    expectedVersion: 7, status: 'Rates requested', followUpDate: '2026-09-11', note: 'RFQ sent to SiliconePrime\nAwaiting pricing.',
  })
  assert.deepEqual(makeUpdate({ status: 'Untouched', followUpDate: '', note: '  ' }, 0), { expectedVersion: 0, status: 'Untouched', followUpDate: null, note: null })
})

test('Outlook compose uses the canonical subject and never includes an internal note', () => {
  const link = mailtoVendor('quotes+arda@example.com', 4445)
  assert.equal(link, 'mailto:quotes%2Barda%40example.com?subject=Quote%204445')
  assert.equal(new URL(link).searchParams.has('body'), false)
})

const heartbeat: VendorSync = { mailbox: 'estimator@example.com', clientName: 'Outlook', lastCheckedAt: '2026-09-10T15:00:00Z', lastSuccessAt: '2026-09-10T15:00:00Z', importedCount: 2, duplicateCount: 0, deferredCount: 0, error: null }

test('connection health reports no evidence, recent heartbeat, pause, and an error distinctly', () => {
  const now = new Date('2026-09-10T15:03:00Z')
  assert.equal(syncState([], now).label, 'Outlook not connected')
  assert.equal(syncState([heartbeat], now).label, 'Outlook connected')
  assert.equal(syncState([heartbeat], new Date('2026-09-10T15:20:00Z')).label, 'Outlook sync paused')
  assert.equal(syncState([{ ...heartbeat, error: 'Outlook could not be opened' }], now).label, 'Outlook needs attention')
})

test('email assignment choices match the specific contact while permitting internal threads', () => {
  const incoming = { direction: 'incoming', fromAddress: 'Quotes@SiliconePrime.example', toAddresses: ['estimator@example.com'] }
  assert.equal(canAssignMessageToContact(incoming, 'quotes@siliconeprime.example'), true)
  assert.equal(canAssignMessageToContact(incoming, 'sales@atlasrubber.example'), false)
  assert.equal(canAssignMessageToContact(incoming, ''), true)
  const outgoing = { direction: 'outgoing', fromAddress: 'estimator@example.com', toAddresses: ['quotes@siliconeprime.example', 'sales@atlasrubber.example'], vendorEmail: 'quotes@siliconeprime.example' }
  assert.equal(canAssignMessageToContact(outgoing, 'quotes@siliconeprime.example'), true)
  assert.equal(canAssignMessageToContact(outgoing, 'sales@atlasrubber.example'), false)
})
