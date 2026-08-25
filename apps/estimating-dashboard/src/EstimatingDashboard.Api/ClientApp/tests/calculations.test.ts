import assert from 'node:assert/strict'
import test from 'node:test'

import { calculateEstimate, safeDivide } from '../src/calculations.ts'
import {
  createRubberEstimateDefaults,
  createStandardEstimateDefaults,
  createSubassemblyDefaults,
  createSubassemblyEstimateDefaults,
} from '../src/estimateDefaults.ts'
import {
  ANNUAL_LABOR_RATES,
  CONTROLLED_OPERATION_OPTIONS,
  RATE_EDIT_HISTORY,
  lookupLaborRate,
} from '../src/estimatingRates.ts'
import type {
  EstimateCalculationResult,
  EstimateCalculationSuccess,
} from '../src/types.ts'
import { QUANTITY_TIERS } from '../src/types.ts'

function mustSucceed(result: EstimateCalculationResult): EstimateCalculationSuccess {
  assert.equal(result.ok, true, result.ok ? undefined : result.errors[0]?.message)
  if (!result.ok) {
    throw new Error('Expected calculation to succeed.')
  }
  return result
}

function assertNear(actual: number | null, expected: number, tolerance = 1e-10): void {
  assert.notEqual(actual, null)
  assert.ok(
    Math.abs((actual as number) - expected) <= tolerance,
    `Expected ${actual} to be within ${tolerance} of ${expected}.`,
  )
}

test('rate tables preserve annual precision, source ordering, duplicates, and history', () => {
  assert.equal(ANNUAL_LABOR_RATES.length, 41)
  assert.equal(RATE_EDIT_HISTORY.length, 12)
  assert.equal(lookupLaborRate('Metals - Mills', 2026), 0.48683333333333334)
  assert.equal(lookupLaborRate('METALS - MILLS', 2026), 0.48683333333333334)
  assert.equal(lookupLaborRate('Tooling (In House)', 2029), 0.8644999999999999)
  assert.equal(
    ANNUAL_LABOR_RATES.filter((row) => row.operation === 'Burn Holes').length,
    2,
  )
  assert.deepEqual(
    CONTROLLED_OPERATION_OPTIONS.filter((operation) => operation === 'Heat Seal'),
    ['Heat Seal', 'Heat Seal'],
  )
})

test('defaults reproduce the Standard, Rubber, and Subassembly workbook row structures', () => {
  const standard = createStandardEstimateDefaults()
  const rubber = createRubberEstimateDefaults()
  const subassemblyEstimate = createSubassemblyEstimateDefaults()
  const subassembly = createSubassemblyDefaults()

  assert.deepEqual(
    standard.operations.slice(0, 3).map((operation) => operation.nameControl),
    ['fixed', 'fixed', 'fixed'],
  )
  assert.equal(
    standard.operations.slice(3).every((operation) => operation.nameControl === 'rate-list'),
    true,
  )
  assert.equal(standard.operations.length, 13)
  assert.equal(rubber.operations.length, 26)
  assert.equal(standard.materials.length, 12)
  assert.equal(standard.processes.length, 5)
  assert.deepEqual(standard.quantities, [...QUANTITY_TIERS])
  assert.deepEqual(rubber.quantities, [...QUANTITY_TIERS])
  assert.equal(rubber.toolingMarkup, 0.12)
  assert.equal(rubber.difficulty, null)
  assert.equal(rubber.cavities, 0)
  assert.equal(subassemblyEstimate.operations.length, 12)
  assert.equal(subassemblyEstimate.operations.slice(0, 2).every(
    (operation) => operation.costTreatment === 'nre',
  ), true)
  assert.equal(subassemblyEstimate.operations.slice(2).every(
    (operation) => operation.costTreatment === 'production',
  ), true)
  assert.equal(subassemblyEstimate.materials.length, 12)
  assert.equal(subassemblyEstimate.processes.length, 12)
  assert.deepEqual(subassemblyEstimate.subassemblies, [])
  assert.equal(subassembly.operations.length, 12)
  assert.equal(subassembly.operations.slice(0, 2).every(
    (operation) => operation.costTreatment === 'nre',
  ), true)
  assert.equal(subassembly.operations.slice(2).every(
    (operation) => operation.costTreatment === 'production',
  ), true)
  assert.equal(subassembly.materials.length, 12)
  assert.equal(subassembly.processes.length, 5)
})

