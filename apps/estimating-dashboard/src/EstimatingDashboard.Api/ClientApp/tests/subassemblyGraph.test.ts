import assert from 'node:assert/strict'
import test from 'node:test'
import { createSubassemblyDefaults, createSubassemblyEstimateDefaults } from '../src/estimateDefaults.ts'
import { reconcileSubassemblyQuantities, removeSubassemblyGraph, updateSubassemblyGraph, validSubassemblyLinkTargets } from '../src/subassemblyGraph.ts'

function nested() {
  const input = createSubassemblyEstimateDefaults()
  input.quantities = [32, 50]
  const child = { ...createSubassemblyDefaults(0), partNumber: 'CHILD', quantityPerParent: 4, deriveQuantitiesFromParent: true }
  const grandchild = { ...createSubassemblyDefaults(1), partNumber: 'GRANDCHILD', quantityPerParent: 12, deriveQuantitiesFromParent: true }
  child.processes = [{ id: 'nested-link', description: 'GRANDCHILD', subassemblyId: grandchild.id, quantityPerParent: 3, setupCost: 0, runCostEach: 0 }]
  grandchild.processes = []
  input.subassemblies = [child, grandchild]
  input.processes = [{ id: 'root-link', description: 'CHILD', subassemblyId: child.id, quantityPerParent: 4, setupCost: 0, runCostEach: 0 }]
  return reconcileSubassemblyQuantities(input)
}

test('editing parent usage updates descendants while retaining direct edge quantity', () => {
  const input = nested()
  const updated = updateSubassemblyGraph(input, input.subassemblies[0].id, (child) => ({ ...child, quantityPerParent: 8 }))
  assert.equal(updated.processes[0].quantityPerParent, 8)
  assert.equal(updated.subassemblies[0].processes[0].quantityPerParent, 3)
  assert.equal(updated.subassemblies[1].quantityPerParent, 24)
  assert.equal(updated.subassemblies[1].quantitiesByParentQuantity[32], 768)
})

test('editing nested child usage or identity updates its owning process link', () => {
  const input = nested()
  const updated = updateSubassemblyGraph(input, input.subassemblies[1].id, (child) => ({ ...child, quantityPerParent: 24, partNumber: 'RENAMED' }))
  assert.equal(updated.subassemblies[0].processes[0].quantityPerParent, 6)
  assert.equal(updated.subassemblies[0].processes[0].description, 'RENAMED')
  assert.equal(updated.subassemblies[1].quantitiesByParentQuantity[32], 768)
})

test('removing nested child removes inner link, while removing parent removes exclusively-owned descendants', () => {
  const input = nested()
  const nestedRemoved = removeSubassemblyGraph(input, input.subassemblies[1].id)
  assert.equal(nestedRemoved.subassemblies.length, 1)
  assert.equal(nestedRemoved.subassemblies[0].processes.length, 0)
  const parentRemoved = removeSubassemblyGraph(input, input.subassemblies[0].id)
  assert.equal(parentRemoved.subassemblies.length, 0)
  assert.equal(parentRemoved.processes.length, 0)
})

test('removing a parent retains descendants referenced by a surviving root link', () => {
  const input = nested()
  input.processes.push({ id: 'shared', description: 'GRANDCHILD', subassemblyId: input.subassemblies[1].id, quantityPerParent: 2, setupCost: 0, runCostEach: 0 })
  const updated = removeSubassemblyGraph(input, input.subassemblies[0].id)
  assert.equal(updated.subassemblies.length, 1)
  assert.equal(updated.subassemblies[0].quantityPerParent, 2)
  assert.equal(updated.processes.length, 1)
})

test('nested selectors exclude self and ancestors that would create a cycle', () => {
  const input = nested()
  assert.deepEqual(validSubassemblyLinkTargets(input.subassemblies, input.subassemblies[1].id), [])
  assert.deepEqual(validSubassemblyLinkTargets(input.subassemblies, input.subassemblies[0].id).map((child) => child.partNumber), ['GRANDCHILD'])
})

test('manual custom child build quantities remain untouched on an unrelated edit', () => {
  const input = nested()
  input.subassemblies[1].deriveQuantitiesFromParent = false
  input.subassemblies[1].quantitiesByParentQuantity = { 32: 777, 50: 888 }
  const updated = updateSubassemblyGraph(input, input.subassemblies[1].id, (child) => ({ ...child, comments: '2 STOCK' }))
  assert.deepEqual(updated.subassemblies[1].quantitiesByParentQuantity, { 32: 777, 50: 888 })
})

test('removing one of multiple incoming Make links recalculates surviving demand', () => {
  const input = nested()
  const grandchild = input.subassemblies[1]
  input.processes.push({
    id: 'direct-grandchild-link',
    description: grandchild.partNumber,
    subassemblyId: grandchild.id,
    quantityPerParent: 2,
    setupCost: 0,
    runCostEach: 0,
  })
  const withBothLinks = reconcileSubassemblyQuantities(input)
  assert.equal(withBothLinks.subassemblies[1].quantityPerParent, 14)
  const afterRemoval = reconcileSubassemblyQuantities({
    ...withBothLinks,
    processes: withBothLinks.processes.filter((process) => process.id !== 'direct-grandchild-link'),
  })
  assert.equal(afterRemoval.subassemblies[1].quantityPerParent, 12)
  assert.equal(afterRemoval.subassemblies[1].quantitiesByParentQuantity[32], 384)
})
