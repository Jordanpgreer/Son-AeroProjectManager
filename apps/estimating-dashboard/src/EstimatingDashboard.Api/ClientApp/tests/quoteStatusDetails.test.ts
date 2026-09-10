import test from 'node:test'
import assert from 'node:assert/strict'
import type { PersonalQuote } from '../src/quoteWorkflowApi.ts'
import { quoteDetailsDirty, quoteDetailsDraft, quoteDetailsUpdate, rebaseQuoteDetailsAfterActivity, setQuoteDueDate, useAutomaticQuoteDate } from '../src/vendor-quotes/quoteDetailsModel.ts'
import { makeUpdate, synchronizeActivityDraft } from '../src/vendor-quotes/model.ts'

const quote: PersonalQuote = {
  id: 17, quoteNumber: 4445, customer: 'Example Aerospace', fulcrumQuoteStatus: 'Open', estimatingRep: 'Jordan Greer', totalValue: 45000,
  rfqDueDate: '2026-09-18T00:00:00', automaticEstimatingDueDate: '2026-09-16T00:00:00', estimatingDueDate: '2026-09-15T00:00:00',
  estimatingDueDateIsOverride: true, ardaStatus: 'RFQ Sent', ardaStatusNotes: 'Waiting for SiliconePrime pricing.',
  ardaStatusChangedAt: '2026-09-10T15:00:00Z', ardaStatusChangedBy: 'Jordan Greer', version: 7,
}

test('quote details preserve the existing status summary and due-date override when opened', () => {
  const draft = quoteDetailsDraft(quote)
  assert.deepEqual(draft, { status: 'RFQ Sent', notes: 'Waiting for SiliconePrime pricing.', dueDate: '2026-09-15', dueDateIsOverride: true })
  assert.equal(quoteDetailsDirty(draft, quote), false)
  assert.equal(quoteDetailsDirty({ ...draft, notes: 'Vendor pricing received.' }, quote), true)
})

test('using automatic dates removes the override without changing status or notes', () => {
  const draft = useAutomaticQuoteDate(quoteDetailsDraft(quote), quote.automaticEstimatingDueDate)
  assert.equal(draft.dueDate, '2026-09-16')
  assert.equal(draft.dueDateIsOverride, false)
  assert.equal(draft.status, 'RFQ Sent')
  assert.equal(draft.notes, quote.ardaStatusNotes)
  assert.equal(quoteDetailsDirty(draft, quote), true)
  assert.equal(quoteDetailsUpdate(draft, quote).estimatingDueDateOverride, null)
})

test('clearing a custom due date restores the automatic date instead of saving an empty override', () => {
  const restored = setQuoteDueDate(quoteDetailsDraft(quote), '', quote.automaticEstimatingDueDate)
  assert.equal(restored.dueDate, '2026-09-16')
  assert.equal(restored.dueDateIsOverride, false)
  const withoutAutomatic = setQuoteDueDate(quoteDetailsDraft(quote), '', null)
  assert.equal(withoutAutomatic.dueDate, '')
  assert.equal(withoutAutomatic.dueDateIsOverride, false)
})

test('setting a date creates a custom override and submits the current concurrency version', () => {
  const draft = setQuoteDueDate({ ...quoteDetailsDraft(quote), status: 'Ready for Review', notes: ' Pricing received. ' }, '2026-09-20', quote.automaticEstimatingDueDate)
  assert.deepEqual(quoteDetailsUpdate(draft, quote), { ardaStatus: 'Ready for Review', notes: 'Pricing received.', estimatingDueDateOverride: '2026-09-20', expectedVersion: 7 })
})

test('quotes without an explicit status or dates have a usable editor without fabricated dates', () => {
  const blank = { ...quote, ardaStatus: null, ardaStatusNotes: null, estimatingDueDate: null, automaticEstimatingDueDate: null, estimatingDueDateIsOverride: false }
  const draft = quoteDetailsDraft(blank)
  assert.deepEqual(draft, { status: 'Untouched', notes: '', dueDate: '', dueDateIsOverride: false })
  assert.equal(quoteDetailsDirty(draft, blank), false)
  assert.deepEqual(quoteDetailsUpdate(draft, blank), { ardaStatus: 'Untouched', notes: null, estimatingDueDateOverride: null, expectedVersion: 7 })
})

test('saving an activity note advances the pending overview version without changing its edits', () => {
  const draft = { ...quoteDetailsDraft(quote), notes: 'Compare vendor lead times.' }
  const afterActivity = { ...quote, version: 8 }
  const baseline = rebaseQuoteDetailsAfterActivity(quote, afterActivity)
  assert.equal(quoteDetailsUpdate(draft, baseline).expectedVersion, 8)
  assert.equal(quoteDetailsUpdate(draft, baseline).notes, 'Compare vendor lead times.')
})

test('a conflicting overview refresh retains the original version to prevent overwriting another edit', () => {
  const draft = { ...quoteDetailsDraft(quote), status: 'Ready for Review' as const }
  for (const changed of [
    { ...quote, version: 8, ardaStatusNotes: 'Another estimator updated the summary.' },
    { ...quote, version: 8, estimatingDueDate: '2026-09-21T00:00:00' },
    { ...quote, version: 8, ardaStatus: 'On Hold' as const },
  ]) {
    const baseline = rebaseQuoteDetailsAfterActivity(quote, changed)
    assert.equal(quoteDetailsUpdate(draft, baseline).expectedVersion, 7)
  }
})

test('saving inline quote details preserves the drafted activity note and follow-up with the new status', () => {
  const previous = { status: 'RFQ Sent', followUpDate: '' }
  const draft = { ...previous, followUpDate: '2026-09-14', note: 'Ask Silicone Prime to confirm the lead time.' }
  const next = { status: 'Ready for Review', followUpDate: '' }
  assert.deepEqual(makeUpdate(synchronizeActivityDraft(draft, previous, next, true), 8), {
    expectedVersion: 8, status: 'Ready for Review', followUpDate: '2026-09-14', note: draft.note,
  })
})

test('an independent thread status draft remains intact when its parent record refreshes', () => {
  const previous = { status: 'Waiting on vendor', followUpDate: '' }
  const draft = { ...previous, status: 'Quote received', note: 'Pricing received for SP-2040.' }
  assert.equal(synchronizeActivityDraft(draft, previous, { ...previous, status: 'Reply received' }, false).status, 'Quote received')
})
