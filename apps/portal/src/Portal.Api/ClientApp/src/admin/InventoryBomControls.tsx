import { Filter, Plus, Settings2 } from 'lucide-react'
import type { ApiSource, ReportColumn, ReportSheet } from './apiCustomizerModel'

export default function InventoryBomControls({ sheet, source, columns, change, customize, addFields, filters, sorted }: {
  sheet: ReportSheet; source: ApiSource; columns: ReportColumn[]; change: (sheet: ReportSheet) => void
  customize: () => void; addFields: () => void; filters: () => void
  sorted: boolean
}) {
  const set = (key: string, value: unknown) => change({ ...sheet, inputs: { ...sheet.inputs, [key]: value } })
  const unmapped = columns.filter(c => source.fields.some(f => f.path === c.path && f.availability === 'unmapped')).length
  return <section className="ac-bom-setup" aria-label="Inventory BOM setup">
    <div className="ac-bom-heading"><div><h3>Inventory BOM</h3><p>Your workbook layout, connected to Fulcrum. Choose items, then preview or customize.</p></div>
      <span className="ac-template-count">{columns.length} Columns</span></div>
    <div className="ac-bom-scope">
      <label className="ac-bom-search"><span>Which Items?</span><textarea rows={2} aria-label="Item numbers or IDs" placeholder="All matching items, or enter numbers separated by commas / new lines" value={String(sheet.inputs['report.itemSearch'] ?? '')} onChange={e => set('report.itemSearch', e.target.value)} maxLength={2000} /></label>
      <label><span>Search By</span><select value={String(sheet.inputs['report.searchBy'] ?? 'number')} onChange={e => set('report.searchBy', e.target.value)}><option value="number">Item Number / PN</option><option value="id">Fulcrum Item ID</option></select></label>
      {sheet.inputs['report.searchBy'] !== 'id' && <label><span>Match</span><select value={String(sheet.inputs['report.matchMode'] ?? 'equal')} onChange={e => set('report.matchMode', e.target.value)}><option value="equal">Exact Number</option><option value="startsWith">Starts With</option><option value="contains">Contains</option></select></label>}
      <label><span>Item Status</span><select value={sheet.inputs['body.isArchived'] == null ? 'all' : sheet.inputs['body.isArchived'] ? 'archived' : 'active'} onChange={e => set('body.isArchived', e.target.value === 'all' ? null : e.target.value === 'archived')}><option value="active">Non-Archived</option><option value="all">All</option><option value="archived">Archived</option></select></label>
      <label><span>Revision</span><select value={sheet.inputs['body.latestRevision'] === true ? 'latest' : 'all'} onChange={e => set('body.latestRevision', e.target.value === 'latest' ? true : null)}><option value="latest">Latest Revision</option><option value="all">All Revisions</option></select></label>
    </div>
    <div className="ac-bom-actions"><div className="ac-actions"><button className="solid-button" onClick={customize}><Settings2 size={15} /> Customize Columns</button><button className="ghost-button" onClick={addFields}><Plus size={15} /> Add Fulcrum Fields</button><button className="ghost-button" onClick={filters}><Filter size={15} /> More Filters</button></div>
      <label className="ac-check" title={sorted ? 'Required while sorting so every material retains its operation details.' : undefined}><input type="checkbox" disabled={sorted} checked={sorted || sheet.inputs['report.repeatOperations'] === true} onChange={e => set('report.repeatOperations', e.target.checked)} /> Repeat Operation Details On Every Row{sorted && <small>(Required For Sorting)</small>}</label></div>
    <p>{unmapped ? `${unmapped} template columns have no verified equivalent and will stay blank until mapped or removed. ` : ''}Material inputs match their routing step automatically. Unassigned inputs are retained.</p>
    <details className="ac-format-options"><summary>Report Notes And Limits</summary><p>Non-archived / latest revision are item filters, not BOM approval status. Times use hours:minutes:seconds. Qty Req is populated only for requires-basis quantities. Full inventory reports can take several minutes. Inventory BOM supports up to 10,000 starting items and uses bounded related routing reads; use item filters to split larger catalogues. Other paginated Customizer reports can read up to 50,000 records per data source. Limits: 100,000 output rows, 20,100 API calls, 30 minutes and 20 MB preview data. A failed or limited pull never produces a partial workbook. This is not a validated ERP import.</p></details>
  </section>
}
