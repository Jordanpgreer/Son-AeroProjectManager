import test from 'node:test'
import assert from 'node:assert/strict'
import { buildActivityTimeline, timelineSummary } from '../src/vendor-quotes/activityTimelineModel.ts'
import { rateRequestThread } from '../src/vendor-quotes/rateRequestModel.ts'
import type { QuoteActivity, VendorDetail, VendorMessage } from '../src/vendor-quotes/types.ts'

const importedAt = '2026-09-16T13:00:00.100Z'
const message = (id: number, changes: Partial<VendorMessage> = {}): VendorMessage => ({
  id, direction: 'incoming', subject: 'Quote 4395', fromAddress: 'vendor@example.test', fromName: 'Vendor',
  toAddresses: ['estimator@example.test'], sentAt: '2026-09-16T11:00:00Z', receivedAt: '2026-09-16T11:01:00Z',
  importedAt, bodyText: 'Pricing for the requested material.', attachments: [], ...changes,
})
const activity = (id: string, changes: Partial<QuoteActivity> = {}): QuoteActivity => ({
  id, kind: 'email', text: 'Email received from Vendor', oldValue: null, newValue: 'Quote 4395',
  occurredAt: '2026-09-16T13:00:00.110Z', accountName: 'ESTIMATOR', displayName: 'Estimator',
  requestId: null, vendorName: null, partNumber: null, ...changes,
})
const thread = (id: number, messages: VendorMessage[]): VendorDetail => ({
  request: { id, quoteHistoryId: 42, quoteNumber: 4395, customer: 'Client', estimatingRep: 'Estimator',
    vendorName: 'Vendor', vendorEmail: 'vendor@example.test', partNumber: 'PART-1', title: 'Material RFQ', status: 'Waiting on vendor',
    statusChangedAt: importedAt, statusChangedBy: 'Estimator', followUpDate: null, createdAt: importedAt, updatedAt: importedAt,
    lastMessageAt: importedAt, messageCount: messages.length, noteCount: 0, version: 1, canEdit: true },
  activity: [], messages,
})

test('the unified timeline shows imported mail once and sorts by communication time', () => {
  const note = activity('quote-2', { kind: 'note', text: 'Pricing checked.', newValue: null, occurredAt: '2026-09-16T12:00:00Z' })
  const entries = buildActivityTimeline([activity('quote-1'), note], [message(1), message(1)])
  assert.equal(entries.length, 2)
  assert.equal(entries[0].key, 'activity-quote-2')
  assert.equal(entries[1].key, 'email-1')
  assert.equal(entries[1].occurredAt, '2026-09-16T11:01:00Z')
  assert.equal(entries[1].type === 'email' && entries[1].importActivity?.id, 'quote-1')
})

test('matching a subject alone never hides an import from a different time or RFQ', () => {
  const emails = [message(1)]
  const events = [activity('thread-1', { requestId: 2 }), activity('quote-2', { occurredAt: '2026-09-15T13:00:00Z' })]
  const entries = buildActivityTimeline(events, emails, [thread(1, emails)])
  assert.equal(entries.length, 3)
  assert.equal(entries.filter(entry => entry.type === 'activity').length, 2)
})

test('repeated quote subjects consume distinct imports without hiding additional history', () => {
  const emails = [message(1), message(2)]
  const events = [activity('quote-1'), activity('quote-2'), activity('quote-3')]
  const entries = buildActivityTimeline(events, emails)
  assert.equal(entries.filter(entry => entry.type === 'email').length, 2)
  assert.equal(entries.filter(entry => entry.type === 'activity').length, 1)
})

test('email assignment, removal and restore audit entries stay visible alongside messages', () => {
  const events = [activity('quote-1', { text: 'Email assigned to this thread' }),
    activity('quote-2', { kind: 'email-removed', text: 'Email removed from Arda (#1)' }),
    activity('quote-3', { kind: 'email-restored', text: 'Email restored to Arda (#1)' })]
  const entries = buildActivityTimeline(events, [message(1)])
  assert.equal(entries.length, 4)
  assert.equal(timelineSummary(events[1]), 'Email removed from Arda')
})

test('notes and status changes remain independently reviewable even when recorded together', () => {
  const note = activity('quote-1', { kind: 'note', text: 'Received supplier pricing.', newValue: null })
  const status = activity('audit-2', { kind: 'status', oldValue: 'RFQ Sent', newValue: 'Ready for Review' })
  const entries = buildActivityTimeline([note, status])
  assert.equal(entries.length, 2)
  assert.equal(timelineSummary(note), 'Added a note.')
  assert.equal(timelineSummary(status), 'Status set to Ready for Review.')
})

test('outgoing messages use their sent timestamp and carry their RFQ context', () => {
  const email = message(1, { direction: 'outgoing' })
  const entries = buildActivityTimeline([], [email], [thread(8, [email])])
  assert.equal(entries[0].occurredAt, email.sentAt)
  assert.equal(entries[0].requestId, 8)
  assert.equal(entries[0].threadName, 'Vendor')
  assert.equal(entries[0].partNumber, 'PART-1')
})

test('rate labels are offered only for sent messages belonging to an RFQ', () => {
  const sent = message(1, { direction: 'outgoing' })
  const incoming = message(2)
  const rfq = thread(8, [sent, incoming])
  assert.equal(rateRequestThread(sent, [rfq])?.id, 8)
  assert.equal(rateRequestThread(incoming, [rfq]), null)
  assert.equal(rateRequestThread(sent, [thread(9, [message(3)])]), null)
  assert.equal(rateRequestThread(sent, []), null)
})
