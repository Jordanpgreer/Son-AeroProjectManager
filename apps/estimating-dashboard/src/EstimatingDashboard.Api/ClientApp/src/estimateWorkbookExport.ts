import ExcelJS from 'exceljs'

import { getAnnualRateAssumptions } from './estimatingRates.ts'
import type {
  EstimateCalculationSuccess,
  MaterialCostAudit,
  OperationCostAudit,
  ProcessCostAudit,
  QuantityTier,
  SubassemblyCalculationAudit,
  SubassemblyEstimateInput,
  SubassemblyInput,
} from './types.ts'

const WORKBOOK_COLUMNS = ['F', 'G', 'H', 'I', 'J', 'K', 'L', 'M'] as const
const templateUrl = new URL('./assets/subassembly-estimating-template.xlsx', import.meta.url).href

function setCell(sheet: ExcelJS.Worksheet, address: string, value: ExcelJS.CellValue) {
  sheet.getCell(address).value = value
}

function setQuantityRow(
  sheet: ExcelJS.Worksheet,
  row: number,
  quantities: readonly QuantityTier[],
  value: (quantity: QuantityTier) => number | null,
) {
  WORKBOOK_COLUMNS.forEach((column, index) => {
    const quantity = quantities[index]
    setCell(sheet, `${column}${row}`, quantity === undefined ? null : value(quantity))
  })
}

function writeOperations(
  sheet: ExcelJS.Worksheet,
  operations: SubassemblyInput['operations'],
  audits: readonly OperationCostAudit[],
  quantities: readonly QuantityTier[],
) {
  for (let index = 0; index < 12; index += 1) {
    const row = 14 + index
    const operation = operations[index]
    const audit = operation
      ? audits.find((candidate) => candidate.operationId === operation.id)
      : undefined
    setCell(sheet, `A${row}`, operation?.name ?? null)
    setCell(sheet, `B${row}`, operation?.setupMinutes ?? null)
    setCell(sheet, `C${row}`, operation?.runMinutes ?? null)
    setCell(sheet, `E${row}`, audit?.laborRate ?? null)
    setQuantityRow(sheet, row, quantities, (quantity) => (
      audit?.unitCostByQuantity[quantity] ?? 0
    ))
    setCell(sheet, `N${row}`, audit?.oneTimeNre ?? 0)
    setCell(sheet, `O${row}`, operation?.notes ?? null)
  }
}

function writeMaterials(
  sheet: ExcelJS.Worksheet,
  materials: SubassemblyInput['materials'],
  audits: readonly MaterialCostAudit[],
  quantities: readonly QuantityTier[],
) {
  for (let index = 0; index < 12; index += 1) {
    const row = 33 + index
    const material = materials[index]
    const audit = material
      ? audits.find((candidate) => candidate.materialId === material.id)
      : undefined
    setCell(sheet, `A${row}`, material?.description ?? null)
    setCell(sheet, `B${row}`, material?.unitOfMeasure ?? null)
    setCell(sheet, `C${row}`, material?.partsQuantity ?? null)
    setCell(sheet, `D${row}`, material?.unitPrice ?? null)
    setCell(sheet, `E${row}`, audit?.extendedCost ?? 0)
    setQuantityRow(sheet, row, quantities, (quantity) => (
      audit?.unitCostByQuantity[quantity] ?? 0
    ))
    setCell(sheet, `N${row}`, material?.amortizeMinBuy ?? false)
  }
}

function writeProcesses(
  sheet: ExcelJS.Worksheet,
  startRow: number,
  rowCount: number,
  processes: SubassemblyInput['processes'],
  audits: readonly ProcessCostAudit[],
  quantities: readonly QuantityTier[],
  subassemblies: readonly SubassemblyInput[] = [],
) {
  for (let index = 0; index < rowCount; index += 1) {
    const row = startRow + index
    const process = processes[index]
    const audit = process
      ? audits.find((candidate) => candidate.processId === process.id)
      : undefined
    const linked = process?.subassemblyId
      ? subassemblies.find((candidate) => candidate.id === process.subassemblyId)
      : undefined
    setCell(sheet, `A${row}`, linked?.partNumber || process?.description || null)
    setCell(sheet, `B${row}`, linked ? process?.quantityPerParent ?? 1 : null)
    setCell(sheet, `C${row}`, Boolean(linked))
    setCell(sheet, `D${row}`, linked ? 0 : process?.setupCost ?? null)
    setCell(sheet, `E${row}`, linked ? 0 : process?.runCostEach ?? null)
    setQuantityRow(sheet, row, quantities, (quantity) => (
      audit?.unitCostByQuantity[quantity] ?? 0
    ))
  }
}

