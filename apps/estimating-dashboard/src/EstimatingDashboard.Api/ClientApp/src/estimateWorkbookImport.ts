import ExcelJS from 'exceljs'

import {
  createEstimateDefaults,
  createSubassemblyDefaults,
} from './estimateDefaults.ts'
import type {
  EstimateInput,
  EstimateOperationInput,
  EstimateYear,
  MaterialInput,
  ProcessInput,
  QuantityTier,
  RubberDifficulty,
  SubassemblyEstimateInput,
  SubassemblyInput,
} from './types.ts'
import { ESTIMATE_YEARS } from './types.ts'

const QUANTITY_COLUMNS = [6, 7, 8, 9, 10, 11, 12, 13] as const
const MAX_WORKBOOK_BYTES = 25 * 1024 * 1024

export interface EstimateWorkbookImportResult {
  estimate: EstimateInput
  sourceSheet: string
  operationNoteCount: number
  warnings: string[]
}

type WorkbookSections = {
  operationHeader: number
  materialHeader: number
  processHeader: number
  processEnd: number
}

function materialDataStart(sheet: ExcelJS.Worksheet, sectionRow: number) {
  return /(?:matl|material)\s*u\/?o\/?m/i.test(cellText(sheet, sectionRow + 1, 2))
    ? sectionRow + 2
    : sectionRow + 1
}

function processDataStart(sheet: ExcelJS.Worksheet, sectionRow: number) {
  return /setup/i.test(cellText(sheet, sectionRow + 1, 4))
    ? sectionRow + 2
    : sectionRow + 1
}

function rawValue(value: ExcelJS.CellValue): unknown {
  if (value && typeof value === 'object' && !(value instanceof Date)) {
    if ('result' in value) return value.result
    if ('richText' in value) return value.richText.map((run) => run.text).join('')
    if ('text' in value) return value.text
  }
  return value
}

function cellText(sheet: ExcelJS.Worksheet, row: number, column: number) {
  const value = rawValue(sheet.getCell(row, column).value)
  if (value == null) return ''
  if (value instanceof Date) return value.toISOString().slice(0, 10)
  return String(value).trim()
}

function cellNumber(sheet: ExcelJS.Worksheet, row: number, column: number) {
  const value = rawValue(sheet.getCell(row, column).value)
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const isPercentage = value.includes('%')
    const parsed = Number(value.replace(/[$,%\s]/g, ''))
    if (!Number.isFinite(parsed)) return null
    return isPercentage ? parsed / 100 : parsed
  }
  return null
}

type NumericConstraints = {
  min?: number
  max?: number
  integer?: boolean
}

function validateImportedNumber(
  value: number,
  label: string,
  { min, max = Number.MAX_SAFE_INTEGER, integer = false }: NumericConstraints = {},
) {
  if (!Number.isFinite(value)) throw new Error(`${label} must be a finite number.`)
  if (integer && !Number.isInteger(value)) throw new Error(`${label} must be a whole number.`)
  if (min !== undefined && value < min) throw new Error(`${label} must be at least ${min}.`)
  if (value > max) throw new Error(`${label} must not exceed ${max}.`)
  return value
}

function constrainedCellNumber(
  sheet: ExcelJS.Worksheet,
  row: number,
  column: number,
  label: string,
  constraints?: NumericConstraints,
) {
  const cell = sheet.getCell(row, column)
  const original = cell.value
  const value = rawValue(original)
  const isBlank = value == null || (typeof value === 'string' && value.trim() === '')
  if (isBlank) {
    if (original && typeof original === 'object' && 'formula' in original) {
      throw new Error(`${label} in ${sheet.name}!${cell.address} has no cached formula result.`)
    }
    return null
  }
  const parsed = cellNumber(sheet, row, column)
  if (parsed === null) throw new Error(`${label} in ${sheet.name}!${cell.address} must be numeric.`)
  return validateImportedNumber(parsed, `${label} in ${sheet.name}!${cell.address}`, constraints)
}

