import assert from 'node:assert/strict'
import test from 'node:test'
import {
  buildFulcrumQuoteEstimates,
  suggestFulcrumQuantityTiers,
  type FulcrumQuoteGeneration,
  type FulcrumQuoteItemNode,
} from '../src/fulcrumQuoteGeneration.ts'

function item(partNumber = '2710975-1'): FulcrumQuoteItemNode {
  return {
    itemId: partNumber,
    partNumber,
    revision: 'C',
    description: 'BASE ASSY, MOTOR',
    availableStock: 32,
    notes: 'Production note',
    quantityPerParent: 1,
    operations: [{
      id: 'routing-1', order: 1, sourceOperation: 'Production control',
      targetOperation: null, rateReferenceKey: null,
      setupMinutes: 5, runMinutes: 2, machineMinutes: 3, instructions: 'Two operators',
    }],
    materials: [{
      id: 'material-1', partNumber: 'MATERIAL-1', description: 'Plastic',
      quantityPerParent: 2.5, unitOfMeasure: 'LB', unitCost: 12.3, notes: 'Buy material',
    }],
    processes: [{ id: 'outside-1', description: 'Coating', unitCost: 4, lotCost: 25, notes: 'Supplier note' }],
    subassemblies: [],
  }
}

function response(): FulcrumQuoteGeneration {
  return {
    quoteHistoryId: 'history-4460', quoteNumber: '4460', customer: 'Honeywell',
    generatedAt: '2026-09-08T15:00:00Z', warnings: [],
    items: [{ lineItemId: 'line-1', quantity: 32, quantities: [32], item: item() }],
  }
}

test('single-quantity suggestions follow deterministic business-friendly increments', () => {
  assert.deepEqual(suggestFulcrumQuantityTiers(32), [32, 50, 75, 100, 150])
  assert.deepEqual(suggestFulcrumQuantityTiers(4), [4, 8, 15, 25, 50])
  assert.deepEqual(suggestFulcrumQuantityTiers(1000), [1000, 1500, 2000, 3000, 4000])
  assert.throws(() => suggestFulcrumQuantityTiers(0), /positive whole/)
  assert.throws(() => suggestFulcrumQuantityTiers(1.5), /positive whole/)
})

test('creates separate estimates for each quote line and retains exact explicit price breaks', () => {
  const source = response()
  source.items[0].quantities = [100, 32, 50, 32]
  source.items.push({ lineItemId: 'line-2', quantity: 4, quantities: [4], item: item('606394-1') })
  const result = buildFulcrumQuoteEstimates(source)
  assert.equal(result.estimates.length, 2)
  assert.deepEqual(result.estimates[0].quantities, [32, 50, 100])
  assert.deepEqual(result.estimates[1].quantities, [4, 8, 15, 25, 50])
  assert.equal(result.estimates[0].metadata.quoteLogNumber, '4460')
  assert.equal(result.estimates[0].metadata.customer, 'Honeywell')
  assert.equal(result.estimates[1].metadata.partNumber, '606394-1')
  assert.ok(result.warnings.some((warning) => warning.includes('Suggested tiers')))
  assert.notEqual(result.estimates[0].operations[0].id, result.estimates[1].operations[0].id)
})

test('imports available stock, all BOM costs, routing times and unmapped operation titles', () => {
  const { estimates } = buildFulcrumQuoteEstimates(response())
  const estimate = estimates[0]
  assert.equal(estimate.kind, 'standard')
  assert.match(estimate.metadata.comments, /^32 STOCK\n/)
  assert.match(estimate.metadata.comments, /Production note/)
  assert.equal(estimate.operations[0].name, 'Production control')
  assert.equal(estimate.operations[0].setupMinutes, 5)
  assert.equal(estimate.operations[0].runMinutes, 2)
  assert.match(estimate.operations[0].notes!, /Two operators/)
  assert.match(estimate.operations[0].notes!, /Machine time: 3 min/)
  assert.equal(estimate.materials[0].partsQuantity, 2.5)
  assert.equal(estimate.materials[0].unitPrice, 12.3)
  assert.equal(estimate.materials[0].unitOfMeasure, 'LB')
  assert.equal(estimate.processes[0].setupCost, 25)
  assert.equal(estimate.processes[0].runCostEach, 4)
  assert.match(estimate.processes[0].description, /Supplier note/)
})

test('uses controlled operation target when provided, with source ordering intact', () => {
  const source = response()
  const first = source.items[0].item.operations[0]
  first.targetOperation = 'Quality Inspection'
  first.order = 2
  source.items[0].item.operations.push({ ...first, id: 'routing-2', order: 1, targetOperation: 'ID & Pack' })
  const result = buildFulcrumQuoteEstimates(source)
  assert.deepEqual(result.estimates[0].operations.map((operation) => operation.name), ['ID & Pack', 'Quality Inspection'])
  assert.equal(result.warnings.some((warning) => warning.includes('no controlled operation')), false)
})

