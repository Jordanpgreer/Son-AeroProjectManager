import assert from 'node:assert/strict'
import test from 'node:test'

import type { FulcrumEstimatePreview } from '../src/fulcrumEstimateApi.ts'
import {
  FULCRUM_BUILDER_SESSION_KEY,
  buildExportRequest,
  buildRateSnapshot,
  canFillFulcrumCalculator,
  canGenerateFulcrumEstimate,
  createBuilderStateFromPreview,
  filenameDate,
  fulcrumBuilderReducer,
  fulcrumEstimateFilename,
  initialsFromDisplayName,
  localIsoDate,
  readBuilderSession,
  writeBuilderSession,
} from '../src/fulcrumEstimateModel.ts'

function preview(): FulcrumEstimatePreview {
  return {
    reviewId: 'review-1',
    expiresAt: '2026-09-01T18:00:00Z',
    summary: {
      partNumber: 'SF890051',
      revision: 'NC',
      estimateDate: '2026-09-01',
      estimatorInitials: 'JG',
      sourceFileName: 'Fulcrum export.xlsx',
      targetSheet: 'Rubber Breakdown',
      rateYear: 2026,
    },
    operations: [{
      id: 'operation-1',
      sourceRow: 11,
      sourceOperation: 'Rubber Mold Set Up',
      operationNumber: 11,
      operationLabel: 'OP 11',
      targetOperationKey: 'manufacturing:9',
      targetOperation: 'Rubber Mold',
      suggestedSetupMinutes: 30,
      suggestedRunMinutes: 2,
      timeType: 'Minutes',
    }],
    materials: [{
      id: 'material-1',
      sourceRow: 7,
      targetRow: 47,
      description: 'Rubber compound',
      unitsRequired: 2,
    }],
    manualTasks: [{
      id: 'manual-b2',
      section: 'Estimate setup',
      label: 'Customer',
      description: 'Enter the customer.',
      sheetName: 'Rubber Breakdown',
      cellAddress: 'B2',
      inputKind: 'text',
      required: true,
      minimum: null,
      materialDescription: null,
      initialValue: '',
    }, {
      id: 'manual-d47',
      section: 'Raw materials and hardware',
      label: 'Unit price',
      description: 'Enter the material unit price.',
      sheetName: 'Rubber Breakdown',
      cellAddress: 'D47',
      inputKind: 'number',
      required: true,
      minimum: 0,
      materialDescription: 'Rubber compound',
      initialValue: '',
    }],
    issues: [],
    canExport: true,
  }
}

test('derives estimator initials from the first and last display-name tokens', () => {
  assert.equal(initialsFromDisplayName('Jordan Greer'), 'JG')
  assert.equal(initialsFromDisplayName('Jordan A. Greer'), 'JG')
  assert.equal(initialsFromDisplayName('Prince'), 'PR')
})

test('formats local dates and the required workbook filename without a revision hyphen', () => {
  assert.equal(localIsoDate(new Date(2026, 8, 1, 23, 30)), '2026-09-01')
  assert.equal(filenameDate('2026-09-01'), '09-01-2026')
  assert.equal(
    fulcrumEstimateFilename('SF890051', 'NC', '2026-09-01', 'JG'),
    'SF890051 NC 09-01-2026 JG.xlsx',
  )
})

test('generation stays blocked until operation overrides and required manual fields are complete', () => {
  let state = createBuilderStateFromPreview(preview())
  assert.equal(canGenerateFulcrumEstimate(state), false)
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'Acme' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-d47', value: '12.50' })
  assert.equal(canGenerateFulcrumEstimate(state), false)
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), true)

  const request = buildExportRequest(state)
  assert.deepEqual(request.operationOverrides, [{
    operationId: 'operation-1',
    setupMinutes: 30,
    runMinutes: 2,
  }])
  assert.deepEqual(request.manualValues, { 'manual-b2': 'Acme', 'manual-d47': 12.5 })
  assert.equal(request.rateYear, 2026)
})

