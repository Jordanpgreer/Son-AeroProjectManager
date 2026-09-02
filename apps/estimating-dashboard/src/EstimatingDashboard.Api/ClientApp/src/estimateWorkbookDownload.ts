import ExcelJS from 'exceljs'

import { downloadSubassemblyWorkbook } from './estimateWorkbookExport.ts'
import type {
  EstimateCalculationSuccess,
  EstimateInput,
  QuantityTier,
} from './types.ts'

const QUANTITY_COLUMNS = [6, 7, 8, 9, 10, 11, 12, 13] as const
const BLUE = 'FF1F4E79'
const LIGHT_BLUE = 'FFDCE6F1'
const RED = 'FFE23B2C'
const WHITE = 'FFFFFFFF'
const LIGHT_BORDER = 'FFB8C7D5'

function cell(sheet: ExcelJS.Worksheet, row: number, column: number, value: ExcelJS.CellValue) {
  sheet.getCell(row, column).value = value
}

function writeQuantities(
  sheet: ExcelJS.Worksheet,
  row: number,
  quantities: readonly QuantityTier[],
  value: (quantity: QuantityTier) => ExcelJS.CellValue,
) {
  QUANTITY_COLUMNS.forEach((column, index) => {
    const quantity = quantities[index]
    cell(sheet, row, column, quantity === undefined ? null : value(quantity))
  })
}

function sectionHeading(sheet: ExcelJS.Worksheet, row: number, title: string) {
  sheet.mergeCells(row, 1, row, 15)
  const heading = sheet.getCell(row, 1)
  heading.value = title
  heading.font = { bold: true, color: { argb: WHITE }, size: 11 }
  heading.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: BLUE } }
  heading.alignment = { vertical: 'middle' }
  sheet.getRow(row).height = 23
}

function tableHeader(sheet: ExcelJS.Worksheet, row: number, values: ExcelJS.CellValue[]) {
  values.forEach((value, index) => cell(sheet, row, index + 1, value))
  const range = sheet.getRow(row)
  range.font = { bold: true, color: { argb: BLUE }, size: 9 }
  range.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: LIGHT_BLUE } }
  range.alignment = { vertical: 'middle', wrapText: true }
  range.height = 28
}

function applyGrid(sheet: ExcelJS.Worksheet, startRow: number, endRow: number) {
  for (let row = startRow; row <= endRow; row += 1) {
    for (let column = 1; column <= 15; column += 1) {
      const target = sheet.getCell(row, column)
      target.border = {
        bottom: { style: 'hair', color: { argb: LIGHT_BORDER } },
      }
      target.alignment = { ...target.alignment, vertical: 'middle' }
    }
  }
}

function writeMetadata(sheet: ExcelJS.Worksheet, estimate: EstimateInput) {
  const metadata = estimate.metadata
  const rows: Array<[number, string, ExcelJS.CellValue, string, ExcelJS.CellValue]> = [
    [2, 'CUSTOMER:', metadata.customer, '', null],
    [3, 'P/N:', metadata.partNumber, 'NSN #:', metadata.nsn],
    [4, 'REV:', metadata.revision, 'SOL #:', metadata.solicitationNumber],
    [5, 'QUOTE LOG No.:', metadata.quoteLogNumber, 'RFQ #:', metadata.rfqNumber],
    [7, 'DATE:', metadata.quoteDate, '', null],
    [8, 'ESTIMATOR:', metadata.estimator, '', null],
    [9, 'Labor Rate Year:', estimate.rateYear, '', null],
  ]
  rows.forEach(([row, leftLabel, leftValue, rightLabel, rightValue]) => {
    cell(sheet, row, 1, leftLabel)
    cell(sheet, row, 2, leftValue)
    if (rightLabel) {
      cell(sheet, row, 4, rightLabel)
      cell(sheet, row, 5, rightValue)
    }
  })
  cell(sheet, 3, 6, estimate.kind === 'rubber' ? 'Complexity:' : 'Yield %:')
  cell(sheet, 3, 7, estimate.kind === 'rubber' ? estimate.difficulty : estimate.yield)
  if (estimate.kind === 'rubber') {
    cell(sheet, 4, 6, 'Yield %:')
    cell(sheet, 4, 7, estimate.yield)
    cell(sheet, 3, 12, '# of Cavities:')
    cell(sheet, 3, 13, estimate.cavities)
    cell(sheet, 4, 12, 'Tooling Markup:')
    cell(sheet, 4, 13, estimate.toolingMarkup)
  }
  cell(sheet, 5, 6, 'Comments:')
  cell(sheet, 5, 7, metadata.comments)
  cell(sheet, 6, 1, 'QTY:')
  cell(sheet, 6, 2, estimate.quantities.join(', '))
  sheet.getCell('G3').numFmt = estimate.kind === 'rubber' ? '0' : '0.0%'
  sheet.getCell(4, 7).numFmt = '0.0%'
  sheet.getCell(4, 13).numFmt = '0.0%'
  for (const address of ['A2', 'A3', 'A4', 'A5', 'A6', 'A7', 'A8', 'A9', 'D3', 'D4', 'D5', 'F3', 'F4', 'F5', 'L3', 'L4']) {
    sheet.getCell(address).font = { bold: true, color: { argb: BLUE }, size: 9 }
  }
}