function writeSharedMetadata(
  sheet: ExcelJS.Worksheet,
  estimate: SubassemblyEstimateInput,
  child?: SubassemblyInput,
) {
  setCell(sheet, 'B2', estimate.metadata.customer)
  setCell(sheet, 'B3', child?.partNumber ?? estimate.metadata.partNumber)
  setCell(sheet, 'B4', child?.revision ?? estimate.metadata.revision)
  setCell(sheet, 'B5', estimate.metadata.quoteLogNumber)
  setCell(sheet, 'B7', estimate.metadata.quoteDate)
  setCell(sheet, 'B8', estimate.metadata.estimator)
  setCell(sheet, 'E3', estimate.metadata.nsn)
  setCell(sheet, 'E4', estimate.metadata.solicitationNumber)
  setCell(sheet, 'E5', estimate.metadata.rfqNumber)
  setCell(sheet, child ? 'F3' : 'F6', estimate.metadata.comments)
}

function writeQuantityHeaders(
  sheet: ExcelJS.Worksheet,
  quantities: readonly QuantityTier[],
  displayQuantity: (quantity: QuantityTier) => number = (quantity) => quantity,
) {
  WORKBOOK_COLUMNS.forEach((column, index) => {
    const quantity = quantities[index]
    const displayed = quantity === undefined ? undefined : displayQuantity(quantity)
    setCell(sheet, `${column}13`, displayed ?? null)
    setCell(sheet, `${column}32`, displayed === undefined ? null : `Qty: ${displayed}`)
  })
}

function populateChildSheet(
  sheet: ExcelJS.Worksheet,
  estimate: SubassemblyEstimateInput,
  child: SubassemblyInput,
  audit: SubassemblyCalculationAudit,
) {
  writeSharedMetadata(sheet, estimate, child)
  writeQuantityHeaders(
    sheet,
    estimate.quantities,
    (quantity) => child.quantitiesByParentQuantity?.[quantity] ?? quantity,
  )
  writeOperations(sheet, child.operations, audit.operations, estimate.quantities)
  writeMaterials(sheet, child.materials, audit.materials, estimate.quantities)
  writeProcesses(sheet, 46, 5, child.processes, audit.processes, estimate.quantities)
  const assumptions = getAnnualRateAssumptions(estimate.rateYear)
  setCell(sheet, 'D28', assumptions.burden)
  setQuantityRow(sheet, 26, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.basicLabor ?? 0)
  setQuantityRow(sheet, 28, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.laborBurden ?? 0)
  setQuantityRow(sheet, 30, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.burdenedLabor ?? 0)
  setCell(sheet, 'N30', audit.rawOneTimeNre ?? 0)
  setQuantityRow(sheet, 51, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.rawMaterial ?? 0)
  setQuantityRow(sheet, 52, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.rawProcess ?? 0)
  setQuantityRow(sheet, 53, estimate.quantities, (quantity) => {
    const current = audit.quantities?.[quantity]
    return current ? current.burdenedLabor + current.rawMaterial + current.rawProcess : 0
  })
  setQuantityRow(sheet, 55, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.unitCost ?? 0)
  setQuantityRow(sheet, 56, estimate.quantities, () => 0)
  setQuantityRow(sheet, 57, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.burdenedLabor ?? 0)
  setQuantityRow(sheet, 58, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.rawMaterial ?? 0)
  setQuantityRow(sheet, 59, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.rawProcess ?? 0)
  setQuantityRow(sheet, 60, estimate.quantities, (quantity) => {
    const current = audit.quantities?.[quantity]
    return current ? current.burdenedLabor + current.rawMaterial + current.rawProcess : 0
  })
  setQuantityRow(sheet, 61, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.amortizedNre ?? 0)
  setCell(sheet, 'D62', 'Per Quantity Margin %')
  setQuantityRow(sheet, 62, estimate.quantities, (quantity) => child.perQuantityMarginByQuantity[quantity] ?? 0)
  WORKBOOK_COLUMNS.forEach((column) => { sheet.getCell(`${column}62`).numFmt = '0.0%' })
  setQuantityRow(sheet, 63, estimate.quantities, (quantity) => audit.quantities?.[quantity]?.unitCost ?? 0)
  setCell(sheet, 'D63', child.partNumber)
}

