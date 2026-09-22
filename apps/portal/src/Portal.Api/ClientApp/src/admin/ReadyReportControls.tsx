import { Filter, Plus, Settings2 } from 'lucide-react'
import type { ReportColumn, ReportSheet } from './apiCustomizerModel'

export default function ReadyReportControls({ kind, sheet, columns, maxRecords, change, changeLimit, customize, addFields, filters }: {
  kind: 'po-vendor-notes' | 'item-bom-yield'; sheet: ReportSheet; columns: ReportColumn[]
  maxRecords: number; change: (sheet: ReportSheet) => void; changeLimit: (value: number) => void
  customize: () => void; addFields: () => void; filters: () => void
}) {
  const set = (key: string, value: unknown) => change({ ...sheet, inputs: { ...sheet.inputs, [key]: value } })
  const po = kind === 'po-vendor-notes'
  return <section className="ac-bom-setup" aria-label={po ? 'PO vendor notes report setup' : 'Item BOM yield report setup'}>
    <div className="ac-bom-heading"><div><h3>{po ? 'PO Vendor Notes By Line Item' : 'Item BOM Yield Report'}</h3><p>{po
      ? 'One row per purchase-order part line, with its quantity, creation date, and line-level vendor note.'
      : 'One row per creates-basis BOM child, with its revision and linked routing operation.'}</p></div>
      <span className="ac-template-count">{columns.length} Columns</span></div>
    <div className="ac-bom-scope">
      {po ? <>
        <label className="ac-bom-search"><span>Which Purchase Orders?</span><textarea rows={2} aria-label="PO numbers" placeholder="All matching POs, or enter numbers separated by commas / new lines" value={String(sheet.inputs['report.poNumbers'] ?? '')} onChange={e => set('report.poNumbers', e.target.value)} maxLength={4000} /></label>
        <label><span>PO Status</span><select value={String(sheet.inputs['body.status'] ?? '')} onChange={e => set('body.status', e.target.value || null)}><option value="">All Statuses</option><option value="draft">Draft</option><option value="needsApproval">Needs Approval</option><option value="approved">Approved</option><option value="ordered">Ordered</option><option value="paid">Paid</option><option value="cancelled">Cancelled</option></select></label>
        <label><span>Maximum Purchase Orders (Arda)</span><input type="number" min={1} max={10000} value={maxRecords} onChange={e => changeLimit(Number(e.target.value))} /></label>
      </> : <>
        <label className="ac-bom-search"><span>Which Parent Items?</span><textarea rows={2} aria-label="Parent item numbers or IDs" placeholder="All matching items, or enter numbers separated by commas / new lines" value={String(sheet.inputs['report.itemSearch'] ?? '')} onChange={e => set('report.itemSearch', e.target.value)} maxLength={2000} /></label>
        <label><span>Search By</span><select value={String(sheet.inputs['report.searchBy'] ?? 'number')} onChange={e => set('report.searchBy', e.target.value)}><option value="number">Item Number / P/N</option><option value="id">Fulcrum Item ID</option></select></label>
        {sheet.inputs['report.searchBy'] !== 'id' && <label><span>Match</span><select value={String(sheet.inputs['report.matchMode'] ?? 'equal')} onChange={e => set('report.matchMode', e.target.value)}><option value="equal">Exact Number</option><option value="startsWith">Starts With</option><option value="contains">Contains</option></select></label>}
        <label><span>Item Status</span><select value={sheet.inputs['body.isArchived'] == null ? 'all' : sheet.inputs['body.isArchived'] ? 'archived' : 'active'} onChange={e => set('body.isArchived', e.target.value === 'all' ? null : e.target.value === 'archived')}><option value="active">Non-Archived</option><option value="all">All</option><option value="archived">Archived</option></select></label>
        <label><span>Revision</span><select value={sheet.inputs['body.latestRevision'] === true ? 'latest' : 'all'} onChange={e => set('body.latestRevision', e.target.value === 'latest' ? true : null)}><option value="latest">Latest Revision</option><option value="all">All Revisions</option></select></label>
        <label><span>Maximum Starting Items (Arda)</span><input type="number" min={1} max={10000} value={maxRecords} onChange={e => changeLimit(Number(e.target.value))} /></label>
      </>}
    </div>
    <div className="ac-bom-actions"><div className="ac-actions"><button className="solid-button" onClick={customize}><Settings2 size={15} /> Customize Columns</button><button className="ghost-button" onClick={addFields}><Plus size={15} /> Add Fulcrum Fields</button><button className="ghost-button" onClick={filters}><Filter size={15} /> More Filters</button></div></div>
    <p>{po
      ? 'Vendor Note is read from the individual PO part line. Lines with blank notes remain visible, and non-part PO lines are excluded because their API records do not expose a line-level vendor note.'
      : 'Only child lines whose Fulcrum quantity basis is Creates are included. Creates Quantity is the configured BOM value, not a calculated scrap percentage.'}</p>
  </section>
}
