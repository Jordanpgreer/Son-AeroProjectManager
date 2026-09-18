import test from 'node:test'
import assert from 'node:assert/strict'
import { canAssignMessageToContact, canonicalVendorStatus, copyQuoteFolderPath, dateTime, DEFAULT_QUOTE_STATUS_SCOPE, isOverdue, isUpdateDirty, makeUpdate, mailtoVendor, quoteFolderHref, quoteFromHash, quoteStatusRequestScope, shouldShowConnectOutlook, sortQuoteStatusesByDueDate, statusTone, syncState, VENDOR_STATUSES } from '../src/vendor-quotes/model.ts'
import type { QuoteStatusSummary, VendorSync } from '../src/vendor-quotes/types.ts'

test('quote links match an entire positive numeric quote identifier', () => {
  assert.equal(quoteFromHash('#/quote-status?quote=4445'), 4445)
  assert.equal(quoteFromHash('#/quote-status?quote=44450'), 44450)
  assert.equal(quoteFromHash('#/quote-status?quote=4445abc'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=0'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=-4445'), null)
  assert.equal(quoteFromHash('#/quote-status?quote=9007199254740992'), null)
  assert.equal(quoteFromHash('#/quote-status'), null)
})

test('quote status starts with my active quotes but direct links can resolve outside that scope', () => {
  assert.equal(DEFAULT_QUOTE_STATUS_SCOPE, 'mine-active')
  assert.equal(quoteStatusRequestScope(DEFAULT_QUOTE_STATUS_SCOPE, null), 'mine-active')
  assert.equal(quoteStatusRequestScope('all', null), 'all')
  assert.equal(quoteStatusRequestScope(DEFAULT_QUOTE_STATUS_SCOPE, 4445), 'all')
})

test('quote status rows sort by estimating due date and use quote number as a stable tie-breaker', () => {
  const summary = (quoteNumber: number, estimatingDueDate: string | null): QuoteStatusSummary => ({
    quoteHistoryId: quoteNumber, quoteNumber, customer: 'Client', estimatingRep: 'Estimator', status: 'In progress',
    statusChangedAt: '2026-09-18T14:35:00', statusChangedBy: 'Estimator', followUpDate: null, estimatingDueDate,
    updatedAt: '2026-09-18T14:35:00', lastMessageAt: null, threadCount: 0, messageCount: 0,
    unassignedMessageCount: 0, version: 1, canEdit: true, canRemove: false,
  })
  const sorted = sortQuoteStatusesByDueDate([
    summary(4401, null), summary(4402, '2026-09-22'), summary(4403, '2026-09-19'), summary(4404, '2026-09-19'),
  ])
  assert.deepEqual(sorted.map(item => item.quoteNumber), [4404, 4403, 4402, 4401])
})

test('status timestamps render the exact recorded date and minute', () => {
  const formatted = dateTime('2026-09-18T14:35:00')
  assert.match(formatted, /Sep 18, 2026/)
  assert.match(formatted, /2:35 PM/)
  assert.equal(dateTime(null), 'Not recorded')
})

test('a due date is overdue only after its local calendar day and while open', () => {
  const today = new Date(2026, 8, 10, 23, 59)
  assert.equal(isOverdue('2026-09-10T00:00:00', 'RFQ Sent', today), false)
  assert.equal(isOverdue('2026-09-09', 'Waiting on vendor', today), true)
  assert.equal(isOverdue('2026-09-09', 'Complete', today), false)
  assert.equal(isOverdue('2026-09-09', 'Accepted', today), false)
  assert.equal(isOverdue('2026-09-09', 'Quote received', today), false)
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
  assert.deepEqual(makeUpdate({ status: 'Waiting on vendor', followUpDate: '2026-09-11', note: ' RFQ sent to SiliconePrime\nAwaiting pricing. ' }, 7), {
    expectedVersion: 7, status: 'Waiting on vendor', followUpDate: '2026-09-11', note: 'RFQ sent to SiliconePrime\nAwaiting pricing.',
  })
  assert.deepEqual(makeUpdate({ status: 'Untouched', followUpDate: '', note: '  ' }, 0), { expectedVersion: 0, status: 'Untouched', followUpDate: null, note: null })
})

test('RFQs offer three statuses and normalize legacy state without treating every reply as pricing', () => {
  assert.deepEqual(VENDOR_STATUSES, ['Untouched', 'Waiting on vendor', 'Quote received'])
  for (const status of VENDOR_STATUSES) assert.equal(canonicalVendorStatus(status), status)
  for (const status of ['Rates requested', 'Reply received', ' waiting ON vendor ']) assert.equal(canonicalVendorStatus(status), 'Waiting on vendor')
  for (const status of ['Under review', 'Accepted', 'QUOTE RECEIVED']) assert.equal(canonicalVendorStatus(status), 'Quote received')
  for (const status of ['Declined', 'Cancelled', 'not-a-status', '', null, undefined]) assert.equal(canonicalVendorStatus(status), 'Untouched')
  assert.equal(statusTone('Quote received'), 'success')
  assert.equal(statusTone('Rates requested'), 'amber')
  assert.equal(statusTone('Reply received'), 'blue')
  assert.equal(statusTone('Accepted'), 'success')
})

test('Outlook compose uses the canonical subject and never includes an internal note', () => {
  const link = mailtoVendor('quotes+arda@example.com', 4445)
  assert.equal(link, 'mailto:quotes%2Barda%40example.com?subject=Quote%204445')
  assert.equal(new URL(link).searchParams.has('body'), false)
})

test('quote folder actions use the registered protocol and copy the exact server path', async () => {
  const path = 'S:\\Quotes\\2026\\Q-4460 Honeywell'
  assert.equal(quoteFolderHref(path), 'sonaero-folder://open?path=S%3A%5CQuotes%5C2026%5CQ-4460%20Honeywell')
  assert.equal(quoteFolderHref('  '), null)
  let copied = ''
  await copyQuoteFolderPath(path, { writeText: async value => { copied = value } })
  assert.equal(copied, path)
})

const heartbeat: VendorSync = { mailbox: 'estimator@example.com', clientName: 'Outlook', lastCheckedAt: '2026-09-10T15:00:00Z', lastSuccessAt: '2026-09-10T15:00:00Z', importedCount: 2, duplicateCount: 0, deferredCount: 0, error: null }

test('connection health reports no evidence, recent heartbeat, pause, and an error distinctly', () => {
  const now = new Date('2026-09-10T15:03:00Z')
  assert.equal(syncState([], now).label, 'Outlook not connected')
  assert.equal(syncState([heartbeat], now).label, 'Outlook connected')
  assert.equal(syncState([heartbeat], new Date('2026-09-10T15:20:00Z')).label, 'Outlook sync paused')
  assert.equal(syncState([{ ...heartbeat, error: 'Outlook could not be opened' }], now).label, 'Outlook needs attention')
})

test('Connect Outlook appears only after confirming that no Outlook connection exists', () => {
  assert.equal(shouldShowConnectOutlook(false, null, []), false)
  assert.equal(shouldShowConnectOutlook(true, null, []), true)
  assert.equal(shouldShowConnectOutlook(true, null, [heartbeat]), false)
  assert.equal(shouldShowConnectOutlook(true, 'Connection status is unavailable.', []), false)
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
