import { describe, expect, it } from 'vitest'
import { addSource, ancestors, blankReport, displayCell, emptySheet, inventoryBomSourceId, inventoryBomStarter, itemBomYieldSourceId, itemBomYieldStarter, loadLayout, materialYieldSourceId, materialYieldStarter, moveColumn, partsStarter, purchaseOrderVendorNotesSourceId, purchaseOrderVendorNotesStarter, relatedSources, removeSource, reportColumns, sourceName, suggestedBindings, withColumns } from '../src/admin/apiCustomizerModel'
import type { ApiCatalog, ApiSource } from '../src/admin/apiCustomizerModel'

const source = (path: string, paths: string[]): ApiSource => ({ id: `POST ${path}`, path, label: path, category: 'Test', description: '', method: 'POST', inputs: [], permissions: [], fields: paths.map(path => ({ path, label: path, type: 'string', description: '', choices: [] })) })
const catalogue: ApiCatalog = { version: 'Test', sources: [
  source('/api/items/list/v2', ['id', 'number', 'revision.revision', 'description', 'isArchived']),
  source('/api/items/{itemId}/routing/operations/list', ['id', 'order', 'name', 'instructions', 'isOutsideProcessing']),
  source('/api/items/{itemId}/routing/input-materials/list', ['id', 'materialName', 'costing', 'routingStepId', 'nestings']),
] }