function populateParentSheet(
  sheet: ExcelJS.Worksheet,
  estimate: SubassemblyEstimateInput,
  result: EstimateCalculationSuccess,
) {
  writeSharedMetadata(sheet, estimate)
  writeQuantityHeaders(sheet, estimate.quantities)
  setCell(sheet, 'B9', estimate.rateYear)
  setCell(sheet, 'G3', estimate.yield)
  setCell(sheet, 'E79', estimate.salesMarkup)
  writeOperations(sheet, estimate.operations, result.operations, estimate.quantities)
  writeMaterials(sheet, estimate.materials, result.materials, estimate.quantities)
  writeProcesses(
    sheet,
    46,
    12,
    estimate.processes,
    result.processes,
    estimate.quantities,
    estimate.subassemblies,
  )
  const assumptions = getAnnualRateAssumptions(estimate.rateYear)
  setCell(sheet, 'D28', assumptions.burden)
  setCell(sheet, 'E62', assumptions.laborGa)
  setCell(sheet, 'E63', assumptions.materialGa)
  setCell(sheet, 'E64', assumptions.processGa)
  setCell(sheet, 'E66', assumptions.laborProfit)
  setCell(sheet, 'E67', assumptions.materialProfit)
  setCell(sheet, 'E68', assumptions.processProfit)
  setQuantityRow(sheet, 26, estimate.quantities, (quantity) => result.quantities[quantity].basicLabor)
  setQuantityRow(sheet, 28, estimate.quantities, (quantity) => result.quantities[quantity].laborBurden)
  setQuantityRow(sheet, 30, estimate.quantities, (quantity) => result.quantities[quantity].burdenedLabor)
  setCell(sheet, 'N30', result.rawOneTimeNre)
  setQuantityRow(sheet, 58, estimate.quantities, (quantity) => result.quantities[quantity].rawMaterial)
  setQuantityRow(sheet, 59, estimate.quantities, (quantity) => result.quantities[quantity].rawProcess)
  setQuantityRow(sheet, 60, estimate.quantities, (quantity) => result.quantities[quantity].preGaMaterialAndLabor)
  setQuantityRow(sheet, 62, estimate.quantities, (quantity) => result.quantities[quantity].labor.ga)
  setQuantityRow(sheet, 63, estimate.quantities, (quantity) => result.quantities[quantity].material.ga)
  setQuantityRow(sheet, 64, estimate.quantities, (quantity) => result.quantities[quantity].process.ga)
  setQuantityRow(sheet, 65, estimate.quantities, (quantity) => {
    const current = result.quantities[quantity]
    return current.preGaMaterialAndLabor + current.labor.ga + current.material.ga + current.process.ga
  })
  setQuantityRow(sheet, 66, estimate.quantities, (quantity) => result.quantities[quantity].labor.profit)
  setQuantityRow(sheet, 67, estimate.quantities, (quantity) => result.quantities[quantity].material.profit)
  setQuantityRow(sheet, 68, estimate.quantities, (quantity) => result.quantities[quantity].process.profit)
  setQuantityRow(sheet, 69, estimate.quantities, (quantity) => result.quantities[quantity].componentSubtotal)
  setQuantityRow(sheet, 70, estimate.quantities, (quantity) => result.quantities[quantity].componentSubtotal)
  setQuantityRow(sheet, 71, estimate.quantities, (quantity) => result.quantities[quantity].loadedComponentMargin)
  setQuantityRow(sheet, 72, estimate.quantities, (quantity) => result.quantities[quantity].labor.loaded)
  setQuantityRow(sheet, 73, estimate.quantities, (quantity) => result.quantities[quantity].material.loaded)
  setQuantityRow(sheet, 74, estimate.quantities, (quantity) => result.quantities[quantity].process.loaded)
  setQuantityRow(sheet, 75, estimate.quantities, (quantity) => result.quantities[quantity].componentSubtotal)
  setQuantityRow(sheet, 76, estimate.quantities, (quantity) => result.quantities[quantity].amortizedNre)
  setQuantityRow(sheet, 77, estimate.quantities, (quantity) => result.quantities[quantity].yieldAdjustment)
  setCell(sheet, 'D78', 'Per Quantity Margin %')
  setQuantityRow(sheet, 78, estimate.quantities, (quantity) => estimate.perQuantityMarginByQuantity[quantity] ?? 0)
  WORKBOOK_COLUMNS.forEach((column) => { sheet.getCell(`${column}78`).numFmt = '0.0%' })
  setQuantityRow(sheet, 79, estimate.quantities, (quantity) => result.quantities[quantity].salesMarkup)
  setQuantityRow(sheet, 80, estimate.quantities, (quantity) => result.quantities[quantity].sellPrice)
  setQuantityRow(sheet, 81, estimate.quantities, (quantity) => result.quantities[quantity].grossMargin)
  setQuantityRow(sheet, 82, estimate.quantities, (quantity) => result.quantities[quantity].materialPercentOfPrice)

  for (let index = 0; index < 12; index += 1) {
    const row = 92 + index
    const child = estimate.subassemblies[index]
    const audit = child
      ? result.subassemblies.find((candidate) => candidate.subassemblyId === child.id)
      : undefined
    setCell(sheet, `A${row}`, child?.partNumber ?? null)
    WORKBOOK_COLUMNS.forEach((_, quantityIndex) => {
      const quantity = estimate.quantities[quantityIndex]
      setCell(sheet, `${String.fromCharCode(66 + quantityIndex)}${row}`, (
        quantity === undefined ? null : audit?.quantities?.[quantity]?.unitCost ?? 0
      ))
    })
  }
}

