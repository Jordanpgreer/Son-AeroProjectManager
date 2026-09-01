import assert from 'node:assert/strict'
import test from 'node:test'

import type { EstimatingOperationMapping } from '../src/fulcrumEstimateApi.ts'
import {
  RATE_OPERATION_OPTIONS,
  filterOperationMappings,
  normalizeOperationName,
  rateOperationByKey,
  validateMappingDraft,
} from '../src/operationRulesModel.ts'

const mappings: EstimatingOperationMapping[] = [{
  id: 'rule-1',
  fulcrumOperation: 'Rubber Mold Set Up',
  targetOperationKey: 'manufacturing:9',
  targetOperation: 'Rubber Mold',
  active: true,
  version: 1,
  updatedAt: '2026-09-01T12:00:00Z',
  updatedBy: 'Jordan Greer',
}]

test('normalizes only case and whitespace for deterministic Fulcrum matching', () => {
  assert.equal(normalizeOperationName('  Rubber   Mold Set Up '), 'rubber mold set up')
  assert.notEqual(normalizeOperationName('ID & Pack'), normalizeOperationName('ID and Pack'))
})

test('rate options use stable category and source-row keys and remove duplicate labels', () => {
  assert.equal(rateOperationByKey('manufacturing:9')?.name, 'Rubber Mold')
  assert.equal(rateOperationByKey('manufacturing:5'), undefined)
  assert.equal(rateOperationByKey('rubber-breakdown:45'), undefined)
  assert.equal(new Set(RATE_OPERATION_OPTIONS.map((option) => option.name.toLocaleLowerCase())).size, RATE_OPERATION_OPTIONS.length)
})

test('mapping validation rejects duplicate active source rules and unknown rate targets', () => {
  assert.match(
    validateMappingDraft(' rubber mold set up ', 'manufacturing:9', mappings) ?? '',
    /already exists/,
  )
  assert.match(validateMappingDraft('New Step', 'missing:1', mappings) ?? '', /Rates Reference/)
  assert.equal(validateMappingDraft('New Step', 'rubber-breakdown:34', mappings), null)
})

test('rule filtering searches both sides and hides inactive rules by default', () => {
  const inactive = { ...mappings[0], id: 'rule-2', fulcrumOperation: 'QA Final', active: false }
  assert.deepEqual(filterOperationMappings([mappings[0], inactive], 'Rubber Mold', false), [mappings[0]])
  assert.equal(filterOperationMappings([mappings[0], inactive], 'QA', false).length, 0)
  assert.equal(filterOperationMappings([mappings[0], inactive], 'QA', true).length, 1)
})
