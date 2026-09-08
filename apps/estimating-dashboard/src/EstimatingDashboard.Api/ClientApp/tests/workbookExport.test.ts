import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import test from 'node:test'

import ExcelJS from 'exceljs'

import { calculateEstimate } from '../src/calculations.ts'
import {
  createSubassemblyDefaults,
  createSubassemblyEstimateDefaults,
} from '../src/estimateDefaults.ts'
import { buildSubassemblyWorkbook } from '../src/estimateWorkbookExport.ts'
import { importEstimateWorkbook } from '../src/estimateWorkbookImport.ts'

test('exports editable formula-driven costs with child roll-up values', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.metadata.customer = 'Test Customer'
  estimate.metadata.partNumber = 'TOP-100'
  estimate.metadata.revision = 'A'
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-200'
  child.revision = 'B'
  estimate.perQuantityMarginByQuantity[10] = 0.05
  child.perQuantityMarginByQuantity[10] = 0.125
  child.operations[2].setupMinutes = 60
  child.operations[2].runMinutes = 2
  child.materials[0] = {
    ...child.materials[0],
    description: 'Plate',
    unitOfMeasure: 'EA',
    partsQuantity: 2,
    unitPrice: 5,
  }
  child.processes[0] = {
    ...child.processes[0],
    description: 'Plating',
    setupCost: 100,
    runCostEach: 2,
  }
  estimate.subassemblies.push(child)
  estimate.processes[0] = {
    ...estimate.processes[0],
    description: child.partNumber,
    subassemblyId: child.id,
    quantityPerParent: 3,
  }
  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return

  const templatePath = new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url)
  const templateBytes = await fs.readFile(templatePath)
  const template = templateBytes.buffer.slice(
    templateBytes.byteOffset,
    templateBytes.byteOffset + templateBytes.byteLength,
  ) as ArrayBuffer
  const output = await buildSubassemblyWorkbook(template, estimate, result)
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(output)

  assert.deepEqual(
    workbook.worksheets.map((sheet) => sheet.name),
    ['Top Assy', ...Array.from({ length: 12 }, (_, index) => `Subassy ${index + 1}`), 'Top Assy Detail', 'Subassy 1 Detail'],
  )
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B2').value, 'Test Customer')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B3').value, 'TOP-100')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('A46').result, 'CHILD-200')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B46').result, 3)
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('C46').result, true)
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('D78').value, 'Per Quantity Margin %')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('F78').value, 0.05)
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('F78').numFmt, '0.0%')
  assert.equal(
    workbook.getWorksheet('Top Assy')?.getCell('F46').result,
    result.processes[0].unitCostByQuantity[10],
  )
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('B3').value, 'CHILD-200')
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('D62').value, 'Per Quantity Margin %')
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('F62').value, 0.125)
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('F62').numFmt, '0.0%')
  assert.equal(
    workbook.getWorksheet('Subassy 1')?.getCell('F63').result,
    result.subassemblies[0].quantities?.[10].unitCost,
  )
  assert.equal(workbook.getWorksheet('Subassy 1')?.state, 'visible')
  assert.equal(workbook.getWorksheet('Subassy 2')?.state, 'hidden')

  let formulaCount = 0
  let formulaErrorCount = 0
  workbook.eachSheet((sheet) => {
    sheet.eachRow({ includeEmpty: false }, (row) => {
      row.eachCell({ includeEmpty: false }, (cell) => {
        const value = cell.value
        if (value && typeof value === 'object' && ('formula' in value || 'sharedFormula' in value)) {
          formulaCount += 1
        }
        if (typeof value === 'string' && /^#(?:REF!|DIV\/0!|VALUE!|NAME\?|N\/A)$/.test(value)) {
          formulaErrorCount += 1
        }
        if (value && typeof value === 'object' && 'error' in value) {
          formulaErrorCount += 1
        }
      })
    })
  })
  assert.ok(formulaCount > 100)
  assert.match(workbook.getWorksheet('Top Assy')!.getCell('F80').formula, /F75:F77/)
  assert.equal(formulaErrorCount, 0)
})

test('API Make batches and nested roll-ups stay linked to editable quantities and preserve overflow rows', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [32]
  estimate.metadata.comments = '32 STOCK'
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'INSERT'
  child.comments = '8 STOCK'
  child.quantityPerParent = 4
  child.deriveQuantitiesFromParent = true
  const grandchild = createSubassemblyDefaults(1)
  grandchild.partNumber = 'NESTED'
  grandchild.quantityPerParent = 8
  grandchild.deriveQuantitiesFromParent = true
  child.processes[0] = { id: 'nested-link', description: 'NESTED', setupCost: 0, runCostEach: 0, subassemblyId: grandchild.id, quantityPerParent: 2 }
  estimate.processes[0] = { id: 'child-link', description: 'INSERT', setupCost: 0, runCostEach: 0, subassemblyId: child.id, quantityPerParent: 4 }
  child.operations = Array.from({ length: 30 }, (_, index) => ({ ...child.operations[2], id: `op-${index}`, setupMinutes: index + 1 }))
  estimate.subassemblies = [child, grandchild]
  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return
  const bytes = await fs.readFile(new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url))
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(await buildSubassemblyWorkbook(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer, estimate, result))
  assert.equal(workbook.getWorksheet('Top Assy')!.getCell('F6').value, '32 STOCK')
  assert.equal(workbook.getWorksheet('Subassy 1')!.getCell('F3').value, '8 STOCK')
  assert.equal(workbook.getWorksheet('Subassy 1')!.getCell('F13').result, 128)
  assert.match(workbook.getWorksheet('Subassy 1')!.getCell('F13').formula, /'Top Assy'!F13\*'Top Assy Detail'!\$B\$/)
  assert.equal(workbook.getWorksheet('Subassy 2')!.getCell('F13').result, 256)
  assert.match(workbook.getWorksheet('Subassy 2')!.getCell('F13').formula, /'Subassy 1'!F13\*'Subassy 1 Detail'!\$B\$/)
  assert.equal(workbook.getWorksheet('Subassy 1 Detail')!.getCell('B44').value, 30)
  assert.equal(workbook.getWorksheet('Subassy 1')!.getCell('F26').formula, "SUM('Subassy 1 Detail'!F15:F44)")
  assert.match(workbook.getWorksheet('Subassy 1 Detail')!.getCell('F15').formula, /MAX\(1,F\$13\)/)
  assert.match(workbook.getWorksheet('Subassy 1')!.getCell('F46').formula, /'Subassy 1 Detail'!F/)
  const imported = (await importEstimateWorkbook(await workbook.xlsx.writeBuffer())).estimate
  assert.equal(imported.kind, 'subassembly')
  if (imported.kind !== 'subassembly') return
  assert.equal(imported.subassemblies[0].operations.length, 30)
  assert.equal(imported.subassemblies[0].comments, '8 STOCK')
  assert.equal(imported.subassemblies[0].deriveQuantitiesFromParent, true)
  assert.equal(imported.subassemblies[0].quantityPerParent, 4)
  assert.equal(imported.subassemblies[1].quantityPerParent, 8)
  assert.equal(imported.subassemblies[0].processes[0].subassemblyId, imported.subassemblies[1].id)
  const importedResult = calculateEstimate(imported)
  assert.ok(importedResult.ok)
  assert.ok(Math.abs(importedResult.quantities[32].sellPrice - result.quantities[32].sellPrice) < 1e-8)
})