function cellBoolean(sheet: ExcelJS.Worksheet, row: number, column: number) {
  const value = rawValue(sheet.getCell(row, column).value)
  if (typeof value === 'boolean') return value
  if (typeof value === 'number') return value !== 0
  return /^(?:true|yes|y|1)$/i.test(String(value ?? '').trim())
}

function normalized(value: string) {
  return value.trim().replace(/\s+/g, ' ').toLocaleLowerCase()
}

function findRow(
  sheet: ExcelJS.Worksheet,
  predicate: (value: string, row: number, column: number) => boolean,
  start = 1,
) {
  for (let row = start; row <= Math.min(sheet.rowCount, 250); row += 1) {
    for (let column = 1; column <= Math.min(sheet.columnCount, 20); column += 1) {
      const value = cellText(sheet, row, column)
      if (predicate(value, row, column)) return row
    }
  }
  return 0
}

function findLabel(
  sheet: ExcelJS.Worksheet,
  pattern: RegExp,
) {
  for (let row = 1; row <= Math.min(sheet.rowCount, 180); row += 1) {
    for (let column = 1; column <= Math.min(sheet.columnCount, 20); column += 1) {
      if (pattern.test(cellText(sheet, row, column))) return { row, column }
    }
  }
  return null
}

function columnNumber(columnLetters: string) {
  return [...columnLetters.toUpperCase()].reduce(
    (total, character) => total * 26 + character.charCodeAt(0) - 64,
    0,
  )
}

function decodeAddress(address: string) {
  const match = /^([A-Z]+)(\d+)$/i.exec(address)
  if (!match) throw new Error(`Invalid workbook cell address: ${address}`)
  return { row: Number(match[2]), column: columnNumber(match[1]) }
}

function labelRange(sheet: ExcelJS.Worksheet, row: number, column: number) {
  for (const mergedRange of sheet.model.merges) {
    const [startAddress, endAddress] = mergedRange.split(':')
    const start = decodeAddress(startAddress)
    const end = decodeAddress(endAddress ?? startAddress)
    if (row >= start.row && row <= end.row && column >= start.column && column <= end.column) {
      return { startRow: start.row, endRow: end.row, startColumn: start.column, endColumn: end.column }
    }
  }
  return { startRow: row, endRow: row, startColumn: column, endColumn: column }
}

function rightText(sheet: ExcelJS.Worksheet, pattern: RegExp) {
  const label = findLabel(sheet, pattern)
  if (!label) return ''
  const range = labelRange(sheet, label.row, label.column)
  return cellText(sheet, label.row, range.endColumn + 1)
}

function rightConstrainedNumber(
  sheet: ExcelJS.Worksheet,
  pattern: RegExp,
  labelText: string,
  constraints?: NumericConstraints,
) {
  const label = findLabel(sheet, pattern)
  if (!label) return null
  const range = labelRange(sheet, label.row, label.column)
  return constrainedCellNumber(
    sheet,
    label.row,
    range.endColumn + 1,
    labelText,
    constraints,
  )
}

function commentsText(sheet: ExcelJS.Worksheet) {
  const label = findLabel(sheet, /^comments:?$/i)
  if (!label) return ''
  const range = labelRange(sheet, label.row, label.column)
  if (range.endColumn > range.startColumn || range.endRow > range.startRow) {
    return cellText(sheet, range.endRow + 1, range.startColumn)
  }
  return cellText(sheet, label.row, label.column + 1)
    || cellText(sheet, label.row + 1, label.column)
}

function findSections(sheet: ExcelJS.Worksheet): WorkbookSections {
  const operationHeader = findRow(
    sheet,
    (value, _row, column) => column <= 3 && /^(?:set-?up|setup) time$/i.test(value),
  )
  const materialHeader = findRow(
    sheet,
    (value, _row, column) => column === 1 && /raw materials?\s*&\s*hardware/i.test(value),
  )
  const processHeader = findRow(
    sheet,
    (value, _row, column) => column === 1 && /^process(?:es|es\/components)?$/i.test(value),
  )
  const processEnd = findRow(
    sheet,
    (value, row, column) => row > processHeader && column <= 2 && /material\s*&\s*hardware total/i.test(value),
    processHeader + 1,
  )
  if (!operationHeader || !materialHeader || !processHeader || !processEnd) {
    throw new Error('This workbook does not match the supported Arda estimating-sheet layout.')
  }
  return { operationHeader, materialHeader, processHeader, processEnd }
}

