import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { calculateEstimate } from '../src/calculations.ts'
import { createSubassemblyDefaults, createSubassemblyEstimateDefaults } from '../src/estimateDefaults.ts'
import { buildSubassemblyWorkbook } from '../src/estimateWorkbookExport.ts'
import { replaceEstimateQuantities } from '../src/types.ts'

test('native Excel recalculates nested BOM pricing after editing the top quantity', { skip: process.env.ARDA_TEST_EXCEL !== '1' }, async () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [32]
  estimate.yield = 0.93
  estimate.salesMarkup = 0.12
  estimate.perQuantityMarginByQuantity = { 32: 0.07 }
  estimate.operations[0].setupMinutes = 15
  estimate.operations[2].setupMinutes = 60
  estimate.operations[2].runMinutes = 3
  estimate.materials[0] = { ...estimate.materials[0], partsQuantity: 2, unitPrice: 4 }
  const child = createSubassemblyDefaults(0)
  const nested = createSubassemblyDefaults(1)
  child.quantityPerParent = 4
  nested.quantityPerParent = 8
  for (const part of [child, nested]) {
    part.deriveQuantitiesFromParent = true
    part.operations[0].setupMinutes = 12
    part.operations[2].setupMinutes = 30
    part.operations[2].runMinutes = 2
    part.materials[0] = { ...part.materials[0], partsQuantity: 3, unitPrice: 1.5 }
    part.materials[1] = { ...part.materials[1], partsQuantity: 250, unitPrice: 1.5, amortizeMinBuy: true }
    part.perQuantityMarginByQuantity = { 32: 0.08 }
  }
  nested.processes[0] = { id: 'finish', description: 'Finish', setupCost: 50, runCostEach: 2 }
  child.processes[0] = { id: 'nested', description: 'Nested', setupCost: 0, runCostEach: 0, subassemblyId: nested.id, quantityPerParent: 2 }
  estimate.processes[0] = { id: 'child', description: 'Child', setupCost: 0, runCostEach: 0, subassemblyId: child.id, quantityPerParent: 4 }
  estimate.subassemblies = [child, nested]
  const result = calculateEstimate(estimate)
  assert.ok(result.ok)
  const template = await fs.readFile(new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url))
  const output = await buildSubassemblyWorkbook(template.buffer.slice(template.byteOffset, template.byteOffset + template.byteLength) as ArrayBuffer, estimate, result)
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'arda-excel-test-'))
  const filename = path.join(directory, 'nested-quote.xlsx')
  try {
    await fs.writeFile(filename, new Uint8Array(output))
    const snapshots = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', fileURLToPath(new URL('./recalculateWorkbook.ps1', import.meta.url)), '-WorkbookPath', filename], { encoding: 'utf8', timeout: 60000 })) as Array<Record<string, number>>
    for (const snapshot of snapshots) {
      const expected = calculateEstimate(replaceEstimateQuantities(estimate, [snapshot.quantity]))
      assert.ok(expected.ok)
      const tier = expected.quantities[snapshot.quantity]
      const expectedValues = { childQuantity: snapshot.quantity * 4, nestedQuantity: snapshot.quantity * 8,
        childCost: expected.subassemblies[0].quantities![snapshot.quantity].unitCost,
        nestedCost: expected.subassemblies[1].quantities![snapshot.quantity].unitCost,
        labor: tier.burdenedLabor, material: tier.rawMaterial, process: tier.rawProcess, nre: tier.amortizedNre,
        yieldAdjustment: tier.yieldAdjustment, salesMarkup: tier.salesMarkup, sellPrice: tier.sellPrice, formulaErrors: 0 }
      for (const [field, value] of Object.entries(expectedValues)) {
        assert.ok(Math.abs(snapshot[field] - value) < 1e-8, `${snapshot.quantity} ${field}: Excel=${snapshot[field]}, calculator=${value}`)
      }
    }
  } finally { await fs.unlink(filename); await fs.rmdir(directory) }
})
