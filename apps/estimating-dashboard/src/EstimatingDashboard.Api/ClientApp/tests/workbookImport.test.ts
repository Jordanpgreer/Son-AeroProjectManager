import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import test from 'node:test'

import ExcelJS from 'exceljs'

import { calculateEstimate } from '../src/calculations.ts'
import {
  createStandardEstimateDefaults,
  createSubassemblyDefaults,
  createSubassemblyEstimateDefaults,
} from '../src/estimateDefaults.ts'
import { buildCleanEstimateWorkbook } from '../src/estimateWorkbookDownload.ts'
import { buildSubassemblyWorkbook } from '../src/estimateWorkbookExport.ts'
import { importEstimateWorkbook } from '../src/estimateWorkbookImport.ts'
import { appendQuantityTier, MAX_QUANTITY_TIERS } from '../src/types.ts'

function exactArrayBuffer(bytes: Uint8Array) {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer
}

test('quantity tiers stop at the eight-column workbook limit', () => {
  const seven = [1, 2, 3, 4, 5, 6, 7]
  assert.deepEqual(appendQuantityTier(seven), [1, 2, 3, 4, 5, 6, 7, 14])
  const eight = Array.from({ length: MAX_QUANTITY_TIERS }, (_, index) => index + 1)
  assert.deepEqual(appendQuantityTier(eight), eight)
})

test('clean standard workbook round-trips custom quantities, facilities, and Column O notes', async () => {
  const estimate = createStandardEstimateDefaults()
  estimate.metadata.customer = 'Round Trip Customer'
  estimate.metadata.partNumber = 'PART-100'
  estimate.metadata.revision = 'C'
  estimate.metadata.comments = 'Customer requested split delivery.'
  estimate.quantities = [1, 3, 20, 125]
  estimate.yield = 0.92
  estimate.operations[3].setupMinutes = 30
  estimate.operations[3].runMinutes = 2
  estimate.operations[3].notes = 'SEQ 20 — verify first article'
  estimate.materials[0] = {
    ...estimate.materials[0],
    description: 'Plate',
    unitOfMeasure: 'EA',
    partsQuantity: 2,
    unitPrice: 4.5,
  }
  estimate.processes[0] = {
    ...estimate.processes[0],
    description: 'Anodize',
    setupCost: 40,
    runCostEach: 1.25,
  }
  estimate.facilitiesByQuantity = { 1: 2, 3: 1, 20: 0.5, 125: 0.25 }

  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return
  const output = await buildCleanEstimateWorkbook(estimate, result)
  const imported = await importEstimateWorkbook(exactArrayBuffer(output))

  assert.equal(imported.estimate.kind, 'standard')
  assert.equal(imported.estimate.metadata.customer, 'Round Trip Customer')
  assert.equal(imported.estimate.metadata.partNumber, 'PART-100')
  assert.equal(imported.estimate.metadata.revision, 'C')
  assert.equal(imported.estimate.metadata.comments, 'Customer requested split delivery.')
  assert.deepEqual(imported.estimate.quantities, [1, 3, 20, 125])
  assert.equal(imported.estimate.yield, 0.92)
  assert.equal(imported.estimate.operations[3].notes, 'SEQ 20 — verify first article')
  assert.equal(imported.operationNoteCount, 1)
  assert.deepEqual(imported.estimate.facilitiesByQuantity, { 1: 2, 3: 1, 20: 0.5, 125: 0.25 })
  assert.equal(imported.estimate.materials[0].description, 'Plate')
  assert.equal(imported.estimate.processes[0].description, 'Anodize')
})

test('subassembly workbook round-trips parent notes, child batch quantities, and links', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.metadata.partNumber = 'TOP-200'
  estimate.quantities = [1, 2, 5, 20]
  estimate.operations[2].notes = 'SEQ 10'
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-201'
  child.revision = 'B'
  child.quantitiesByParentQuantity = { 1: 1, 2: 1, 5: 2, 20: 5 }
  child.operations[2].setupMinutes = 60
  child.operations[2].runMinutes = 2
  child.operations[2].notes = 'SEQ 30'
  estimate.subassemblies.push(child)
  estimate.processes[0] = {
    ...estimate.processes[0],
    description: child.partNumber,
    subassemblyId: child.id,
    quantityPerParent: 0.25,
  }
  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return

  const templatePath = new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url)
  const template = exactArrayBuffer(await fs.readFile(templatePath))
  const output = await buildSubassemblyWorkbook(template, estimate, result)
  const imported = await importEstimateWorkbook(output)

  assert.equal(imported.estimate.kind, 'subassembly')
  if (imported.estimate.kind !== 'subassembly') return
  assert.deepEqual(imported.estimate.quantities, [1, 2, 5, 20])
  assert.equal(imported.estimate.operations[2].notes, 'SEQ 10')
  assert.equal(imported.estimate.subassemblies[0].operations[2].notes, 'SEQ 30')
  assert.deepEqual(
    imported.estimate.subassemblies[0].quantitiesByParentQuantity,
    { 1: 1, 2: 1, 5: 2, 20: 5 },
  )
  assert.equal(imported.estimate.processes[0].subassemblyId, imported.estimate.subassemblies[0].id)
  assert.equal(imported.estimate.processes[0].quantityPerParent, 0.25)
  assert.equal(imported.operationNoteCount, 2)
})