test('matches the one-child Subassembly workbook roll-up without child G&A or profit', () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [10]
  estimate.salesMarkup = 0
  const child = createSubassemblyDefaults()
  child.partNumber = 'CHILD-001'
  child.facilitiesByQuantity[10] = 0.25

  const program = child.operations.find((operation) => operation.name === 'Program')
  const mill = child.operations.find((operation) => operation.name === 'Metals - Mills')
  assert.ok(program)
  assert.ok(mill)
  program.setupMinutes = 60
  mill.runMinutes = 2
  child.materials[0].partsQuantity = 1
  child.materials[0].unitPrice = 5
  child.processes[0].setupCost = 20
  child.processes[0].runCostEach = 1
  estimate.subassemblies.push(child)
  estimate.processes[0].description = child.partNumber
  estimate.processes[0].subassemblyId = child.id

  const result = mustSucceed(calculateEstimate(estimate))
  const childAudit = result.subassemblies[0]
  assert.notEqual(childAudit.quantities, null)
  const childAtTen = childAudit.quantities?.[10]
  assert.ok(childAtTen)

  assertNear(childAtTen.basicLabor, 0.9736666666666667)
  assertNear(childAtTen.laborBurden, 4.040716666666667)
  assertNear(childAtTen.burdenedLabor, 5.014383333333334)
  assertNear(childAtTen.rawMaterial, 5.5)
  assertNear(childAtTen.rawProcess, 3)
  assertNear(childAtTen.rawOneTimeNre, 124.8)
  assertNear(childAtTen.amortizedNre, 12.48)
  assertNear(childAtTen.facilities, 0.25)
  assertNear(childAtTen.unitCost, 26.24438333333333)

  assertNear(result.processes[0].unitCostByQuantity[10], childAtTen.unitCost)
  assertNear(result.quantities[10].rawProcess, childAtTen.unitCost)
  assertNear(result.quantities[10].process.ga, childAtTen.unitCost * 0.2)
  assertNear(result.quantities[10].process.profit, childAtTen.unitCost * 1.2 * 0.2)
  assertNear(result.quantities[10].process.loaded, childAtTen.unitCost * 1.2 * 1.2)
  assertNear(result.quantities[10].sellPrice, 39.10413116666667)
})

test('rolls up multiple ordered children and applies quantity-per-parent multipliers', () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [10]
  estimate.yield = 1
  const first = createSubassemblyDefaults(0)
  const second = createSubassemblyDefaults(1)
  first.partNumber = 'FIRST'
  second.partNumber = 'SECOND'
  first.facilitiesByQuantity[10] = 2
  second.facilitiesByQuantity[10] = 3
  estimate.subassemblies.push(first, second)
  estimate.processes[0].subassemblyId = first.id
  estimate.processes[0].quantityPerParent = 2
  estimate.processes[1].subassemblyId = second.id
  estimate.processes[1].quantityPerParent = 3

  const result = mustSucceed(calculateEstimate(estimate))

  assert.deepEqual(
    result.subassemblies.map((subassembly) => subassembly.subassemblyId),
    [first.id, second.id],
  )
  assert.equal(result.subassemblies[0].quantities?.[10].unitCost, 2)
  assert.equal(result.subassemblies[1].quantities?.[10].unitCost, 3)
  assert.equal(result.processes[0].unitCostByQuantity[10], 4)
  assert.equal(result.processes[1].unitCostByQuantity[10], 9)
  assert.equal(result.quantities[10].rawProcess, 13)
  assertNear(result.quantities[10].process.loaded, 18.72)
})