test('keeps the base quote quantity alongside additional breaks and persists review notes', () => {
  const source = response()
  source.quoteNumber = 4460
  source.estimator = 'Jordan Greer'
  source.items[0].quantities = [50, 100]
  source.warnings = ['Verify supplier pricing before review.']
  const result = buildFulcrumQuoteEstimates(source)
  assert.deepEqual(result.estimates[0].quantities, [32, 50, 100])
  assert.equal(result.estimates[0].metadata.quoteLogNumber, '4460')
  assert.equal(result.estimates[0].metadata.estimator, 'Jordan Greer')
  assert.match(result.estimates[0].metadata.comments, /Verify supplier pricing before review/)
})

test('preserves nested Make sheets and multiplies quantities along each parent path', () => {
  const source = response()
  const child = item('2710975-7')
  child.quantityPerParent = 4
  child.availableStock = 2
  const grandchild = item('INSERT-DETAIL')
  grandchild.quantityPerParent = 3
  child.subassemblies = [grandchild]
  source.items[0].item.subassemblies = [child]
  const estimate = buildFulcrumQuoteEstimates(source).estimates[0]
  assert.equal(estimate.kind, 'subassembly')
  if (estimate.kind !== 'subassembly') return
  assert.equal(estimate.subassemblies.length, 2)
  const [importedChild, importedGrandchild] = estimate.subassemblies
  assert.equal(importedChild.quantityPerParent, 4)
  assert.equal(importedChild.deriveQuantitiesFromParent, true)
  assert.equal(importedChild.quantitiesByParentQuantity[32], 128)
  assert.equal(importedGrandchild.quantityPerParent, 12)
  assert.equal(importedGrandchild.quantitiesByParentQuantity[32], 384)
  assert.equal(importedChild.processes.at(-1)?.subassemblyId, importedGrandchild.id)
  assert.equal(importedChild.processes.at(-1)?.quantityPerParent, 3)
  assert.equal(estimate.processes.at(-1)?.subassemblyId, importedChild.id)
  assert.equal(estimate.processes.at(-1)?.quantityPerParent, 4)
  assert.equal(estimate.processes.filter((process) => process.subassemblyId).length, 1)
  assert.match(importedChild.comments!, /^2 STOCK\n/)
  assert.equal(importedGrandchild.materials[0].partsQuantity, 2.5)
  assert.equal(importedChild.operations[0].runMinutes, 2)
})

test('does not silently truncate quantity tiers, BOM rows or routing steps', () => {
  const source = response()
  source.items[0].quantities = [1, 2, 3, 4, 5, 6, 7, 8, 9]
  assert.throws(() => buildFulcrumQuoteEstimates(source), /No quantities were discarded/)
  source.items[0].quantities = [32]
  const node = source.items[0].item
  node.operations = Array.from({ length: 40 }, (_, index) => ({ ...node.operations[0], id: `op-${index}`, order: index }))
  node.materials = Array.from({ length: 20 }, (_, index) => ({ ...node.materials[0], id: `material-${index}` }))
  const estimate = buildFulcrumQuoteEstimates(source).estimates[0]
  assert.equal(estimate.operations.length, 40)
  assert.equal(estimate.materials.length, 20)
})

test('fails explicitly for cyclic BOM, too many Make sheets, or invalid usage', () => {
  const source = response()
  source.items[0].item.subassemblies = [item()]
  assert.throws(() => buildFulcrumQuoteEstimates(source), /circular/)
  source.items[0].item.subassemblies = Array.from({ length: 13 }, (_, index) => item(`child-${index}`))
  assert.throws(() => buildFulcrumQuoteEstimates(source), /No subassemblies were discarded/)
  source.items[0].item.subassemblies = [item('child')]
  source.items[0].item.subassemblies[0].quantityPerParent = 0
  assert.throws(() => buildFulcrumQuoteEstimates(source), /quantity per parent/)
})

test('warns on unavailable inventory and missing costs instead of claiming zero stock or known pricing', () => {
  const source = response()
  source.items[0].item.availableStock = null
  source.items[0].item.materials[0].unitCost = null
  source.items[0].item.processes[0].unitCost = null
  source.items[0].item.processes[0].lotCost = null
  const result = buildFulcrumQuoteEstimates(source)
  assert.equal(result.estimates[0].metadata.comments.includes('STOCK'), false)
  assert.ok(result.warnings.some((warning) => warning.includes('available stock was not supplied')))
  assert.ok(result.warnings.some((warning) => warning.includes('purchase price')))
  assert.ok(result.warnings.some((warning) => warning.includes('outside process')))
})

test('reuses one subassembly sheet when the same Make item appears on multiple BOM edges', () => {
  const source = response()
  const shared = item('REUSED-MAKE')
  source.items[0].item.subassemblies = [
    { ...shared, quantityPerParent: 2 },
    { ...shared, quantityPerParent: 3 },
  ]
  const estimate = buildFulcrumQuoteEstimates(source).estimates[0]
  assert.equal(estimate.kind, 'subassembly')
  if (estimate.kind !== 'subassembly') return
  assert.equal(estimate.subassemblies.length, 1)
  assert.equal(estimate.subassemblies[0].partNumber, 'REUSED-MAKE')
  assert.equal(estimate.subassemblies[0].quantityPerParent, 5)
  assert.equal(estimate.subassemblies[0].quantitiesByParentQuantity[32], 160)
  assert.equal(estimate.processes.filter((process) => process.subassemblyId === estimate.subassemblies[0].id).length, 2)
})