export async function buildCleanEstimateWorkbook(
  estimate: Exclude<EstimateInput, { kind: 'subassembly' }>,
  result: EstimateCalculationSuccess,
) {
  if (estimate.quantities.length > QUANTITY_COLUMNS.length) {
    throw new Error('The workbook format supports up to eight quantity tiers.')
  }
  const workbook = new ExcelJS.Workbook()
  workbook.creator = 'Arda Estimating'
  workbook.created = new Date()
  const sheet = workbook.addWorksheet(
    estimate.kind === 'rubber' ? 'Rubber Estimate' : 'Standard Estimate',
    { views: [{ state: 'frozen', ySplit: 13, activeCell: 'A14' }] },
  )
  sheet.pageSetup = {
    orientation: 'landscape',
    fitToPage: true,
    fitToWidth: 1,
    fitToHeight: 0,
    margins: { left: 0.25, right: 0.25, top: 0.4, bottom: 0.4, header: 0.2, footer: 0.2 },
  }
  sheet.mergeCells('A1:O1')
  cell(sheet, 1, 1, 'ARDA ESTIMATE WORKSHEET')
  sheet.getCell('A1').font = { bold: true, color: { argb: WHITE }, size: 15 }
  sheet.getCell('A1').fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: BLUE } }
  sheet.getCell('A1').alignment = { vertical: 'middle' }
  sheet.getRow(1).height = 31
  writeMetadata(sheet, estimate)

  const operationSection = 11
  const operationHeader = 13
  const operationStart = 14
  const quantityHeaders = QUANTITY_COLUMNS.map((_, index) => estimate.quantities[index] ?? null)
  sectionHeading(sheet, operationSection, 'MANUFACTURING OPERATIONS')
  tableHeader(sheet, operationHeader, [
    'Operation', 'SET-UP TIME', 'RUN TIME', 'Treatment', 'LABOR RATE',
    ...quantityHeaders, 'NRE / SET-UP', 'Notes / Sequence',
  ])
  estimate.operations.forEach((operation, index) => {
    const row = operationStart + index
    const audit = result.operations.find((candidate) => candidate.operationId === operation.id)
    cell(sheet, row, 1, operation.name)
    cell(sheet, row, 2, operation.setupMinutes)
    cell(sheet, row, 3, operation.runMinutes)
    cell(sheet, row, 4, operation.costTreatment === 'production' ? 'Production' : 'NRE')
    cell(sheet, row, 5, audit?.laborRate ?? null)
    writeQuantities(sheet, row, estimate.quantities, (quantity) => audit?.unitCostByQuantity[quantity] ?? 0)
    cell(sheet, row, 14, audit?.oneTimeNre ?? 0)
    cell(sheet, row, 15, operation.notes ?? '')
  })
  const operationEnd = operationStart + Math.max(estimate.operations.length, 1) - 1
  applyGrid(sheet, operationHeader, operationEnd)

  const materialSection = operationEnd + 2
  const materialHeader = materialSection + 1
  const materialStart = materialHeader + 1
  sectionHeading(sheet, materialSection, 'RAW MATERIALS & HARDWARE')
  tableHeader(sheet, materialHeader, [
    'Material', 'Matl UoM', 'Parts QTY', 'Unit Price', 'Extended',
    ...quantityHeaders.map((quantity) => quantity === null ? null : `Qty: ${quantity}`), 'Amortize / Min Buy', '',
  ])
  estimate.materials.forEach((material, index) => {
    const row = materialStart + index
    const audit = result.materials.find((candidate) => candidate.materialId === material.id)
    cell(sheet, row, 1, material.description)
    cell(sheet, row, 2, material.unitOfMeasure)
    cell(sheet, row, 3, material.partsQuantity)
    cell(sheet, row, 4, material.unitPrice)
    cell(sheet, row, 5, audit?.extendedCost ?? 0)
    writeQuantities(sheet, row, estimate.quantities, (quantity) => audit?.unitCostByQuantity[quantity] ?? 0)
    cell(sheet, row, 14, material.amortizeMinBuy)
    cell(sheet, row, 15, material.notes ?? '')
  })
  const materialEnd = materialStart + Math.max(estimate.materials.length, 1) - 1
  applyGrid(sheet, materialHeader, materialEnd)

  const processSection = materialEnd + 2
  const processHeader = processSection + 1
  const processStart = processHeader + 1
  sectionHeading(sheet, processSection, 'PROCESSES')
  tableHeader(sheet, processHeader, [
    'Process', '', '', 'SETUP $', 'RUN $ EA.', ...quantityHeaders, '', '',
  ])
  estimate.processes.forEach((process, index) => {
    const row = processStart + index
    const audit = result.processes.find((candidate) => candidate.processId === process.id)
    cell(sheet, row, 1, process.description)
    cell(sheet, row, 4, process.setupCost)
    cell(sheet, row, 5, process.runCostEach)
    writeQuantities(sheet, row, estimate.quantities, (quantity) => audit?.unitCostByQuantity[quantity] ?? 0)
  })
  const processEnd = processStart + Math.max(estimate.processes.length, 1) - 1
  const totalsRow = processEnd + 1
  cell(sheet, totalsRow, 2, 'MATERIAL & HARDWARE TOTAL')
  writeQuantities(sheet, totalsRow, estimate.quantities, (quantity) => result.quantities[quantity].rawMaterial)
  applyGrid(sheet, processHeader, totalsRow)

  const perQuantityMarginRow = totalsRow + 3
  cell(sheet, perQuantityMarginRow - 1, 2, 'Sales/Management Markup')
  cell(sheet, perQuantityMarginRow - 1, 5, estimate.salesMarkup)
  sheet.getCell(perQuantityMarginRow - 1, 5).numFmt = '0.0%'
  cell(sheet, perQuantityMarginRow, 4, 'Per Quantity Margin %')
  writeQuantities(
    sheet,
    perQuantityMarginRow,
    estimate.quantities,
    (quantity) => estimate.perQuantityMarginByQuantity[quantity] ?? 0,
  )

  const pricingSection = perQuantityMarginRow + 2
  sectionHeading(sheet, pricingSection, 'CALCULATED PRICING')
  const pricingRows: Array<[string, (quantity: QuantityTier) => number, string]> = [
    ['Sell Price', (quantity) => result.quantities[quantity].sellPrice, '$#,##0.00'],
    ['Per Quantity Margin', (quantity) => result.quantities[quantity].perQuantityMargin, '$#,##0.00'],
    ['Extended Value', (quantity) => result.quantities[quantity].extendedValue, '$#,##0.00'],
    ['Gross Margin', (quantity) => result.quantities[quantity].grossMargin ?? 0, '0.0%'],
    ['Material % of Price', (quantity) => result.quantities[quantity].materialPercentOfPrice ?? 0, '0.0%'],
  ]
  pricingRows.forEach(([label, value, numberFormat], index) => {
    const row = pricingSection + 1 + index
    cell(sheet, row, 4, label)
    writeQuantities(sheet, row, estimate.quantities, value)
    QUANTITY_COLUMNS.forEach((column) => { sheet.getCell(row, column).numFmt = numberFormat })
  })
  sheet.getRow(pricingSection + 1).font = { bold: true, color: { argb: BLUE } }

  sheet.columns = [
    { width: 30 }, { width: 14 }, { width: 13 }, { width: 16 }, { width: 14 },
    ...Array.from({ length: 8 }, () => ({ width: 13 })),
    { width: 16 }, { width: 24 },
  ]
  for (let row = 1; row <= sheet.rowCount; row += 1) {
    sheet.getRow(row).font = { ...sheet.getRow(row).font, name: 'Aptos', size: sheet.getRow(row).font?.size ?? 9 }
  }
  sheet.getColumn(4).numFmt = '$#,##0.00'
  sheet.getColumn(5).numFmt = '$#,##0.00'
  QUANTITY_COLUMNS.forEach((column) => { sheet.getColumn(column).numFmt = '$#,##0.00' })
  sheet.getColumn(15).alignment = { wrapText: true, vertical: 'top' }
  QUANTITY_COLUMNS.forEach((column) => { sheet.getCell(perQuantityMarginRow, column).numFmt = '0.0%' })
  sheet.getCell(perQuantityMarginRow, 4).fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: LIGHT_BLUE } }
  sheet.getCell(perQuantityMarginRow, 4).font = { bold: true, color: { argb: RED } }
  workbook.calcProperties.fullCalcOnLoad = false
  return workbook.xlsx.writeBuffer()
}

function safeFilenamePart(value: string) {
  return [...value.trim()]
    .map((character) => character.charCodeAt(0) < 32 ? '-' : character)
    .join('')
    .replace(/[<>:"/\\|?*]+/g, '-')
    .replace(/\s+/g, ' ')
    .slice(0, 80)
}

function downloadBuffer(buffer: ArrayBuffer, filename: string) {
  const blob = new Blob([buffer], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  })
  const href = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(href)
}

export async function downloadEstimateWorkbook(
  estimate: EstimateInput,
  result: EstimateCalculationSuccess,
) {
  if (estimate.kind === 'subassembly') {
    await downloadSubassemblyWorkbook(estimate, result)
    return
  }
  const output = await buildCleanEstimateWorkbook(estimate, result)
  const part = safeFilenamePart(estimate.metadata.partNumber) || 'Estimate'
  const partRevision = safeFilenamePart(estimate.metadata.revision)
  downloadBuffer(output, `${part}${partRevision ? ` Rev ${partRevision}` : ''} Estimate.xlsx`)
}
