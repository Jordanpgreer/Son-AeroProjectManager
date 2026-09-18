import { Filter, Plus, Settings2 } from 'lucide-react'
import type { ReportColumn, ReportSheet } from './apiCustomizerModel'

export default function MaterialYieldControls({ sheet, columns, maxRecords, change, changeLimit, customize, addFields, filters }: {
  sheet: ReportSheet; columns: ReportColumn[]; change: (sheet: ReportSheet) => void
  maxRecords: number; changeLimit: (value: number) => void
  customize: () => void; addFields: () => void; filters: () => void
}) {
  const set = (key: string, value: unknown) => change({ ...sheet, inputs: { ...sheet.inputs, [key]: value } })
  return <section className="ac-bom-setup" aria-label="Material produces yield setup">
    <div className="ac-bom-heading"><div><h3>Material Produces Yield</h3><p>One row per raw-material nesting, with its configured Produces quantity and verified part-number relationship.</p></div>
      <span className="ac-template-count">{columns.length} Columns</span></div>
    <div className="ac-bom-scope">
      <label className="ac-bom-search"><span>Which Produced Items?</span><textarea rows={2} aria-label="Produced item numbers or IDs" placeholder="All matching items, or enter numbers separated by commas / new lines" value={String(sheet.inputs['report.itemSearch'] ?? '')} onChange={e => set('report.itemSearch', e.target.value)} maxLength={2000} /></label>
      <label><span>Search By</span><select value={String(sheet.inputs['report.searchBy'] ?? 'number')} onChange={e => set('report.searchBy', e.target.value)}><option value="number">Item Number / P/N</option><option value="id">Fulcrum Item ID</option></select></label>
      {sheet.inputs['report.searchBy'] !== 'id' && <label><span>Match</span><select value={String(sheet.inputs['report.matchMode'] ?? 'equal')} onChange={e => set('report.matchMode', e.target.value)}><option value="equal">Exact Number</option><option value="startsWith">Starts With</option><option value="contains">Contains</option></select></label>}
      <label><span>Item Status</span><select value={sheet.inputs['body.isArchived'] == null ? 'all' : sheet.inputs['body.isArchived'] ? 'archived' : 'active'} onChange={e => set('body.isArchived', e.target.value === 'all' ? null : e.target.value === 'archived')}><option value="active">Non-Archived</option><option value="all">All</option><option value="archived">Archived</option></select></label>
      <label><span>Revision</span><select value={sheet.inputs['body.latestRevision'] === true ? 'latest' : 'all'} onChange={e => set('body.latestRevision', e.target.value === 'latest' ? true : null)}><option value="latest">Latest Revision</option><option value="all">All Revisions</option></select></label>
      <label><span>Maximum Starting Items (Arda)</span><input type="number" min={1} max={25000} value={maxRecords} onChange={e => changeLimit(Number(e.target.value))} /></label>
    </div>
    <div className="ac-bom-actions"><div className="ac-actions"><button className="solid-button" onClick={customize}><Settings2 size={15} /> Customize Columns</button><button className="ghost-button" onClick={addFields}><Plus size={15} /> Add Fulcrum Fields</button><button className="ghost-button" onClick={filters}><Filter size={15} /> More Filters</button></div></div>
    <p>From P/N is populated only when a material input item matches the raw-material ID and routing step exactly. Ambiguous or absent matches remain blank and are explained in the match-status column. Produces is a configured quantity, not a scrap percentage.</p>
  </section>
}