describe('API customizer workbook model', () => {
  it('starts with active parts and exact ID links, retaining part numbers on child sheets', () => {
    const report = partsStarter(catalogue)
    expect(report.sheets[0].inputs).toEqual({ 'body.isArchived': false, 'body.latestRevision': true })
    expect(report.sheets.slice(1).map(s => s.bindings['path.itemId'])).toEqual([{ sheetId: 'parts', path: 'id' }, { sheetId: 'parts', path: 'id' }])
    expect(report.sheets[1].columns[0]).toMatchObject({ sheetId: 'parts', path: 'number', format: 'text' })
  })
  it('keeps the row sort attached to its column after a drag reorder', () => {
    const sheet = { ...partsStarter(catalogue).sheets[0], sortColumn: 0 }
    const moved = moveColumn(sheet, 0, 2)
    expect(moved.sortColumn).toBe(2)
    expect(moved.columns[2].path).toBe('number')
    expect(sheet.columns[0].path).toBe('number')
  })
  it('offers ancestor data to a nested worksheet without unrelated sibling data', () => {
    const report = partsStarter(catalogue)
    report.sheets.push({ ...emptySheet('fourth'), parentSheetId: 'routing' })
    expect(ancestors(report, report.sheets[3]).map(s => s.id)).toEqual(['parts', 'routing'])
  })
  it('preserves identifier text and uses the selected financial/date display', () => {
    expect(displayCell('000123', 'text')).toBe('000123')
    expect(displayCell(12.5, 'currency')).toBe('$12.50')
    expect(displayCell('2026-09-09T00:00:00Z', 'date')).toBe('2026-09-09')
    expect(displayCell(null, 'text')).toBe('')
  })
  it('links nested resource IDs to the correct ancestors, never an unrelated ID', () => {
    const report = partsStarter(catalogue)
    const nested = source('/api/items/{itemId}/routing/operations/{operationId}/materials/list', ['id'])
    nested.inputs = ['itemId', 'operationId', 'unrelatedId'].map(key => ({ key: `path.${key}`, label: key, type: 'string', description: '', required: true, choices: [], children: [], format: null }))
    expect(suggestedBindings(catalogue, nested, report.sheets.slice(0, 2))).toEqual({
      'path.itemId': { sheetId: 'parts', path: 'id' },
      'path.operationId': { sheetId: 'routing', path: 'id' },
    })
  })
  it('starts blank and adds any record type without example worksheets', () => {
    const blank = blankReport()
    expect(blank.sheets).toEqual([])
    const report = addSource(blank, catalogue, catalogue.sources[0])
    expect(report.sheets).toHaveLength(1)
    expect(report.outputMode).toBe('combined')
    expect(report.autoSize).toBe(true)
    expect(sourceName(catalogue.sources[0])).toBe('Items')
    expect(sourceName(catalogue.sources[1])).toBe('Routing Steps')
  })
  it('does not link unrelated endpoints just because both records have an id', () => {
    const generic = source('/api/tools/{id}', ['id'])
    generic.inputs = [{ key: 'path.id', label: 'ID', type: 'string', description: '', required: true, choices: [], children: [], format: null }]
    expect(suggestedBindings(catalogue, generic, partsStarter(catalogue).sheets.slice(0, 1))).toEqual({})
  })
  it('suggests complete relationships and maintains output columns when related sources are removed', () => {
    const routing = { ...catalogue.sources[1], inputs: [{ key: 'path.itemId', label: 'Item ID', type: 'string', description: '', required: true, choices: [], children: [], format: null }] }
    const catalog = { ...catalogue, sources: [catalogue.sources[0], routing] }
    const report = addSource(blankReport(), catalog, catalog.sources[0])
    expect(relatedSources(catalog, report, report.sheets[0]).map(r => r.source.id)).toEqual([routing.id])
    const linked = addSource(report, catalog, routing, report.sheets[0])
    expect(linked.sheets[1].bindings['path.itemId']).toEqual({ sheetId: linked.sheets[0].id, path: 'id' })
    expect(linked.detailSheetId).toBe(linked.sheets[1].id)
    const removed = removeSource(linked, linked.sheets[1].id)
    expect(reportColumns(removed).every(c => c.sheetId === removed.sheets[0].id)).toBe(true)
    expect(removed.detailSheetId).toBe(removed.sheets[0].id)
  })
  it('loads existing layouts without duplicating ancestor columns and preserves the selected sort field', () => {
    const old = loadLayout({ ...partsStarter(catalogue), outputColumns: [] })
    expect(old.outputMode).toBe('separate')
    expect(reportColumns(old).filter(c => c.sheetId === 'parts' && c.path === 'number')).toHaveLength(1)
    const columns = reportColumns(old)
    const changed = withColumns({ ...old, outputSortColumn: 0 }, [...columns.slice(1), columns[0]])
    expect(changed.outputSortColumn).toBe(columns.length - 1)
    expect(changed.sheets.every(s => s.columns.every(c => c.sheetId === s.id))).toBe(true)
  })
  it('creates Excel-safe names for reporting sources with compound labels', () => {
    const record = source('/api/reporting/shipping/list', ['id', 'number'])
    const report = addSource(blankReport(), { version: 'test', sources: [record] }, record)
    expect(report.sheets[0].name).toBe('Reporting Shipping')
    expect(reportColumns(report)[0].header).toBe('Reporting Shipping Number')
  })
  it('starts Inventory BOM with the 32 template columns and automatically connected report source', () => {
    const bom = { ...source('/arda/reports/inventory-bom', Array.from({ length: 34 }, (_, i) => `field${i}`)), id: inventoryBomSourceId }
    bom.fields[12] = { ...bom.fields[12], label: 'Setup Time', type: 'duration' }
    bom.inputs = ['body.isArchived', 'body.latestRevision'].map(key => ({ key, type: 'boolean', label: key, description: '', required: false, choices: [], children: [], format: null }))
    const report = inventoryBomStarter({ ...catalogue, sources: [...catalogue.sources, bom] })
    expect(reportColumns(report)).toHaveLength(32)
    expect(report.sheets).toHaveLength(1)
    expect(report.sheets[0].sourceId).toBe(inventoryBomSourceId)
    expect(report.sheets[0].inputs).toMatchObject({ 'body.isArchived': false, 'body.latestRevision': true, 'report.repeatOperations': false })
    expect(reportColumns(report)[12].format).toBe('duration')
    expect(report.maxRecords).toBe(5000)
    expect(sourceName(bom)).toBe('Inventory BOM')
    const renamed = withColumns(report, reportColumns(report).map((c, i) => i === 0 ? { ...c, header: 'My BOM', path: 'item.customFields.BOM' } : c))
    expect(loadLayout(renamed).outputColumns![0]).toMatchObject({ header: 'My BOM', path: 'item.customFields.BOM' })
  })
  it('starts material yield as a ready report with configured Produces and item filters', () => {
    const yieldSource = { ...source('/arda/reports/material-yield', ['fromPartNumber', 'toPartNumber', 'produces', 'producesUom']), id: materialYieldSourceId }
    yieldSource.fields[2] = { ...yieldSource.fields[2], type: 'integer' }
    yieldSource.inputs = ['body.isArchived', 'body.latestRevision'].map(key => ({ key, type: 'boolean', label: key, description: '', required: false, choices: [], children: [], format: null }))
    const report = materialYieldStarter({ ...catalogue, sources: [...catalogue.sources, yieldSource] })
    expect(report.sheets[0].sourceId).toBe(materialYieldSourceId)
    expect(report.sheets[0].inputs).toMatchObject({ 'body.isArchived': false, 'body.latestRevision': true, 'report.itemSearch': '' })
    expect(reportColumns(report).map(c => c.path)).toEqual(['fromPartNumber', 'toPartNumber', 'produces', 'producesUom'])
    expect(reportColumns(report)[2].format).toBe('number')
    expect(sourceName(yieldSource)).toBe('Material Produces Yield')
    expect(report.maxRecords).toBe(25000)
  })
  it('starts PO vendor notes with the five requested columns and date formatting', () => {
    const poNotes = { ...source('/arda/reports/purchase-order-vendor-notes', ['purchaseOrderNumber', 'lineItemNumber', 'quantity', 'createdDate', 'vendorNote']), id: purchaseOrderVendorNotesSourceId }
    poNotes.fields[1] = { ...poNotes.fields[1], type: 'integer' }
    poNotes.fields[2] = { ...poNotes.fields[2], type: 'number' }
    const report = purchaseOrderVendorNotesStarter({ ...catalogue, sources: [...catalogue.sources, poNotes] })
    expect(report.sheets[0].sourceId).toBe(purchaseOrderVendorNotesSourceId)
    expect(reportColumns(report).map(c => c.header)).toEqual(['purchaseOrderNumber', 'lineItemNumber', 'quantity', 'createdDate', 'vendorNote'])
    expect(reportColumns(report)[3].format).toBe('date')
    expect(report.sheets[0].inputs['report.poNumbers']).toBe('')
    expect(report.maxRecords).toBe(10000)
    expect(sourceName(poNotes)).toBe('PO Vendor Notes By Line Item')
  })
  it('starts Item BOM Yield with the six requested creates-basis columns', () => {
    const itemYield = { ...source('/arda/reports/item-bom-yield', ['parentItemNumber', 'childNumber', 'revision', 'operationName', 'operationNumber', 'createsQuantity']), id: itemBomYieldSourceId }
    itemYield.fields[4] = { ...itemYield.fields[4], type: 'integer' }
    itemYield.fields[5] = { ...itemYield.fields[5], type: 'number' }
    itemYield.inputs = ['body.isArchived', 'body.latestRevision'].map(key => ({ key, type: 'boolean', label: key, description: '', required: false, choices: [], children: [], format: null }))
    const report = itemBomYieldStarter({ ...catalogue, sources: [...catalogue.sources, itemYield] })
    expect(report.sheets[0].sourceId).toBe(itemBomYieldSourceId)
    expect(reportColumns(report).map(c => c.path)).toEqual(['parentItemNumber', 'childNumber', 'revision', 'operationName', 'operationNumber', 'createsQuantity'])
    expect(reportColumns(report).slice(4).map(c => c.format)).toEqual(['number', 'number'])
    expect(report.sheets[0].inputs).toMatchObject({ 'body.isArchived': false, 'body.latestRevision': true, 'report.itemSearch': '' })
    expect(report.maxRecords).toBe(10000)
    expect(sourceName(itemYield)).toBe('Item BOM Yield Report')
  })
  it('formats duration values as elapsed hours without wrapping at midnight', () => {
    expect(displayCell(15 / 1440, 'duration')).toBe('0:15:00')
    expect(displayCell(27 / 24, 'duration')).toBe('27:00:00')
    expect(displayCell(0, 'duration')).toBe('0:00:00')
    expect(displayCell(null, 'duration')).toBe('')
  })
})
