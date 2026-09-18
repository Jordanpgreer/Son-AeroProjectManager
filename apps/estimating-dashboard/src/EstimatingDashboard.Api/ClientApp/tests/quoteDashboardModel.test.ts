import assert from 'node:assert/strict'
import test from 'node:test'

import { sortActivePersonalQuotes, sortCompletedPersonalQuotes } from '../src/quoteDashboardModel.ts'
import type { PersonalQuote } from '../src/quoteWorkflowApi.ts'

function quote(overrides: Partial<PersonalQuote>): PersonalQuote {
  return {
    id: 1,
    quoteNumber: 1001,
    customer: 'Example Aerospace',
    fulcrumQuoteStatus: 'Open',
    estimatingRep: 'Estimator',
    totalValue: 1000,
    rfqDueDate: null,
    automaticEstimatingDueDate: null,
    estimatingDueDate: null,
    estimatingDueDateIsOverride: false,
    estimatingCompletionDate: null,
    isCompleted: false,
    isOverdue: false,
    ardaStatus: null,
    ardaStatusNotes: null,
    ardaStatusChangedAt: null,
    ardaStatusChangedBy: null,
    version: 1,
    ...overrides,
  }
}

test('active personal quotes sort by estimating due date with undated quotes last', () => {
  const result = sortActivePersonalQuotes([
    quote({ id: 1, quoteNumber: 1001, estimatingDueDate: null }),
    quote({ id: 2, quoteNumber: 1002, estimatingDueDate: '2026-09-20' }),
    quote({ id: 3, quoteNumber: 1003, estimatingDueDate: '2026-09-18' }),
    quote({ id: 4, quoteNumber: 1004, isCompleted: true, estimatingDueDate: '2026-09-01' }),
  ])

  assert.deepEqual(result.map((item) => item.id), [3, 2, 1])
})

test('completed personal quotes sort newest completion first and exclude active work', () => {
  const result = sortCompletedPersonalQuotes([
    quote({ id: 1, isCompleted: true, estimatingCompletionDate: '2026-09-10' }),
    quote({ id: 2, isCompleted: false, estimatingCompletionDate: null }),
    quote({ id: 3, isCompleted: true, estimatingCompletionDate: '2026-09-17' }),
  ])

  assert.deepEqual(result.map((item) => item.id), [3, 1])
})
