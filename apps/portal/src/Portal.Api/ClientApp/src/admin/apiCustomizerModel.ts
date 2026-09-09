export interface ApiField { path: string; label: string; type: string; description: string; choices: string[]; availability?: string }
export interface ApiInput { key: string; label: string; type: string; description: string; required: boolean; choices: string[]; children: ApiInput[]; format: string | null }
export interface ApiSource { id: string; label: string; category: string; description: string; method: string; path: string; fields: ApiField[]; inputs: ApiInput[]; permissions: string[] }
export interface ApiCatalog { version: string; sources: ApiSource[] }
export interface FieldRef { sheetId: string; path: string }
export interface ReportColumn extends FieldRef { header: string; format: string; width: number }
export interface ReportSheet {
  id: string; name: string; sourceId: string; parentSheetId: string | null
  inputs: Record<string, unknown>; bindings: Record<string, FieldRef>; columns: ReportColumn[]
  startRow: number; startColumn: number; sortColumn: number | null; sortDescending: boolean; includeEmptyParents: boolean
}
export interface ReportDefinition {
  name: string; headerColor: string; freezeHeaders: boolean; autoFilter: boolean; wrapText: boolean
  maxRecords: number; sheets: ReportSheet[]
  outputMode?: 'combined' | 'separate'; detailSheetId?: string | null; autoSize?: boolean
  outputColumns?: ReportColumn[]; outputSortColumn?: number | null; outputSortDescending?: boolean
}
export interface SavedReport { id: string; name: string; definition: ReportDefinition; version: number; updatedBy: string; updatedAt: string }
export interface ReportRun {
  id: string; name: string; sample: boolean; createdAt: string; expiresAt: string; requestCount: number; warnings: string[]
  sheets: { id: string; name: string; columns: ReportColumn[]; rows: unknown[][] }[]
}
export function emptySheet(id: string = crypto.randomUUID()): ReportSheet {
  return { id, name: 'Worksheet', sourceId: '', parentSheetId: null, inputs: {}, bindings: {}, columns: [], startRow: 1, startColumn: 1, sortColumn: null, sortDescending: false, includeEmptyParents: true }
}
export function defaultColumns(source: ApiSource, sheetId: string): ReportColumn[] {
  const common = ['number', 'name', 'description', 'status', 'isArchived', 'id']
  const fields = common.map(path => source.fields.find(f => f.path === path)).filter((x): x is ApiField => !!x)
  return (fields.length ? fields.slice(0, 5) : source.fields.slice(0, 5)).map(f => columnFor(f, sheetId))
}
export function columnFor(field: ApiField, sheetId: string): ReportColumn {
  return { sheetId, path: field.path, header: field.label, width: 24,
    format: ['number', 'integer'].includes(field.type) ? 'number' : field.type === 'boolean' ? 'boolean' : field.type === 'duration' ? 'duration' : 'text' }
}
export function blankReport(): ReportDefinition {
  return { name: 'New Report', headerColor: 'C65D21', freezeHeaders: true, autoFilter: true, wrapText: true,
    maxRecords: 1000, sheets: [], outputMode: 'combined', detailSheetId: null, autoSize: true, outputColumns: [] }
}
export function sourceName(source: ApiSource): string {
  if (source.id === inventoryBomSourceId) return 'Inventory BOM'
  const path = source.path.replace(/\/list(?:\/v\d+)?$/, '')
  const tail = path.split(/\{[^}]+\}\//).at(-1)!.replace(/^\/api\//, '').replace(/\/\{[^}]+\}$/, '')
  const labels: Record<string, string> = { items: 'Items', 'routing/operations': 'Routing Steps', 'routing/input-items': 'BOM Components', 'routing/input-materials': 'BOM Materials', routing: 'Routing Settings', 'input-items': 'BOM Components', 'input-materials': 'BOM Materials' }
  return labels[tail] ?? tail.split('/').map(part => part.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase())).join(' / ')
}
export const inventoryBomSourceId = 'ARDA inventory-bom'
export function inventoryBomStarter(catalogue: ApiCatalog): ReportDefinition {
  const source = catalogue.sources.find(s => s.id === inventoryBomSourceId)
  if (!source) throw new Error('The Inventory BOM report is unavailable. Refresh the page after updating the server.')
  const sheet = { ...emptySheet('inventory-bom'), name: 'BOM', sourceId: source.id,
    inputs: { ...defaultInputs(source), 'report.repeatOperations': false, 'report.itemSearch': '', 'report.searchBy': 'number', 'report.matchMode': 'equal' } }
  const columns = source.fields.slice(0, 32).map(f => columnFor(f, sheet.id))
  return withColumns({ ...blankReport(), name: 'Inventory BOM', maxRecords: 5000, sheets: [sheet], detailSheetId: sheet.id }, columns)
}
export function defaultInputs(source: ApiSource): Record<string, unknown> {
  const values: Record<string, unknown> = {}
  if (source.inputs.some(i => i.key === 'body.isArchived')) values['body.isArchived'] = false
  if (source.inputs.some(i => i.key === 'body.latestRevision')) values['body.latestRevision'] = true
  return values
}
export function reportColumns(definition: ReportDefinition): ReportColumn[] {
  return definition.outputColumns?.length ? definition.outputColumns : definition.sheets.flatMap(s => s.columns)
    .filter((c, i, all) => all.findIndex(other => other.sheetId === c.sheetId && other.path === c.path) === i)
}
export function withColumns(definition: ReportDefinition, columns: ReportColumn[]): ReportDefinition {
  const sort = reportColumns(definition)[definition.outputSortColumn ?? -1]
  const nextSort = sort ? columns.findIndex(c => c.sheetId === sort.sheetId && c.path === sort.path) : -1
  return { ...definition, outputColumns: columns, outputSortColumn: nextSort >= 0 ? nextSort : null,
    sheets: definition.sheets.map(s => ({ ...s, columns: columns.filter(c => c.sheetId === s.id), sortColumn: null })) }
}
export function loadLayout(definition: ReportDefinition): ReportDefinition {
  return withColumns({ ...structuredClone(definition), autoSize: true, wrapText: true, outputMode: definition.outputMode ?? 'separate' }, reportColumns(definition))
}
export function relatedSources(catalogue: ApiCatalog, definition: ReportDefinition, parent: ReportSheet) {
  const scope = [...ancestors(definition, parent), parent]
  return catalogue.sources.flatMap(source => {
    if (source.id === parent.sourceId || /\/\{[^}]+\}$/.test(source.path)) return []
    const bindings = suggestedBindings(catalogue, source, scope)
    const required = source.inputs.filter(i => i.required && i.key.startsWith('path.'))
    if (!required.length || required.some(i => !bindings[i.key]) || !Object.values(bindings).some(b => b.sheetId === parent.id)) return []
    return [{ source, bindings }]
  }).sort((a, b) => sourceName(a.source).localeCompare(sourceName(b.source)))
}
export function addSource(definition: ReportDefinition, catalogue: ApiCatalog, source: ApiSource, parent?: ReportSheet): ReportDefinition {
  const id = crypto.randomUUID()
  const base = sourceName(source).replace(/[\\/?:*[\]]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 27)
  let name = base
  for (let i = 2; definition.sheets.some(s => s.name.toLowerCase() === name.toLowerCase()); i++) name = `${base} ${i}`
  const sheet = { ...emptySheet(id), name, sourceId: source.id, inputs: defaultInputs(source),
    parentSheetId: parent?.id ?? null, bindings: parent ? suggestedBindings(catalogue, source, [...ancestors(definition, parent), parent]) : {} }
  sheet.columns = defaultColumns(source, id).map(c => ({ ...c, header: fieldHeading(c.path, c.header, name) }))
  const next = { ...definition, name: definition.name === 'New Report' ? `${name} Report` : definition.name,
    detailSheetId: id, sheets: [...definition.sheets, sheet] }
  return withColumns(next, [...reportColumns(definition), ...sheet.columns])
}
export function fieldHeading(path: string, label: string, source: string): string {
  return ['id', 'name', 'number'].includes(path) ? `${source.replace(/s$/, '')} ${path === 'id' ? 'ID' : path === 'name' ? 'Name' : 'Number'}` : label
}
export function removeSource(definition: ReportDefinition, id: string): ReportDefinition {
  const removed = new Set([id])
  for (const sheet of definition.sheets) if (sheet.parentSheetId && removed.has(sheet.parentSheetId)) removed.add(sheet.id)
  return withColumns({ ...definition, sheets: definition.sheets.filter(s => !removed.has(s.id)),
    detailSheetId: removed.has(definition.detailSheetId ?? '') ? definition.sheets[0]?.id : definition.detailSheetId }, reportColumns(definition).filter(c => !removed.has(c.sheetId)))
}
export function partsStarter(catalogue: ApiCatalog): ReportDefinition {
  const paths = ['/api/items/list/v2', '/api/items/{itemId}/routing/operations/list', '/api/items/{itemId}/routing/input-materials/list']
  const ids = ['parts', 'routing', 'materials']
  const names = ['Parts', 'Routing Steps', 'Materials']
  const columns = [['number', 'revision.revision', 'description', 'isArchived'], ['order', 'name', 'instructions', 'isOutsideProcessing'], ['materialName', 'costing', 'routingStepId', 'nestings']]
  const sheets = paths.map((path, i) => {
    const source = catalogue.sources.find(s => s.path === path)
    const sheet = { ...emptySheet(ids[i]), name: names[i], sourceId: source?.id ?? '' }
    if (source) sheet.columns = columns[i].map(p => source.fields.find(f => f.path === p)).filter((x): x is ApiField => !!x).map(f => columnFor(f, ids[i]))
    if (i === 0) sheet.inputs = { 'body.isArchived': false, 'body.latestRevision': true }
    else {
      sheet.parentSheetId = 'parts'
      sheet.bindings = { 'path.itemId': { sheetId: 'parts', path: 'id' } }
      sheet.columns.unshift({ sheetId: 'parts', path: 'number', header: 'Part Number', format: 'text', width: 24 })
    }
    return sheet
  })
  return { name: 'Active Parts, Routing And Materials', headerColor: 'C65D21', freezeHeaders: true, autoFilter: true, wrapText: false, maxRecords: 100, sheets }
}
export function ancestors(definition: ReportDefinition, sheet: ReportSheet): ReportSheet[] {
  const result: ReportSheet[] = []
  let parent = sheet.parentSheetId
  while (parent && !result.some(s => s.id === parent)) {
    const found = definition.sheets.find(s => s.id === parent)
    if (!found) break
    result.unshift(found)
    parent = found.parentSheetId
  }
  return result
}
export function suggestedBindings(catalogue: ApiCatalog, source: ApiSource, parents: ReportSheet[]): Record<string, FieldRef> {
  const bindings: Record<string, FieldRef> = {}
  for (const input of source.inputs.filter(input => input.key.startsWith('path.'))) {
    const key = input.key.slice(5)
    const marker = source.path.indexOf(`/{${key}}`)
    const collection = marker < 0 ? '' : source.path.slice(0, marker)
    for (const parent of [...parents].reverse()) {
      const parentSource = catalogue.sources.find(candidate => candidate.id === parent.sourceId)
      if (!parentSource) continue
      // Only infer an ID when the ancestor represents this exact API resource.
      const parentCollection = parentSource.path.replace(/\/list(?:\/v\d+)?$/, '').replace(/\/\{[^}]+\}$/, '')
      const field = parentSource.fields.find(field => key !== 'id' && field.path === key)
        ?? (collection === parentCollection ? parentSource.fields.find(field => field.path === 'id') : undefined)
      if (field) { bindings[input.key] = { sheetId: parent.id, path: field.path }; break }
    }
  }
  return bindings
}
export function moveColumn(sheet: ReportSheet, from: number, to: number): ReportSheet {
  if (from < 0 || to < 0 || from >= sheet.columns.length || to >= sheet.columns.length) return sheet
  const columns = [...sheet.columns]
  const sort = sheet.sortColumn === null ? null : columns[sheet.sortColumn]
  columns.splice(to, 0, columns.splice(from, 1)[0])
  return { ...sheet, columns, sortColumn: sort ? columns.indexOf(sort) : null }
}
export function displayCell(value: unknown, format: string): string {
  if (value === null || value === undefined) return ''
  if (format === 'currency' && typeof value === 'number') return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value)
  if (format === 'number' && typeof value === 'number') return new Intl.NumberFormat('en-US', { maximumFractionDigits: 8 }).format(value)
  if (format === 'duration' && typeof value === 'number') {
    const seconds = Math.round(value * 86400)
    return `${Math.floor(seconds / 3600)}:${String(Math.floor(seconds % 3600 / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
  }
  if (format === 'date' && typeof value === 'string' && !Number.isNaN(Date.parse(value))) return value.slice(0, 10)
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE'
  return typeof value === 'object' ? JSON.stringify(value) : String(value)
}