test('reports dangling subassembly process links explicitly', () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [10]
  estimate.processes[0].subassemblyId = 'missing-child'
  estimate.processes[0].quantityPerParent = 2

  const result = calculateEstimate(estimate)

  assert.equal(result.ok, false)
  if (result.ok) {
    throw new Error('Expected calculation to fail.')
  }
  assert.equal(result.errors[0].code, 'missing-subassembly-link')
  assert.equal(result.processes[0].unitCostByQuantity[10], 0)
})

test('reports missing child labor rates with subassembly context', () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [10]
  const child = createSubassemblyDefaults()
  child.partNumber = 'RATE-FAIL'
  child.operations[2].name = 'Unknown child operation'
  estimate.subassemblies.push(child)
  estimate.processes[0].subassemblyId = child.id

  const result = calculateEstimate(estimate)

  assert.equal(result.ok, false)
  if (result.ok) {
    throw new Error('Expected calculation to fail.')
  }
  const error = result.errors[0]
  assert.equal(error.code, 'missing-subassembly-rate')
  if (error.code !== 'missing-subassembly-rate') {
    throw new Error('Expected a missing subassembly rate error.')
  }
  assert.equal(error.subassemblyId, child.id)
  assert.equal(error.subassemblyPartNumber, child.partNumber)
  assert.equal(result.subassemblies[0].quantities, null)
})

test('editable quantity tiers drive every calculation and audit map', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.quantities = [7, 42, 333]
  const operation = estimate.operations.find(
    (candidate) => candidate.name === 'Metals - Mills',
  )
  assert.ok(operation)
  operation.setupMinutes = 35
  operation.runMinutes = 3

  const result = mustSucceed(calculateEstimate(estimate))
  const audit = result.operations.find((candidate) => candidate.operationId === operation.id)
  const laborRate = lookupLaborRate('Metals - Mills', 2026)
  assert.notEqual(laborRate, undefined)

  assert.deepEqual(Object.keys(result.quantities).map(Number), [7, 42, 333])
  assert.equal(result.quantities[10], undefined)
  assertNear(
    audit?.unitCostByQuantity[7] ?? null,
    (35 / 7 + 3) * (laborRate as number),
  )
  assert.equal(result.quantities[7].facilities, 0)
})

test('appended operation, material, and process rows participate in pricing', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.quantities = [20]
  estimate.operations.push({
    id: 'custom-operation-test',
    name: 'Metals - Mills',
    nameControl: 'rate-list',
    setupMinutes: 20,
    runMinutes: 1,
    costTreatment: 'production',
    amortizeNre: false,
  })
  estimate.materials.push({
    id: 'custom-material-test',
    description: 'Test material',
    unitOfMeasure: 'EA',
    partsQuantity: 2,
    unitPrice: 4,
    amortizeMinBuy: true,
  })
  estimate.processes.push({
    id: 'custom-process-test',
    description: 'Test process',
    setupCost: 40,
    runCostEach: 2,
  })

  const result = mustSucceed(calculateEstimate(estimate))
  const material = result.materials.find((candidate) => (
    candidate.materialId === 'custom-material-test'
  ))
  const process = result.processes.find((candidate) => (
    candidate.processId === 'custom-process-test'
  ))

  assert.equal(result.operations.at(-1)?.operationId, 'custom-operation-test')
  assert.equal(material?.extendedCost, 8)
  assert.equal(material?.unitCostByQuantity[20], 0.4)
  assert.equal(process?.unitCostByQuantity[20], 4)
})

test('matches the Standard 2026 Metals - Mills golden values without rounding', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.yield = 0.95
  estimate.salesMarkup = 0.1
  const operation = estimate.operations.find(
    (candidate) => candidate.name === 'Metals - Mills',
  )
  assert.ok(operation)
  operation.setupMinutes = 60
  operation.runMinutes = 2

  const result = mustSucceed(calculateEstimate(estimate))

  assertNear(result.quantities[10].sellPrice, 32.77400946666667)
  assertNear(result.quantities[1000].sellPrice, 8.439307437666667)
  assertNear(result.quantities[10].grossMargin, 0.3574051407588739)
})