test('subassembly child setup and NRE allocation use imported child batch quantities', () => {
  const estimate = createSubassemblyEstimateDefaults()
  estimate.quantities = [20]
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-BATCH'
  child.quantitiesByParentQuantity = { 20: 5 }
  child.operations[0].setupMinutes = 60
  child.operations[2].setupMinutes = 60
  estimate.subassemblies.push(child)
  estimate.processes[0].subassemblyId = child.id

  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return
  const childResult = result.subassemblies[0]
  const production = childResult.operations[2]

  assert.equal(production.unitCostByQuantity[20], production.laborRate! * 12)
  assert.equal(childResult.quantities?.[20].amortizedNre, childResult.rawOneTimeNre! / 5)
})

test('unknown imported operation names remain editable and fail with an explicit missing-rate error', async () => {
  const estimate = createStandardEstimateDefaults()
  const initialResult = calculateEstimate(estimate)
  assert.equal(initialResult.ok, true)
  if (!initialResult.ok) return
  const output = await buildCleanEstimateWorkbook(estimate, initialResult)
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(output)
  workbook.worksheets[0].getCell('A17').value = 'Imported Custom Route'

  const imported = await importEstimateWorkbook(await workbook.xlsx.writeBuffer())
  assert.equal(imported.estimate.operations[3].name, 'Imported Custom Route')
  assert.equal(imported.estimate.operations[3].nameControl, 'rate-list')
  const calculation = calculateEstimate(imported.estimate)
  assert.equal(calculation.ok, false)
  if (calculation.ok) return
  assert.equal(calculation.errors[0].code, 'missing-rate')
  assert.match(calculation.errors[0].message, /Imported Custom Route/)
})

test('repeated workbook operation names receive distinct stable row identities', async () => {
  const estimate = createStandardEstimateDefaults()
  const result = calculateEstimate(estimate)
  assert.equal(result.ok, true)
  if (!result.ok) return
  const output = await buildCleanEstimateWorkbook(estimate, result)
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(output)
  workbook.worksheets[0].getCell('A14').value = 'ID & Pack'
  workbook.worksheets[0].getCell('A15').value = 'ID & Pack'

  const imported = await importEstimateWorkbook(await workbook.xlsx.writeBuffer())
  const ids = imported.estimate.operations.map((operation) => operation.id)
  assert.equal(new Set(ids).size, ids.length)
})

test('blank metadata values never consume adjacent labels or merged comments headings', async () => {
  const templatePath = new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url)
  const estimate = createSubassemblyEstimateDefaults()
  estimate.metadata.revision = ''
  estimate.metadata.solicitationNumber = ''
  estimate.metadata.comments = ''
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-WITH-BLANK-METADATA'
  child.revision = ''
  estimate.subassemblies.push(child)
  estimate.processes[0].description = child.partNumber
  estimate.processes[0].subassemblyId = child.id
  const calculation = calculateEstimate(estimate)
  assert.equal(calculation.ok, true)
  if (!calculation.ok) return
  const output = await buildSubassemblyWorkbook(
    exactArrayBuffer(await fs.readFile(templatePath)),
    estimate,
    calculation,
  )
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(output)
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('D4').value, 'SOL #:')
  assert.equal(workbook.getWorksheet('Top Assy')?.getCell('F5').value, 'Comments:')
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('D4').value, 'SOL #:')
  assert.equal(workbook.getWorksheet('Subassy 1')?.getCell('F2').value, 'Comments:')

  const imported = await importEstimateWorkbook(output)
  assert.equal(imported.estimate.metadata.revision, '')
  assert.equal(imported.estimate.metadata.solicitationNumber, '')
  assert.equal(imported.estimate.metadata.comments, '')
  assert.equal(imported.estimate.kind, 'subassembly')
  if (imported.estimate.kind !== 'subassembly') return
  assert.equal(imported.estimate.subassemblies[0].partNumber, 'CHILD-WITH-BLANK-METADATA')
  assert.equal(imported.estimate.subassemblies[0].revision, '')
})