function readQuantities(sheet: ExcelJS.Worksheet, headerRow: number) {
  const fromHeaders = QUANTITY_COLUMNS
    .map((column, index) => constrainedCellNumber(
      sheet,
      headerRow,
      column,
      `Quote quantity ${index + 1}`,
      { min: 1, integer: true },
    ))
    .filter((value): value is number => value !== null)
  const summaryText = rightText(sheet, /^qty:?$/i)
  const fromSummary = (summaryText.match(/[-+]?\d+(?:\.\d+)?/g) ?? []).map((token, index) => (
    validateImportedNumber(Number(token), `Summary quote quantity ${index + 1}`, { min: 1, integer: true })
  ))
  if (fromSummary.length > QUANTITY_COLUMNS.length) {
    throw new Error('The workbook has more than eight quote quantities; this layout supports eight.')
  }
  const selected = fromHeaders.length ? fromHeaders : fromSummary
  if (new Set(selected).size !== selected.length) {
    throw new Error('Each imported quote quantity must be unique.')
  }
  const quantities = [...selected]
  if (!quantities.length) throw new Error('No positive quote quantities were found in the workbook.')
  return quantities as QuantityTier[]
}

function importedId(prefix: string, index: number) {
  return `${prefix}-${index + 1}`
}

function readOperations(
  sheet: ExcelJS.Worksheet,
  startRow: number,
  endRow: number,
  defaults: readonly EstimateOperationInput[],
  idPrefix: string,
) {
  const defaultByName = new Map(defaults.map((operation) => [normalized(operation.name), operation]))
  const operations: EstimateOperationInput[] = []
  const usedIds = new Set<string>()
  for (let row = startRow; row <= endRow; row += 1) {
    const name = cellText(sheet, row, 1)
    if (!name) continue
    const matched = defaultByName.get(normalized(name))
    const inferredNre = /^(?:program|fixtures|mold\/tooling)$/i.test(name)
    const preferredId = matched?.id ?? importedId(idPrefix, operations.length)
    let id = preferredId
    for (let duplicate = 2; usedIds.has(id); duplicate += 1) {
      id = `${preferredId}-${duplicate}`
    }
    usedIds.add(id)
    operations.push({
      id,
      name,
      notes: cellText(sheet, row, 15),
      nameControl: matched?.nameControl ?? 'rate-list',
      setupMinutes: constrainedCellNumber(sheet, row, 2, `${name} setup minutes`, { min: 0 }) ?? 0,
      runMinutes: constrainedCellNumber(sheet, row, 3, `${name} run minutes`, { min: 0 }) ?? 0,
      costTreatment: matched?.costTreatment ?? (inferredNre ? 'nre' : 'production'),
      amortizeNre: matched?.costTreatment === 'conditional-tooling-nre'
        ? cellBoolean(sheet, row, 4)
        : false,
    })
  }
  return operations.length ? operations : [...defaults]
}

function readMaterials(
  sheet: ExcelJS.Worksheet,
  startRow: number,
  endRow: number,
  defaults: readonly MaterialInput[],
  idPrefix: string,
) {
  const materials: MaterialInput[] = []
  for (let row = startRow; row <= endRow; row += 1) {
    const description = cellText(sheet, row, 1)
    const unitOfMeasure = cellText(sheet, row, 2)
    const partsQuantity = constrainedCellNumber(sheet, row, 3, `Material row ${row} parts quantity`, { min: 0 }) ?? 0
    const unitPrice = constrainedCellNumber(sheet, row, 4, `Material row ${row} unit price`, { min: 0 }) ?? 0
    const amortizeMinBuy = cellBoolean(sheet, row, 14)
    if (!description && !unitOfMeasure && !partsQuantity && !unitPrice && !amortizeMinBuy) continue
    materials.push({
      id: importedId(idPrefix, materials.length),
      description,
      unitOfMeasure,
      partsQuantity,
      unitPrice,
      amortizeMinBuy,
    })
  }
  return materials.length ? materials : [...defaults]
}