test('calculator import remains blocked when server preview validation fails', () => {
  const rejected = preview()
  rejected.canExport = false
  rejected.operations = []
  let state = createBuilderStateFromPreview(rejected)
  assert.equal(canFillFulcrumCalculator(state), false)

  const errored = preview()
  errored.issues.push({
    severity: 'error',
    sheet: 'Bill of Materials',
    row: 7,
    column: 'B',
    message: 'Material is invalid.',
  })
  state = createBuilderStateFromPreview(errored)
  assert.equal(canFillFulcrumCalculator(state), false)

  const warningOnly = preview()
  warningOnly.issues.push({
    severity: 'warning',
    sheet: 'Routing',
    row: 11,
    column: 'B',
    message: 'Review this operation.',
  })
  state = createBuilderStateFromPreview(warningOnly)
  assert.equal(canFillFulcrumCalculator(state), true)
})

test('missing OP numbers and out-of-range editor values block generation', () => {
  const missingOp = preview()
  missingOp.operations[0].operationNumber = null
  let state = createBuilderStateFromPreview(missingOp)
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'Acme' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-d47', value: '12.50' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), false)

  state = createBuilderStateFromPreview(preview())
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'x'.repeat(1001) })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-d47', value: '12.50' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), false)

  state = createBuilderStateFromPreview(preview())
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'Acme' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-d47', value: '-1' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), false)

  state = createBuilderStateFromPreview(preview())
  state = fulcrumBuilderReducer(state, { type: 'set-operation-value', operationId: 'operation-1', field: 'setupMinutes', value: '1000001' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'Acme' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-d47', value: '1000000001' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), false)
})

test('prefilled orange cells still require explicit estimator confirmation', () => {
  const estimate = preview()
  estimate.manualTasks[1].initialValue = 0.08
  let state = createBuilderStateFromPreview(estimate)
  state = fulcrumBuilderReducer(state, { type: 'set-manual-value', taskId: 'manual-b2', value: 'Acme' })
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-b2' })
  assert.equal(canGenerateFulcrumEstimate(state), false)
  state = fulcrumBuilderReducer(state, { type: 'confirm-manual-task', taskId: 'manual-d47' })
  assert.equal(canGenerateFulcrumEstimate(state), true)
})

test('rate snapshot comes only from the controlled annual rates and assumptions', () => {
  const snapshot = buildRateSnapshot(2026)
  const rubberMold = snapshot.operationRates.find((row) => row.rateReferenceKey === 'manufacturing:9')
  assert.equal(rubberMold?.operation, 'Rubber Mold')
  assert.equal(rubberMold?.value, 0.4005)
  assert.equal(snapshot.assumptions.burden, 4.15)
  assert.throws(() => buildRateSnapshot(2030), /not available/)
})

test('session persistence stores structured review state and never workbook bytes', () => {
  const values = new Map<string, string>()
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value) },
    removeItem: (key: string) => { values.delete(key) },
  }
  const state = createBuilderStateFromPreview(preview())
  writeBuilderSession(storage, state)
  const raw = values.get(FULCRUM_BUILDER_SESSION_KEY) ?? ''
  assert.match(raw, /review-1/)
  assert.doesNotMatch(raw, /arrayBuffer|rawWorkbook|fileBytes/)
  assert.equal(readBuilderSession(storage)?.preview?.summary.partNumber, 'SF890051')
})

test('session recovery rejects malformed previews and rebuilds missing editor maps', () => {
  const values = new Map<string, string>()
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
  }
  values.set(FULCRUM_BUILDER_SESSION_KEY, JSON.stringify({
    stage: 'operations',
    preview: { reviewId: 'stale-review' },
  }))
  assert.equal(readBuilderSession(storage), null)

  values.set(FULCRUM_BUILDER_SESSION_KEY, JSON.stringify({
    stage: 'operations',
    preview: preview(),
    activeManualTaskId: 'missing-task',
  }))
  const recovered = readBuilderSession(storage)
  assert.deepEqual(recovered?.operationValues['operation-1'], {
    setupMinutes: '30',
    runMinutes: '2',
  })
  assert.equal(recovered?.manualValues['manual-b2'], '')
  assert.equal(recovered?.activeManualTaskId, 'manual-b2')
})

test('session persistence failures do not prevent using the builder', () => {
  const unavailableStorage = {
    setItem: () => { throw new Error('Storage disabled') },
    removeItem: () => { throw new Error('Storage disabled') },
  }
  assert.doesNotThrow(() => writeBuilderSession(unavailableStorage, createBuilderStateFromPreview(preview())))
  assert.doesNotThrow(() => writeBuilderSession(unavailableStorage, {
    ...createBuilderStateFromPreview(preview()),
    preview: null,
  }))
})