test('switching the selected year changes both the resolved rate and sell price', () => {
  const estimate = createStandardEstimateDefaults()
  const operation = estimate.operations.find(
    (candidate) => candidate.name === 'Metals - Mills',
  )
  assert.ok(operation)
  operation.setupMinutes = 30
  operation.runMinutes = 4

  estimate.rateYear = 2025
  const result2025 = mustSucceed(calculateEstimate(estimate))
  estimate.rateYear = 2026
  const result2026 = mustSucceed(calculateEstimate(estimate))

  const audit2025 = result2025.operations.find((audit) => audit.operationId === operation.id)
  const audit2026 = result2026.operations.find((audit) => audit.operationId === operation.id)
  assert.equal(audit2025?.laborRate, 0.4681666666666667)
  assert.equal(audit2026?.laborRate, 0.48683333333333334)
  assert.notEqual(result2025.quantities[100].sellPrice, result2026.quantities[100].sellPrice)
})

test('material rows implement both minimum-buy allocation branches', () => {
  const estimate = createStandardEstimateDefaults()
  const material = estimate.materials[0]
  material.partsQuantity = 2
  material.unitPrice = 5

  material.amortizeMinBuy = false
  const purchasePerOrder = mustSucceed(calculateEstimate(estimate))
  assert.equal(purchasePerOrder.materials[0].extendedCost, 10)
  assert.equal(purchasePerOrder.materials[0].unitCostByQuantity[10], 11)
  assertNear(purchasePerOrder.materials[0].unitCostByQuantity[1000], 10.01)

  material.amortizeMinBuy = true
  const amortized = mustSucceed(calculateEstimate(estimate))
  assert.equal(amortized.materials[0].unitCostByQuantity[10], 1)
  assert.equal(amortized.materials[0].unitCostByQuantity[1000], 0.01)
})

test('outside process rows allocate setup and add run cost per unit', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.processes[0].setupCost = 100
  estimate.processes[0].runCostEach = 3

  const result = mustSucceed(calculateEstimate(estimate))

  assert.equal(result.processes[0].unitCostByQuantity[10], 13)
  assert.equal(result.processes[0].unitCostByQuantity[1000], 3.1)
})

test('Rubber purchase tooling becomes marked-up NRE only when amortization is enabled', () => {
  const estimate = createRubberEstimateDefaults()
  const fixtures = estimate.operations.find(
    (operation) => operation.name === 'Fixtures (Purchase)',
  )
  const tooling = estimate.operations.find(
    (operation) => operation.name === 'Mold/Tooling',
  )
  assert.ok(fixtures)
  assert.ok(tooling)
  fixtures.setupMinutes = 100
  fixtures.runMinutes = 20
  tooling.setupMinutes = 50
  tooling.runMinutes = 10

  const excluded = mustSucceed(calculateEstimate(estimate))
  assert.equal(excluded.oneTimeNre, 0)

  fixtures.amortizeNre = true
  tooling.amortizeNre = true
  const included = mustSucceed(calculateEstimate(estimate))

  assertNear(included.rawOneTimeNre, 201.6)
  assertNear(included.oneTimeNre, 290.304)
  assertNear(included.quantities[10].amortizedNre, 29.0304)
})

test('loads raw operation NRE with labor G&A and profit before amortization', () => {
  const estimate = createStandardEstimateDefaults()
  const program = estimate.operations.find(
    (operation) => operation.name === 'Program',
  )
  assert.ok(program)
  program.setupMinutes = 60

  const result = mustSucceed(calculateEstimate(estimate))

  assertNear(result.rawOneTimeNre, 124.8)
  assertNear(result.oneTimeNre, 179.712)
  assertNear(result.quantities[10].amortizedNre, 17.9712)
})