function readProcesses(
  sheet: ExcelJS.Worksheet,
  startRow: number,
  endRow: number,
  defaults: readonly ProcessInput[],
  idPrefix: string,
  subassemblies: readonly SubassemblyInput[] = [],
) {
  const childByPart = new Map<string, SubassemblyInput>()
  for (const child of subassemblies) {
    const key = normalized(child.partNumber)
    if (!key) continue
    if (childByPart.has(key)) {
      throw new Error(`Duplicate subassembly part number "${child.partNumber.trim()}" cannot be linked deterministically.`)
    }
    childByPart.set(key, child)
  }
  const processes: ProcessInput[] = []
  for (let row = startRow; row <= endRow; row += 1) {
    const description = cellText(sheet, row, 1)
    const setupCost = constrainedCellNumber(sheet, row, 4, `Process row ${row} setup cost`, { min: 0 }) ?? 0
    const runCostEach = constrainedCellNumber(sheet, row, 5, `Process row ${row} run cost`, { min: 0 }) ?? 0
    const hasLinkMarker = cellBoolean(sheet, row, 3)
    if (hasLinkMarker && !description) {
      throw new Error(`Linked process row ${row} must name a subassembly part number.`)
    }
    const linked = hasLinkMarker ? childByPart.get(normalized(description)) : undefined
    if (hasLinkMarker && !linked) {
      throw new Error(`Linked process row ${row} references "${description}", but no matching subassembly sheet was imported.`)
    }
    if (!description && !setupCost && !runCostEach && !linked) continue
    processes.push({
      id: importedId(idPrefix, processes.length),
      description,
      setupCost: linked ? 0 : setupCost,
      runCostEach: linked ? 0 : runCostEach,
      ...(linked ? {
        subassemblyId: linked.id,
        quantityPerParent: constrainedCellNumber(
          sheet,
          row,
          2,
          `Linked process row ${row} quantity per parent`,
          { min: 0.000001 },
        ) ?? 1,
      } : {}),
    })
  }
  return processes.length ? processes : [...defaults]
}

function readFacilities(
  sheet: ExcelJS.Worksheet,
  quantities: readonly QuantityTier[],
) {
  const row = findRow(
    sheet,
    (value, _row, column) => column <= 5 && /^facilities$/i.test(value),
  )
  return Object.fromEntries(quantities.map((quantity, index) => [
    quantity,
    row
      ? constrainedCellNumber(
          sheet,
          row,
          QUANTITY_COLUMNS[index],
          `Facilities margin for quantity ${quantity}`,
          { min: 0 },
        ) ?? 0
      : 0,
  ]))
}

function readRateYear(sheet: ExcelJS.Worksheet, warnings: string[]) {
  const candidate = rightConstrainedNumber(
    sheet,
    /^labor rate year:?$/i,
    'Labor rate year',
    { min: 2000, max: 2100, integer: true },
  ) ?? 2026
  if (ESTIMATE_YEARS.includes(candidate as EstimateYear)) return candidate as EstimateYear
  warnings.push(`Labor rate year ${candidate} is unavailable; 2026 rates were selected.`)
  return 2026
}