test('workbook import rejects out-of-domain numeric inputs', async () => {
  const estimate = createStandardEstimateDefaults()
  const calculation = calculateEstimate(estimate)
  assert.equal(calculation.ok, true)
  if (!calculation.ok) return
  const output = await buildCleanEstimateWorkbook(estimate, calculation)

  const invalidYield = new ExcelJS.Workbook()
  await invalidYield.xlsx.load(output)
  invalidYield.worksheets[0].getCell('G3').value = 1.5
  await assert.rejects(
    async () => importEstimateWorkbook(exactArrayBuffer(await invalidYield.xlsx.writeBuffer())),
    /Expected yield.*must not exceed 1/,
  )

  const negativeSetup = new ExcelJS.Workbook()
  await negativeSetup.xlsx.load(output)
  negativeSetup.worksheets[0].getCell('B14').value = -1
  await assert.rejects(
    async () => importEstimateWorkbook(exactArrayBuffer(await negativeSetup.xlsx.writeBuffer())),
    /setup minutes.*must be at least 0/,
  )
})

test('workbook import rejects invalid child build quantities', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  const child = createSubassemblyDefaults(0)
  child.partNumber = 'CHILD-BUILD'
  estimate.subassemblies.push(child)
  estimate.processes[0].subassemblyId = child.id
  estimate.processes[0].description = child.partNumber
  const calculation = calculateEstimate(estimate)
  assert.equal(calculation.ok, true)
  if (!calculation.ok) return
  const templatePath = new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url)
  const output = await buildSubassemblyWorkbook(
    exactArrayBuffer(await fs.readFile(templatePath)),
    estimate,
    calculation,
  )
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(output)
  workbook.getWorksheet('Subassy 1')!.getCell('F13').value = 0

  await assert.rejects(
    async () => importEstimateWorkbook(exactArrayBuffer(await workbook.xlsx.writeBuffer())),
    /Child build quantity.*must be at least 1/,
  )
})

test('workbook import never degrades missing or duplicate subassembly links into ordinary processes', async () => {
  const estimate = createSubassemblyEstimateDefaults()
  const first = createSubassemblyDefaults(0)
  first.partNumber = 'CHILD-LINK'
  estimate.subassemblies.push(first)
  estimate.processes[0].subassemblyId = first.id
  estimate.processes[0].description = first.partNumber
  const calculation = calculateEstimate(estimate)
  assert.equal(calculation.ok, true)
  if (!calculation.ok) return
  const templatePath = new URL('../src/assets/subassembly-estimating-template.xlsx', import.meta.url)
  const template = exactArrayBuffer(await fs.readFile(templatePath))
  const output = await buildSubassemblyWorkbook(template, estimate, calculation)

  const missing = new ExcelJS.Workbook()
  await missing.xlsx.load(output)
  missing.getWorksheet('Top Assy')!.getCell('A46').value = 'MISSING-CHILD'
  await assert.rejects(
    async () => importEstimateWorkbook(exactArrayBuffer(await missing.xlsx.writeBuffer())),
    /no matching subassembly sheet was imported/,
  )

  const duplicateEstimate = createSubassemblyEstimateDefaults()
  const duplicateOne = createSubassemblyDefaults(0)
  const duplicateTwo = createSubassemblyDefaults(1)
  duplicateOne.partNumber = 'DUPLICATE-CHILD'
  duplicateTwo.partNumber = 'DUPLICATE-CHILD'
  duplicateEstimate.subassemblies.push(duplicateOne, duplicateTwo)
  duplicateEstimate.processes[0].subassemblyId = duplicateOne.id
  duplicateEstimate.processes[0].description = duplicateOne.partNumber
  const duplicateCalculation = calculateEstimate(duplicateEstimate)
  assert.equal(duplicateCalculation.ok, true)
  if (!duplicateCalculation.ok) return
  const duplicateOutput = await buildSubassemblyWorkbook(
    exactArrayBuffer(await fs.readFile(templatePath)),
    duplicateEstimate,
    duplicateCalculation,
  )
  await assert.rejects(
    () => importEstimateWorkbook(duplicateOutput),
    /Duplicate subassembly part number.*cannot be linked deterministically/,
  )
})