test('matches the complete Standard workbook fixture across every quantity tier', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.rateYear = 2026
  estimate.yield = 0.95
  estimate.salesMarkup = 0.05

  const program = estimate.operations.find((operation) => operation.name === 'Program')
  const millTurn = estimate.operations.find((operation) => operation.name === 'Mill/Turn')
  const quality = estimate.operations.find(
    (operation) => operation.name === 'Quality Inspection',
  )
  assert.ok(program)
  assert.ok(millTurn)
  assert.ok(quality)
  program.setupMinutes = 60
  millTurn.setupMinutes = 30
  millTurn.runMinutes = 2
  quality.runMinutes = 1

  estimate.materials[0].partsQuantity = 2
  estimate.materials[0].unitPrice = 5
  estimate.materials[0].amortizeMinBuy = true
  estimate.materials[1].partsQuantity = 1
  estimate.materials[1].unitPrice = 3
  estimate.materials[1].amortizeMinBuy = false
  estimate.processes[0].setupCost = 100
  estimate.processes[0].runCostEach = 2
  for (const quantity of QUANTITY_TIERS) {
    estimate.facilitiesByQuantity[quantity] = 0.25
  }

  const result = mustSucceed(calculateEstimate(estimate))
  const expected: Record<number, readonly [number, number]> = {
    10: [68.5360055667, 685.360055667],
    25: [40.1137054767, 1002.84263692],
    50: [30.6396054467, 1531.98027233],
    75: [27.4815721033, 2061.11790775],
    100: [25.9025554317, 2590.25554317],
    250: [23.0603254227, 5765.08135567],
    500: [22.1129154197, 11056.4577098],
    1000: [21.6392104182, 21639.2104182],
  }

  assertNear(result.oneTimeNre, 179.712)
  for (const quantity of QUANTITY_TIERS) {
    assertNear(result.quantities[quantity].sellPrice, expected[quantity][0], 1e-8)
    assertNear(result.quantities[quantity].extendedValue, expected[quantity][1], 1e-6)
  }
})

test('Rubber difficulty and cavity fields are metadata and do not alter costs', () => {
  const estimate = createRubberEstimateDefaults()
  const operation = estimate.operations.find(
    (candidate) => candidate.name === 'Calendering',
  )
  assert.ok(operation)
  operation.setupMinutes = 20
  operation.runMinutes = 4
  estimate.difficulty = 1
  estimate.cavities = 2
  const first = mustSucceed(calculateEstimate(estimate))

  estimate.difficulty = 5
  estimate.cavities = 100
  const second = mustSucceed(calculateEstimate(estimate))

  assert.deepEqual(first.quantities, second.quantities)
  assert.equal(first.oneTimeNre, second.oneTimeNre)
})

test('operation notes are metadata and do not alter costs', () => {
  const estimate = createStandardEstimateDefaults()
  const operation = estimate.operations.find(
    (candidate) => candidate.name === 'Mill/Turn',
  )
  assert.ok(operation)
  operation.setupMinutes = 20
  operation.runMinutes = 4
  const withoutNote = mustSucceed(calculateEstimate(estimate))

  operation.notes = 'Requires first-article setup verification.'
  const withNote = mustSucceed(calculateEstimate(estimate))

  assert.deepEqual(withNote.quantities, withoutNote.quantities)
  assert.equal(withNote.oneTimeNre, withoutNote.oneTimeNre)
})

test('zero-value estimates return null ratios instead of NaN or Infinity', () => {
  const result = mustSucceed(calculateEstimate(createStandardEstimateDefaults()))

  assert.equal(result.quantities[10].sellPrice, 0)
  assert.equal(result.quantities[10].grossMargin, null)
  assert.equal(result.quantities[10].materialPercentOfPrice, null)
  assert.equal(result.quantities[10].loadedComponentMargin, null)
  assert.equal(safeDivide(1, 0), null)
  assert.equal(safeDivide(0, 0), null)
})

test('a missing exact rate produces an explicit calculation failure', () => {
  const estimate = createStandardEstimateDefaults()
  estimate.operations[3].name = ' Metals - Mills'

  const result = calculateEstimate(estimate)

  assert.equal(result.ok, false)
  if (result.ok) {
    throw new Error('Expected calculation to fail.')
  }
  assert.equal(result.quantities, null)
  assert.equal(result.rawOneTimeNre, null)
  assert.equal(result.oneTimeNre, null)
  assert.equal(result.errors[0].code, 'missing-rate')
  assert.equal(result.errors[0].operationName, ' Metals - Mills')
})