function readMetadata(sheet: ExcelJS.Worksheet) {
  return {
    customer: rightText(sheet, /^customer:?$/i),
    partNumber: rightText(sheet, /^p\/n:?$/i),
    revision: rightText(sheet, /^rev:?$/i).replace(/^rev\s*/i, ''),
    nsn: rightText(sheet, /^nsn\s*#?:?$/i),
    quoteLogNumber: rightText(sheet, /^quote log no\.?\s*:?$/i),
    solicitationNumber: rightText(sheet, /^sol\s*#?:?$/i),
    rfqNumber: rightText(sheet, /^rfq\s*#?:?$/i),
    quoteDate: rightText(sheet, /^date:?$/i),
    estimator: rightText(sheet, /^estimator:?$/i),
    comments: commentsText(sheet),
  }
}

function applySharedSheet(
  estimate: EstimateInput,
  sheet: ExcelJS.Worksheet,
  sections: WorkbookSections,
  warnings: string[],
) {
  const quantities = readQuantities(sheet, sections.operationHeader)
  return {
    ...estimate,
    metadata: readMetadata(sheet),
    quantities,
    rateYear: readRateYear(sheet, warnings),
    yield: rightConstrainedNumber(
      sheet,
      /^yield\s*%:?$/i,
      'Expected yield',
      { min: 0, max: 1 },
    ) ?? estimate.yield,
    salesMarkup: rightConstrainedNumber(
      sheet,
      /^sales\/management markup$/i,
      'Sales markup',
      { min: 0, max: 10 },
    ) ?? estimate.salesMarkup,
    operations: readOperations(
      sheet,
      sections.operationHeader + 1,
      sections.materialHeader - 1,
      estimate.operations,
      `${estimate.kind}-import-operation`,
    ),
    materials: readMaterials(
      sheet,
      materialDataStart(sheet, sections.materialHeader),
      sections.processHeader - 1,
      estimate.materials,
      `${estimate.kind}-import-material`,
    ),
    facilitiesByQuantity: readFacilities(sheet, quantities),
  }
}

function sheetInputScore(sheet: ExcelJS.Worksheet) {
  let score = 0
  for (const row of [2, 3, 4, 5, 7, 8]) {
    if (cellText(sheet, row, 2)) score += 4
  }
  for (let row = 14; row <= Math.min(sheet.rowCount, 50); row += 1) {
    if ((cellNumber(sheet, row, 2) ?? 0) !== 0 || (cellNumber(sheet, row, 3) ?? 0) !== 0) score += 1
  }
  return score
}

function readChildSheet(
  sheet: ExcelJS.Worksheet,
  index: number,
  parentQuantities: readonly QuantityTier[],
) {
  const sections = findSections(sheet)
  const child = createSubassemblyDefaults(index)
  child.partNumber = rightText(sheet, /^p\/n:?$/i)
  child.revision = rightText(sheet, /^rev:?$/i).replace(/^rev\s*/i, '')
  child.quantitiesByParentQuantity = Object.fromEntries(parentQuantities.map((quantity, quantityIndex) => [
    quantity,
    constrainedCellNumber(
      sheet,
      sections.operationHeader,
      QUANTITY_COLUMNS[quantityIndex],
      `Child build quantity for parent quantity ${quantity}`,
      { min: 1, integer: true },
    ) ?? quantity,
  ]))
  child.operations = readOperations(
    sheet,
    sections.operationHeader + 1,
    sections.materialHeader - 1,
    child.operations,
    `${child.id}-import-operation`,
  )
  child.materials = readMaterials(
    sheet,
    materialDataStart(sheet, sections.materialHeader),
    sections.processHeader - 1,
    child.materials,
    `${child.id}-import-material`,
  )
  child.processes = readProcesses(
    sheet,
    processDataStart(sheet, sections.processHeader),
    sections.processEnd - 1,
    child.processes,
    `${child.id}-import-process`,
  )
  child.facilitiesByQuantity = readFacilities(sheet, parentQuantities)
  return child
}

function childHasInputs(child: SubassemblyInput, ordinal: number, linkedParts: Set<string>) {
  if (linkedParts.has(normalized(child.partNumber))) return true
  if (child.partNumber && child.partNumber !== String(ordinal)) return true
  return child.operations.some((operation) => operation.setupMinutes || operation.runMinutes || operation.notes)
    || child.materials.some((material) => material.description || material.partsQuantity || material.unitPrice)
    || child.processes.some((process) => process.description || process.setupCost || process.runCostEach)
}

async function importSubassemblyWorkbook(workbook: ExcelJS.Workbook) {
  const warnings: string[] = []
  const top = workbook.getWorksheet('Top Assy')
  if (!top) throw new Error('The workbook is missing the Top Assy sheet.')
  const sections = findSections(top)
  const base = applySharedSheet(
    createEstimateDefaults('subassembly'),
    top,
    sections,
    warnings,
  ) as SubassemblyEstimateInput
  const linkedParts = new Set<string>()
  for (let row = sections.processHeader + 1; row < sections.processEnd; row += 1) {
    if (cellBoolean(top, row, 3)) linkedParts.add(normalized(cellText(top, row, 1)))
  }
  const children = Array.from({ length: 12 }, (_, index) => {
    const sheet = workbook.getWorksheet(`Subassy ${index + 1}`)
    return sheet ? readChildSheet(sheet, index, base.quantities) : null
  }).filter((child): child is SubassemblyInput => child !== null)
    .filter((child, index) => childHasInputs(child, index + 1, linkedParts))
  base.subassemblies = children
  base.processes = readProcesses(
    top,
    processDataStart(top, sections.processHeader),
    sections.processEnd - 1,
    base.processes,
    'subassembly-parent-import-process',
    children,
  )
  return { estimate: base, sourceSheet: top.name, warnings }
}

async function importSingleSheetWorkbook(workbook: ExcelJS.Workbook) {
  const candidates = workbook.worksheets.filter((sheet) => (
    /rubber|rev\s*e|estimate/i.test(sheet.name)
  ))
  const sheet = (candidates.length ? candidates : workbook.worksheets)
    .sort((left, right) => sheetInputScore(right) - sheetInputScore(left))[0]
  if (!sheet) throw new Error('The workbook does not contain a worksheet.')
  const kind = /rubber/i.test(sheet.name) ? 'rubber' : 'standard'
  const warnings: string[] = []
  const sections = findSections(sheet)
  const estimate = applySharedSheet(createEstimateDefaults(kind), sheet, sections, warnings)
  estimate.processes = readProcesses(
    sheet,
    processDataStart(sheet, sections.processHeader),
    sections.processEnd - 1,
    estimate.processes,
    `${kind}-import-process`,
  )
  if (estimate.kind === 'rubber') {
    const difficulty = rightConstrainedNumber(
      sheet,
      /^complexity:?$/i,
      'Rubber complexity',
      { min: 1, max: 5, integer: true },
    )
    estimate.difficulty = difficulty === null ? null : difficulty as RubberDifficulty
    estimate.cavities = rightConstrainedNumber(
      sheet,
      /^#?\s*of cavities:?$/i,
      'Rubber cavity count',
      { min: 0, integer: true },
    ) ?? estimate.cavities
    estimate.toolingMarkup = rightConstrainedNumber(
      sheet,
      /^tooling markup:?$/i,
      'Rubber tooling markup',
      { min: 0, max: 10 },
    ) ?? estimate.toolingMarkup
  }
  return { estimate, sourceSheet: sheet.name, warnings }
}

export async function importEstimateWorkbook(
  source: ArrayBuffer,
): Promise<EstimateWorkbookImportResult> {
  if (!source.byteLength) throw new Error('The selected workbook is empty.')
  if (source.byteLength > MAX_WORKBOOK_BYTES) throw new Error('The workbook is larger than 25 MB.')
  const workbook = new ExcelJS.Workbook()
  try {
    await workbook.xlsx.load(source)
  } catch {
    throw new Error('The file could not be read. Save legacy .xls files as .xlsx and try again.')
  }
  const imported = workbook.getWorksheet('Top Assy')
    ? await importSubassemblyWorkbook(workbook)
    : await importSingleSheetWorkbook(workbook)
  const operationNoteCount = imported.estimate.operations.filter((operation) => operation.notes?.trim()).length
    + (imported.estimate.kind === 'subassembly'
      ? imported.estimate.subassemblies.flatMap((child) => child.operations)
        .filter((operation) => operation.notes?.trim()).length
      : 0)
  return { ...imported, operationNoteCount }
}
