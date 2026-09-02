import assert from 'node:assert/strict'
import test from 'node:test'

import type { FulcrumEstimatePreview } from '../src/fulcrumEstimateApi.ts'
import {
  buildFulcrumCalculatorImport,
  importGuideTaskComplete,
} from '../src/fulcrumCalculatorImport.ts'

function preview(): FulcrumEstimatePreview {
  const task = (id: string, label: string, inputKind: 'text' | 'number') => ({
    id,
    section: 'Estimate input',
    label,
    description: `Complete ${label}.`,
    sheetName: 'Rubber Breakdown',
    cellAddress: 'hidden-in-ui',
    inputKind,
    required: true,
    minimum: inputKind === 'number' ? 0 : null,
    materialDescription: id.startsWith('bom-') ? 'Rubber compound' : null,
    initialValue: '',
  })
  return {
    reviewId: 'review-1',
    expiresAt: '2026-09-02T15:00:00Z',
    summary: {
      partNumber: 'SA-100',
      revision: 'B',
      estimateDate: '2026-09-02',
      estimatorInitials: 'JG',
      sourceFileName: 'source.xlsx',
      targetSheet: 'Rubber Breakdown',
      rateYear: 2026,
    },
    operations: [{
      id: 'routing-3',
      sourceRow: 3,
      sourceOperation: 'Final inspection',
      operationNumber: 30,
      operationLabel: 'OP 30',
      targetOperationKey: 'rubber-breakdown:40',
      targetOperation: 'Quality Inspection',
      suggestedSetupMinutes: 10,
      suggestedRunMinutes: 2,
      timeType: 'PerUnit',
    }],
    materials: [{
      id: 'bom-3',
      sourceRow: 3,
      targetRow: 47,
      description: 'Rubber compound',
      unitsRequired: 1.5,
    }],
    manualTasks: [
      task('customer', 'Customer', 'text'),
      task('quoteLogNumber', 'Quote log number', 'text'),
      ...Array.from({ length: 8 }, (_, index) => task(`quantity${index + 1}`, `Quantity ${index + 1}`, 'number')),
      task('bom-3.unitOfMeasure', 'Unit of measure', 'text'),
      task('bom-3.unitPrice', 'Unit price', 'number'),
      task('bom-3.notes', 'Material notes', 'text'),
    ],
    issues: [],
    canExport: true,
  }
}

test('BOM and routing preview fills the calculator in source order with reviewed times', () => {
  const imported = buildFulcrumCalculatorImport(preview(), {
    'routing-3': { setupMinutes: '12.5', runMinutes: '3.25' },
  })
  assert.equal(imported.estimate.kind, 'rubber')
  assert.deepEqual(imported.estimate.metadata, {
    customer: '',
    partNumber: 'SA-100',
    revision: 'B',
    nsn: '',
    quoteLogNumber: '',
    solicitationNumber: '',
    rfqNumber: '',
    quoteDate: '2026-09-02',
    estimator: 'JG',
    comments: '',
  })
  assert.deepEqual(imported.estimate.quantities, [10, 25, 50, 75, 100, 200, 400, 800])
  assert.deepEqual(imported.estimate.operations.map((operation) => ({
    name: operation.name,
    setup: operation.setupMinutes,
    run: operation.runMinutes,
  })), [{ name: 'Quality Inspection', setup: 12.5, run: 3.25 }])
  assert.deepEqual(imported.estimate.materials[0], {
    id: 'bom-3',
    description: 'Rubber compound',
    unitOfMeasure: '',
    partsQuantity: 1.5,
    unitPrice: 0,
    notes: '',
    amortizeMinBuy: false,
  })
})

test('manual workbook needs map to calculator controls without exposing cell addresses', () => {
  const imported = buildFulcrumCalculatorImport(preview(), {
    'routing-3': { setupMinutes: '10', runMinutes: '2' },
  })
  assert.deepEqual(imported.guideTasks.map((task) => task.fieldKey), [
    'metadata-customer',
    'metadata-quoteLogNumber',
    ...Array.from({ length: 8 }, (_, index) => `quantity-${index}`),
    'material-bom-3-unitOfMeasure',
    'material-bom-3-unitPrice',
    'material-bom-3-notes',
  ])
  assert.equal(JSON.stringify(imported.guideTasks).includes('hidden-in-ui'), false)

  const customer = imported.guideTasks[0]
  assert.equal(importGuideTaskComplete(customer, imported.estimate), false)
  imported.estimate.metadata.customer = 'Acme'
  assert.equal(importGuideTaskComplete(customer, imported.estimate), true)

  const unitPrice = imported.guideTasks.find((task) => task.id === 'bom-3.unitPrice')!
  assert.equal(importGuideTaskComplete(unitPrice, imported.estimate), true)
  imported.estimate.materials[0].unitPrice = Number.NaN
  assert.equal(importGuideTaskComplete(unitPrice, imported.estimate), false)
})