function stripFormula(value: ExcelJS.CellValue): ExcelJS.CellValue {
  if (!value || typeof value !== 'object' || value instanceof Date) return value
  if ('formula' in value || 'sharedFormula' in value) {
    const result = 'result' in value ? value.result : null
    if (result && typeof result === 'object' && 'error' in result) return null
    return typeof result === 'string' && result.startsWith('#') ? null : result ?? null
  }
  return value
}

function stripAllFormulas(workbook: ExcelJS.Workbook) {
  workbook.eachSheet((sheet) => {
    sheet.eachRow({ includeEmpty: false }, (row) => {
      row.eachCell({ includeEmpty: false }, (cell) => {
        cell.value = stripFormula(cell.value)
        if (cell.note) cell.note = ''
      })
    })
  })
}

export async function buildSubassemblyWorkbook(
  template: ArrayBuffer,
  estimate: SubassemblyEstimateInput,
  result: EstimateCalculationSuccess,
): Promise<ArrayBuffer> {
  if (estimate.quantities.length > WORKBOOK_COLUMNS.length) {
    throw new Error('The workbook format supports up to eight quantity tiers.')
  }
  if (estimate.subassemblies.length > 12) {
    throw new Error('The workbook format supports up to twelve subassemblies.')
  }
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(template)
  const parent = workbook.getWorksheet('Top Assy')
  if (!parent) throw new Error('The export template is missing the Top Assy sheet.')
  populateParentSheet(parent, estimate, result)
  for (let index = 0; index < 12; index += 1) {
    const sheet = workbook.getWorksheet(`Subassy ${index + 1}`)
    if (!sheet) throw new Error(`The export template is missing Subassy ${index + 1}.`)
    const child = estimate.subassemblies[index]
    const audit = child
      ? result.subassemblies.find((candidate) => candidate.subassemblyId === child.id)
      : undefined
    if (child && audit) {
      populateChildSheet(sheet, estimate, child, audit)
      sheet.state = 'visible'
    } else {
      sheet.state = 'hidden'
    }
  }
  stripAllFormulas(workbook)
  workbook.calcProperties.fullCalcOnLoad = false
  return workbook.xlsx.writeBuffer()
}

function safeFilenamePart(value: string) {
  return [...value.trim()]
    .filter((character) => character.charCodeAt(0) >= 32)
    .join('')
    .replace(/[<>:"/\\|?*]+/g, '-')
    .replace(/\s+/g, ' ')
    .slice(0, 80)
}

export async function downloadSubassemblyWorkbook(
  estimate: SubassemblyEstimateInput,
  result: EstimateCalculationSuccess,
) {
  const response = await fetch(templateUrl)
  if (!response.ok) throw new Error('Could not load the subassembly workbook template.')
  const output = await buildSubassemblyWorkbook(await response.arrayBuffer(), estimate, result)
  const blob = new Blob([output], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  })
  const part = safeFilenamePart(estimate.metadata.partNumber) || 'Estimate'
  const revision = safeFilenamePart(estimate.metadata.revision)
  const filename = `${part}${revision ? ` Rev ${revision}` : ''} Subassembly Estimate.xlsx`
  const href = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(href)
}
