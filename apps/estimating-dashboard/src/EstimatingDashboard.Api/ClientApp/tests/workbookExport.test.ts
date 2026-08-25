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

test('exports a populated workbook-shaped snapshot with child roll-up values and no formulas', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.metadata.customer = 'Test Customer'
  estimate.metadata.partNumber = 'TOP-100'
  estimate.metadata.revision = 'A'
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-200'
  child.revision = 'B'
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
    ['Top Assy', ...Array.from({ length: 12 }, (_, index) => `Subassy ${index + 1}`)],
  )
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B2').value, 'Test Customer')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B3').value, 'TOP-100')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('A46').value, 'CHILD-200')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('B46').value, 3)
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('C46').value, true)
  assert.equal(
    workbook.getWorksheet('Top Assy')?.getCell('F46').value,
    result.processes[0].unitCostByQuantity[10],
  )
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('B3').value, 'CHILD-200')
  assert.equal(
    workbook.getWorksheet('Subassy 1')?.getCell('F63').value,
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
  assert.equal(formulaCount, 0)
  assert.equal(formulaErrorCount, 0)
})
