import ExcelJS from 'exceljs'
import { subassemblyBuildQuantity } from './calculations.ts'
import { getAnnualRateAssumptions, lookupLaborRate } from './estimatingRates.ts'
import type { EstimateCalculationSuccess, SubassemblyEstimateInput, SubassemblyInput } from './types.ts'

const columns = ['F', 'G', 'H', 'I', 'J', 'K', 'L', 'M']
const quoted = (name: string) => `'${name.replaceAll("'", "''")}'`

/** Editable detail sheets retain every imported row; summary sheets remain formula-driven. */
export function addWorkbookFormulas(workbook: ExcelJS.Workbook, estimate: SubassemblyEstimateInput, result: EstimateCalculationSuccess) {
  const entries = [
    { name: 'Top Assy', input: estimate, child: undefined as SubassemblyInput | undefined },
    ...estimate.subassemblies.map((child, index) => ({ name: `Subassy ${index + 1}`, input: child, child })),
  ]
  const sheetFor = (id: string) => entries.find((entry) => entry.child?.id === id)
  const layouts = entries.map((entry) => {
    const detail = workbook.addWorksheet(`${entry.name} Detail`)
    const operationsStart = 15
    const materialsStart = operationsStart + entry.input.operations.length + 3
    const processesStart = materialsStart + entry.input.materials.length + 3
    return { ...entry, detail, operationsStart, materialsStart, processesStart }
  })
  const formula = (sheet: ExcelJS.Worksheet, address: string, expression: string, cached?: number | string) => {
    const current = sheet.getCell(address).value
    sheet.getCell(address).value = { formula: expression, result: cached ?? (typeof current === 'number' || typeof current === 'string' || typeof current === 'boolean' ? current : 0) }
  }
  for (const entry of layouts) {
    const { name, child, input, detail, operationsStart, materialsStart, processesStart } = entry
    const summary = workbook.getWorksheet(name)!
    const detailRef = quoted(detail.name)
    const audit = child ? result.subassemblies.find((row) => row.subassemblyId === child.id)! : result
    detail.getCell('A1').value = `${child?.partNumber ?? estimate.metadata.partNumber} — editable cost inputs`
    detail.getCell('A2').value = 'Edit times, rates, materials, process costs and component quantities here. Change quote quantities on Top Assy.'
    detail.getCell('A3').value = child?.comments ?? estimate.metadata.comments
    detail.mergeCells('A1:O1')
    detail.mergeCells('A2:O2')
    detail.mergeCells('A3:O3')
    detail.getRow(1).height = 28
    detail.getRow(2).height = 32
    detail.getRow(3).height = 28
    detail.getCell('A1').font = { name: 'Calibri', size: 16, bold: true, color: { argb: 'FF194B78' } }
    detail.getCell('A2').alignment = { wrapText: true, vertical: 'middle' }
    detail.getCell('A3').alignment = { wrapText: true, vertical: 'middle' }
    detail.getCell('P1').value = 'Arda cost details v1'
    detail.getCell('P2').value = input.operations.length
    detail.getCell('P3').value = input.materials.length
    detail.getCell('P4').value = input.processes.length
    detail.getCell('P5').value = child?.deriveQuantitiesFromParent ?? false
    detail.getCell('P6').value = child?.quantityPerParent ?? 1
    detail.getColumn('P').hidden = true
    detail.getColumn('A').width = 44
    detail.getColumn('O').width = 56
    for (const column of ['B', 'C', 'D', 'E', ...columns, 'N']) detail.getColumn(column).width = 15
    detail.views = [{ state: 'frozen', xSplit: 1, ySplit: 14 }]
    detail.getCell('A14').value = 'Operation'
    detail.getCell('B14').value = 'Setup minutes'
    detail.getCell('C14').value = 'Run minutes / each'
    detail.getCell('D14').value = 'Cost treatment'
    detail.getCell('E14').value = 'Rate / minute'
    detail.getCell('N14').value = 'One-time NRE'
    detail.getCell('O14').value = 'Notes'
    const incoming = child ? layouts.flatMap((parent) => parent.input.processes.map((process, index) => ({ parent, process, index })))
      .filter(({ process }) => process.subassemblyId === child.id) : []
    columns.forEach((column, index) => {
      const quantity = estimate.quantities[index]
      if (quantity === undefined) return
      if (child?.deriveQuantitiesFromParent && child.quantityPerParent !== undefined) {
        const expression = incoming.length
          ? incoming.map(({ parent, index: processIndex }) => `${quoted(parent.name)}!${column}13*${quoted(parent.detail.name)}!$B$${parent.processesStart + processIndex}`).join('+')
          : `'Top Assy'!${column}13*${child.quantityPerParent}`
        formula(summary, `${column}13`, expression, subassemblyBuildQuantity(child, quantity))
      }
      formula(summary, `${column}32`, `"Qty: "&${column}13`, `Qty: ${subassemblyBuildQuantity(child, quantity)}`)
      formula(detail, `${column}13`, `${quoted(name)}!${column}13`, subassemblyBuildQuantity(child, quantity))
    })
    input.operations.forEach((operation, index) => {
      const row = operationsStart + index
      detail.getCell(`A${row}`).value = operation.name
      detail.getCell(`B${row}`).value = operation.setupMinutes
      detail.getCell(`C${row}`).value = operation.runMinutes
      detail.getCell(`D${row}`).value = operation.costTreatment
      detail.getCell(`E${row}`).value = lookupLaborRate(operation.name, estimate.rateYear) ?? 0
      detail.getCell(`O${row}`).value = operation.notes ?? ''
      formula(detail, `N${row}`, `IF(D${row}="nre",(B${row}+C${row})*E${row},0)`, audit.operations[index]?.oneTimeNre ?? 0)
      columns.forEach((column, tier) => {
        const quantity = estimate.quantities[tier]
        if (quantity === undefined) return
        formula(detail, `${column}${row}`, `IF($D${row}="production",($B${row}/MAX(1,${column}$13)+$C${row})*$E${row},0)`, audit.operations[index]?.unitCostByQuantity[quantity] ?? 0)
      })
      if (index < 12) for (const column of ['A', 'B', 'C', 'E', ...columns, 'N', 'O']) formula(summary, `${column}${14 + index}`, `${detailRef}!${column}${row}`)
    })
    detail.getCell(`A${materialsStart - 1}`).value = 'Material / purchased component'
    detail.getCell(`B${materialsStart - 1}`).value = 'Unit of measure'
    detail.getCell(`C${materialsStart - 1}`).value = 'Quantity / each'
    detail.getCell(`D${materialsStart - 1}`).value = 'Unit price'
    detail.getCell(`E${materialsStart - 1}`).value = 'Extended cost'
    detail.getCell(`N${materialsStart - 1}`).value = 'Amortize min buy'
    input.materials.forEach((material, index) => {
      const row = materialsStart + index
      detail.getCell(`A${row}`).value = material.description
      detail.getCell(`B${row}`).value = material.unitOfMeasure
      detail.getCell(`C${row}`).value = material.partsQuantity
      detail.getCell(`D${row}`).value = material.unitPrice
      detail.getCell(`N${row}`).value = material.amortizeMinBuy
      detail.getCell(`O${row}`).value = material.notes ?? ''
      formula(detail, `E${row}`, `C${row}*D${row}`, audit.materials[index]?.extendedCost ?? 0)
      columns.forEach((column, tier) => {
        const quantity = estimate.quantities[tier]
        if (quantity === undefined) return
        formula(detail, `${column}${row}`, `IF($N${row},$E${row}/MAX(1,${column}$13),$E${row}*(1+1/MAX(1,${column}$13)))`, audit.materials[index]?.unitCostByQuantity[quantity] ?? 0)
      })
      if (index < 12) for (const column of ['A', 'B', 'C', 'D', 'E', ...columns, 'N', 'O']) formula(summary, `${column}${33 + index}`, `${detailRef}!${column}${row}`)
    })
    detail.getCell(`A${processesStart - 1}`).value = 'Process / Make subassembly'
    detail.getCell(`B${processesStart - 1}`).value = 'Qty per parent'
    detail.getCell(`D${processesStart - 1}`).value = 'Setup cost'
    detail.getCell(`E${processesStart - 1}`).value = 'Cost / each'
    input.processes.forEach((process, index) => {
      const row = processesStart + index
      const linked = process.subassemblyId ? sheetFor(process.subassemblyId) : undefined
      detail.getCell(`A${row}`).value = process.description
      detail.getCell(`B${row}`).value = linked ? process.quantityPerParent ?? 1 : null
      detail.getCell(`C${row}`).value = Boolean(linked)
      detail.getCell(`D${row}`).value = process.setupCost
      detail.getCell(`E${row}`).value = process.runCostEach
      columns.forEach((column, tier) => {
        const quantity = estimate.quantities[tier]
        if (quantity === undefined) return
        formula(detail, `${column}${row}`, linked
          ? `${quoted(linked.name)}!${column}63*$B${row}`
          : `$D${row}/MAX(1,${column}$13)+$E${row}`, audit.processes[index]?.unitCostByQuantity[quantity] ?? 0)
      })
      if (index < (child ? 5 : 12)) for (const column of ['A', 'B', 'C', 'D', 'E', ...columns]) formula(summary, `${column}${46 + index}`, `${detailRef}!${column}${row}`)
    })
    const sum = (column: string, start: number, count: number) => count ? `SUM(${detailRef}!${column}${start}:${column}${start + count - 1})` : '0'
    formula(summary, 'N30', sum('N', operationsStart, input.operations.length), child ? audit.rawOneTimeNre ?? 0 : result.rawOneTimeNre)
    columns.forEach((column, index) => {
      const quantity = estimate.quantities[index]
      if (quantity === undefined) return
      const f = (row: number, expression: string) => formula(summary, `${column}${row}`, expression)
      f(26, sum(column, operationsStart, input.operations.length))
      f(28, `${column}26*$D$28`)
      f(30, `${column}26+${column}28`)
      if (child) {
        f(51, sum(column, materialsStart, input.materials.length))
        f(52, sum(column, processesStart, input.processes.length))
        f(53, `${column}30+${column}51+${column}52`)
        f(55, `${column}63`)
        f(57, `${column}30`)
        f(58, `${column}51`)
        f(59, `${column}52`)
        f(60, `${column}53`)
        f(61, `$N$30/MAX(1,${column}$13)`)
        const legacy = child.perQuantityMarginByQuantity[quantity] === undefined ? child.facilitiesByQuantity?.[quantity] ?? 0 : 0
        f(63, `(${column}60+${column}61)*(1+${column}62)+${legacy}`)
      } else {
        f(58, sum(column, materialsStart, input.materials.length))
        f(59, sum(column, processesStart, input.processes.length))
        f(60, `${column}30+${column}58+${column}59`)
        f(62, `${column}30*$E$62`)
        f(63, `${column}58*$E$63`)
        f(64, `${column}59*$E$64`)
        f(65, `SUM(${column}60,${column}62:${column}64)`)
        f(66, `(${column}30+${column}62)*$E$66`)
        f(67, `(${column}58+${column}63)*$E$67`)
        f(68, `(${column}59+${column}64)*$E$68`)
        f(69, `SUM(${column}65:${column}68)`)
        f(70, `${column}69`)
        f(71, `IFERROR((${column}69-${column}60)/${column}69,0)`)
        f(72, `SUM(${column}30,${column}62,${column}66)`)
        f(73, `SUM(${column}58,${column}63,${column}67)`)
        f(74, `SUM(${column}59,${column}64,${column}68)`)
        f(75, `SUM(${column}72:${column}74)`)
        f(76, `$N$30*(1+$E$62)*(1+$E$66)/MAX(1,${column}$13)`)
        f(77, `${column}60*(1-$G$3)`)
        f(79, `${column}75*$E$79`)
        const legacy = estimate.perQuantityMarginByQuantity[quantity] === undefined ? estimate.facilitiesByQuantity?.[quantity] ?? 0 : 0
        f(80, `(SUM(${column}75:${column}77)+${column}79)*(1+${column}78)+${legacy}`)
        f(81, `IFERROR((${column}80-${column}60-${column}76-${column}77)/(${column}80-${column}76),0)`)
        f(82, `IFERROR(${column}58/${column}80,0)`)
        estimate.subassemblies.forEach((_, childIndex) => formula(summary, `${String.fromCharCode(66 + index)}${92 + childIndex}`, `'Subassy ${childIndex + 1}'!${column}63`))
      }
    })
    summary.getCell('A10').value = `All imported cost rows are editable on ${detail.name}.`
    summary.getCell('D28').value = getAnnualRateAssumptions(estimate.rateYear).burden
    for (const rowNumber of [14, materialsStart - 1, processesStart - 1]) {
      const row = detail.getRow(rowNumber)
      row.height = 34
      for (let column = 1; column <= 15; column += 1) {
        const cell = row.getCell(column)
        cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF194B78' } }
        cell.font = { name: 'Calibri', size: 10, bold: true, color: { argb: 'FFFFFFFF' } }
        cell.alignment = { wrapText: true, vertical: 'middle' }
      }
    }
    for (const [start, count] of [[operationsStart, input.operations.length], [materialsStart, input.materials.length], [processesStart, input.processes.length]]) {
      for (let row = start; row < start + count; row += 1) {
        detail.getRow(row).height = 30
        detail.getCell(`A${row}`).alignment = { wrapText: true, vertical: 'middle' }
        detail.getCell(`O${row}`).alignment = { wrapText: true, vertical: 'middle' }
        for (let column = 1; column <= 15; column += 1) {
          const cell = detail.getCell(row, column)
          cell.font = { name: 'Calibri', size: 10, color: { argb: cell.type === ExcelJS.ValueType.Formula ? 'FF17283B' : 'FF245E91' } }
          if (column >= 6 && column <= 13) cell.numFmt = '#,##0.00'
          if ((row - start) % 2 === 0) cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FFEAF2F8' } }
        }
      }
    }
  }
  workbook.calcProperties.fullCalcOnLoad = true
}
