import { useEffect, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, Check, ChevronDown, Download, Filter, History, Link2, Plus, Save, Search, Settings2, Table2, X } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import { addSource, blankReport, columnFor, displayCell, fieldHeading, inventoryBomSourceId, inventoryBomStarter, loadLayout, relatedSources, removeSource, reportColumns, sourceName, withColumns } from './apiCustomizerModel'
import type { ApiCatalog, ReportColumn, ReportDefinition, ReportRun, ReportSheet, SavedReport } from './apiCustomizerModel'
import { BuilderDialog, FilterEditor } from './ApiCustomizerControls'
import InventoryBomControls from './InventoryBomControls'
import './api-customizer.css'

const root = '/api/admin/api-customizer'
const headers = { 'X-Arda-Customizer': '1' }
type Activity = { id: string; actor: string; action: string; reportName: string; occurredAt: string }
type Picker = { type: 'source' | 'fields' | 'filters' | 'columns' | 'mapping'; sheetId?: string; columnIndex?: number }

export default function ApiCustomizerPanel() {
  const [catalogue, setCatalogue] = useState<ApiCatalog | null>(null)
  const [definition, setDefinition] = useState<ReportDefinition>(blankReport)
  const [saved, setSaved] = useState<SavedReport[]>([])
  const [savedId, setSavedId] = useState('')
  const [savedVersion, setSavedVersion] = useState(0)
  const [picker, setPicker] = useState<Picker | null>(null)
  const [search, setSearch] = useState('')
  const [showSpecific, setShowSpecific] = useState(false)
  const [customKey, setCustomKey] = useState('')
  const [run, setRun] = useState<ReportRun | null>(null)
  const [resultIndex, setResultIndex] = useState(0)
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [page, setPage] = useState(0)
  const [options, setOptions] = useState(false)
  const [showUnmapped, setShowUnmapped] = useState(false)
  const [history, setHistory] = useState<Activity[] | null>(null)
  const [confirm, setConfirm] = useState<'delete' | 'new' | null>(null)
  const controller = useRef<AbortController | null>(null)

  useEffect(() => {
    const abort = new AbortController()
    Promise.all([portalApi<ApiCatalog>(`${root}/catalogue`, { signal: abort.signal }), portalApi<SavedReport[]>(`${root}/reports`, { signal: abort.signal })])
      .then(([catalog, reports]) => { setCatalogue(catalog); setSaved(reports) })
      .catch(cause => { if (!abort.signal.aborted) setError(toErrorMessage(cause)) })
    return () => { abort.abort(); controller.current?.abort() }
  }, [])

  const columns = reportColumns(definition)
  const currentResult = run?.sheets[resultIndex]
  const selectedSheet = definition.sheets.find(s => s.id === picker?.sheetId)
  const selectedSource = catalogue?.sources.find(s => s.id === selectedSheet?.sourceId)
  const rowType = definition.sheets.find(s => s.id === definition.detailSheetId) ?? definition.sheets[0]
  const bomSheet = definition.sheets.length === 1 && definition.sheets[0].sourceId === inventoryBomSourceId ? definition.sheets[0] : null
  const bomSource = catalogue?.sources.find(s => s.id === inventoryBomSourceId)
  const previewColumns = (currentResult?.columns ?? columns).map((column, index) => ({ column, index }))
    .filter(({ column }) => !bomSheet || showUnmapped || !bomSource?.fields.some(f => f.path === column.path && f.availability === 'unmapped'))
  const openPicker = (next: Picker) => { setPicker(next); setSearch(''); setShowSpecific(false); setCustomKey('') }
  function change(next: ReportDefinition) { setDefinition(next); setRun(null); setPage(0); setResultIndex(0); setMessage(''); setError('') }
  function updateSheet(sheet: ReportSheet) { change({ ...definition, sheets: definition.sheets.map(s => s.id === sheet.id ? sheet : s) }) }
  function changeColumns(next: ReportColumn[]) {
    const updated = withColumns(definition, next)
    // Fulcrum returns these optional details only when their include flag is set.
    for (const sheet of updated.sheets) {
      const source = catalogue?.sources.find(s => s.id === sheet.sourceId)
      for (const [prefix, flag] of Object.entries({ vendorDetails: 'includeVendorData', customerDetails: 'includeCustomerData', customerTiers: 'includeCustomerTierData', usedByItems: 'includeUsageData' }))
        if (sheet.columns.some(c => c.path.startsWith(prefix) || c.path.startsWith(`item.${prefix}`)) && source?.inputs.some(i => i.key === `body.${flag}`)) sheet.inputs = { ...sheet.inputs, [`body.${flag}`]: true }
    }
    change(updated)
  }
  function reorder(from: number, to: number) {
    if (from === to || from < 0 || to < 0 || from >= columns.length || to >= columns.length) return
    const next = [...columns]; next.splice(to, 0, next.splice(from, 1)[0]); changeColumns(next)
  }
  async function preview(sample: boolean) {
    setError(''); setMessage(''); setRun(null); setBusy(sample ? 'sample' : 'live'); setPage(0); setResultIndex(0)
    controller.current = new AbortController()
    try { setRun(await portalApi<ReportRun>(`${root}/preview`, { method: 'POST', headers, body: JSON.stringify({ definition, sample }), signal: controller.current.signal })) }
    catch (cause) { setError(controller.current.signal.aborted ? 'Report cancelled.' : toErrorMessage(cause)) }
    finally { setBusy('') }
  }
  async function save(copy = false) {
    setBusy('save'); setError('')
    try {
      const id = copy || !savedId ? crypto.randomUUID() : savedId
      const report = await portalApi<SavedReport>(`${root}/reports/${id}`, { method: 'PUT', headers, body: JSON.stringify({ definition, version: copy ? 0 : savedVersion }) })
      setSaved(list => [...list.filter(r => r.id !== id), report].sort((a, b) => a.name.localeCompare(b.name)))
      setSavedId(id); setSavedVersion(report.version); setMessage('Report saved. You can reopen it from Saved Reports.')
    } catch (cause) { setError(toErrorMessage(cause)) } finally { setBusy('') }
  }
  async function removeSaved() {
    setBusy('delete'); setError('')
    try {
      await portalApi(`${root}/reports/${savedId}?version=${savedVersion}`, { method: 'DELETE', headers })
      setSaved(list => list.filter(r => r.id !== savedId)); setSavedId(''); setSavedVersion(0); setConfirm(null); setMessage('Saved report removed. Your open report is unchanged.')
    } catch (cause) { setError(toErrorMessage(cause)) } finally { setBusy('') }
  }
  async function download() {
    if (!run) return
    setBusy('export'); setError('')
    try {
      const response = await fetch(`${root}/runs/${run.id}/excel`, { credentials: 'include' })
      if (!response.ok) { const problem = await response.json(); throw new Error(problem.detail || 'Excel download failed.') }
      const url = URL.createObjectURL(await response.blob()); const link = document.createElement('a')
      link.href = url; link.download = `${run.sample ? 'SAMPLE - ' : ''}${run.name.replace(/[^\w .-]/g, '_')}.xlsx`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) { setError(toErrorMessage(cause)) } finally { setBusy('') }
  }
  async function showHistory() {
    try { setHistory(await portalApi<Activity[]>(`${root}/history`)) } catch (cause) { setError(toErrorMessage(cause)) }
  }
  if (!catalogue) return <div className="admin-surface" role={error ? 'alert' : 'status'}>{error || 'Loading available information...'}</div>

  const sources = picker?.type === 'source'
    ? selectedSheet ? relatedSources(catalogue, definition, selectedSheet).map(r => r.source)
      : catalogue.sources.filter(source => source.id !== inventoryBomSourceId && (showSpecific || !source.inputs.some(i => i.required && i.key.startsWith('path.'))))
    : []
  const matches = sources.filter(source => `${sourceName(source)} ${source.category} ${source.description}`.toLowerCase().includes(search.toLowerCase()))
    .sort((a, b) => sourceName(a).localeCompare(sourceName(b)))
  const fields = selectedSource?.fields.filter(field => `${field.label} ${field.path}`.toLowerCase().includes(search.toLowerCase())) ?? []

  return <div className="ac-builder">
    <fieldset className="ac-controls" disabled={!!busy}>
      <div className="ac-toolbar">
        <label className="ac-report-name"><span>Report Name</span><input maxLength={120} value={definition.name} onChange={e => change({ ...definition, name: e.target.value })} /></label>
        <label><span>Saved Reports</span><select value={savedId} onChange={e => { const report = saved.find(r => r.id === e.target.value); setSavedId(report?.id ?? ''); setSavedVersion(report?.version ?? 0); if (report) change(loadLayout(report.definition)) }}>
          <option value="">Unsaved Report</option>{saved.map(report => <option value={report.id} key={report.id}>{report.name}</option>)}</select></label>
        <button className="solid-button" disabled={!columns.length} onClick={() => void save()}><Save size={15} /> Save Report</button>
        <button className="ghost-button" onClick={() => setConfirm('new')}><Plus size={15} /> New</button>
        <button className="ghost-button" aria-label="Report options" aria-expanded={options} onClick={() => setOptions(!options)}><Settings2 size={16} /></button>
        <button className="ghost-button" aria-label="Report activity" onClick={() => void showHistory()}><History size={16} /></button>
      </div>
      {options && <section className="ac-options" aria-label="Report options">
        <label><span>Report Type</span><select value={definition.outputMode ?? 'separate'} onChange={e => change({ ...definition, outputMode: e.target.value as 'combined' | 'separate' })}>
          <option value="combined">One Combined Table</option><option value="separate">Separate Tables In Excel</option></select></label>
        <label><span>Maximum Records Per Request</span><input type="number" min={1} max={5000} value={definition.maxRecords} onChange={e => change({ ...definition, maxRecords: Number(e.target.value) })} /></label>
        <label><span>Sort By</span><select value={definition.outputSortColumn ?? ''} onChange={e => change({ ...definition, outputSortColumn: e.target.value === '' ? null : Number(e.target.value) })}>
          <option value="">Source Order</option>{columns.map((c, i) => <option value={i} key={i}>{c.header}</option>)}</select></label>
        <label className="ac-check"><input type="checkbox" checked={definition.outputSortDescending ?? false} onChange={e => change({ ...definition, outputSortDescending: e.target.checked })} /> Descending</label>
        {bomSheet && <button className="ghost-button" onClick={() => openPicker({ type: 'source', sheetId: bomSheet.id })}><Link2 size={14} /> Connect Other Fulcrum Records</button>}
        {savedId && <div className="ac-actions"><button className="ghost-button" onClick={() => void save(true)}>Save A Copy</button><button className="ghost-button" onClick={() => setConfirm('delete')}>Delete Saved Report</button></div>}
        <p>Column widths and row heights fit the content automatically. Long values wrap; Excel filters and frozen headings are included.</p>
      </section>}
      {bomSheet && bomSource && <InventoryBomControls sheet={bomSheet} source={bomSource} columns={columns} change={updateSheet} sorted={definition.outputSortColumn != null}
        customize={() => openPicker({ type: 'columns', sheetId: bomSheet.id })} addFields={() => openPicker({ type: 'fields', sheetId: bomSheet.id })} filters={() => openPicker({ type: 'filters', sheetId: bomSheet.id })} />}
      {!bomSheet && <section className="ac-step">
        <div className="ac-step-heading"><span>1</span><div><h3>Choose Your Starting Records</h3><p>Start with any available record type, then choose the information you need.</p></div></div>
        {!definition.sheets.length ? <div className="ac-start-options">{bomSource && <button className="ac-start ac-template-start" onClick={() => { change(inventoryBomStarter(catalogue)); setSavedId(''); setSavedVersion(0) }}><Table2 size={22} /><span>Inventory BOM<small>Start With Your Workbook Layout</small><small>Revisions, routing, operation times and required materials. All 32 columns are ready to customize.</small></span><ArrowRight size={18} /></button>}
          <button className="ac-start" onClick={() => openPicker({ type: 'source' })}><Search size={19} /><span>Build A Different Report<small>Choose any available Fulcrum records and fields</small></span><ArrowRight size={18} /></button></div>
          : <div className="ac-start-summary"><Table2 size={20} /><strong>{definition.sheets[0].name}</strong><span>{Object.values(definition.sheets[0].inputs).filter(v => v != null).length} Filters Applied</span><button className="ghost-button" onClick={() => openPicker({ type: 'filters', sheetId: definition.sheets[0].id })}><Filter size={14} /> Filter Records</button><button className="ghost-button" onClick={() => setConfirm('new')}>Start Over</button></div>}
      </section>}
      {definition.sheets.length > 0 && !bomSheet && <>
        <section className="ac-step">
          <div className="ac-step-heading"><span>2</span><div><h3>Choose Fields And Related Information</h3><p>Select the fields to include. Related records connect automatically using their IDs.</p></div></div>
          <div className="ac-records">{definition.sheets.map(sheet => <article className={`ac-record ${sheet.parentSheetId ? 'ac-related' : ''}`} key={sheet.id}>
            <div className="ac-record-heading"><div><strong>{sheet.name}</strong>{sheet.parentSheetId && <small><Link2 size={12} /> From {definition.sheets.find(s => s.id === sheet.parentSheetId)?.name} / Matched Automatically</small>}</div>
              <div className="ac-actions"><button className="ghost-button" onClick={() => openPicker({ type: 'fields', sheetId: sheet.id })}><Plus size={14} /> Choose Fields</button>
                <button className="ghost-button" aria-label={`Filter ${sheet.name}`} onClick={() => openPicker({ type: 'filters', sheetId: sheet.id })}><Filter size={14} /></button>
                {sheet.parentSheetId && <button className="ghost-button" aria-label={`Remove ${sheet.name}`} onClick={() => change(removeSource(definition, sheet.id))}><X size={14} /></button>}</div></div>
            <div className="ac-field-chips">{columns.filter(c => c.sheetId === sheet.id).map(c => <button key={c.path} onClick={() => changeColumns(columns.filter(column => column !== c))} title={`Remove ${c.header}`}>{c.header}<X size={12} /></button>)}{!columns.some(c => c.sheetId === sheet.id) && <span>No fields selected. This source can still connect other records.</span>}</div>
            <button className="ac-add-related" disabled={definition.sheets.length >= 8} onClick={() => openPicker({ type: 'source', sheetId: sheet.id })}><Link2 size={14} /> Add Related Information</button>
            {catalogue.sources.find(s => s.id === sheet.sourceId)?.path === '/api/items/list/v2' && <p className="ac-hint">Current item BOMs and routing: Is Archived = No, Latest Revision = Yes. The API does not expose a separate active-BOM flag.</p>}
          </article>)}</div>
        </section>
        <section className="ac-step">
          <div className="ac-step-heading"><span>3</span><div><h3>Review Your Report</h3><p>Arrange fields below. Sizing is automatic.</p></div></div>
          {definition.outputMode === 'combined' && <div className="ac-row-type"><label><span>Each Row Represents</span><select value={rowType?.id ?? ''} onChange={e => change({ ...definition, detailSheetId: e.target.value })}>{definition.sheets.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
            <p>{rowType?.id === definition.sheets[0].id ? 'Related details are listed together beside each starting record.' : 'Starting-record information repeats beside each matching detail. Other related lists stay together in a cell.'}</p></div>}
          <div className="ac-column-strip" aria-label="Report columns">{columns.map((c, index) => <div key={`${c.sheetId}:${c.path}`} draggable onDragStart={e => e.dataTransfer.setData('text/arda-column', String(index))} onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); const value = e.dataTransfer.getData('text/arda-column'); if (value && Number.isInteger(Number(value))) reorder(Number(value), index) }}>
            <span>{index + 1}</span><strong>{c.header}</strong><small>{definition.sheets.find(s => s.id === c.sheetId)?.name}</small>
            <button className="ghost-button" aria-label={`Move column ${index + 1} left`} disabled={index === 0} onClick={() => reorder(index, index - 1)}><ArrowLeft size={12} /></button>
            <button className="ghost-button" aria-label={`Move column ${index + 1} right`} disabled={index === columns.length - 1} onClick={() => reorder(index, index + 1)}><ArrowRight size={12} /></button>
          </div>)}</div>
          <details className="ac-format-options"><summary><Settings2 size={14} /> Rename Or Format Columns<ChevronDown size={14} /></summary><div>{columns.map((c, index) => <label key={`${c.sheetId}:${c.path}`}><span>{definition.sheets.find(s => s.id === c.sheetId)?.name} / {c.path}</span>
            <input aria-label={`Column ${index + 1} heading`} maxLength={120} value={c.header} onChange={e => changeColumns(columns.map((v, i) => i === index ? { ...v, header: e.target.value } : v))} />
            <select aria-label={`Column ${index + 1} format`} value={c.format} onChange={e => changeColumns(columns.map((v, i) => i === index ? { ...v, format: e.target.value } : v))}>{['text', 'number', 'currency', 'date', 'boolean'].map(format => <option value={format} key={format}>{format[0].toUpperCase() + format.slice(1)}</option>)}</select></label>)}</div></details>
        </section>
      </>}
    </fieldset>
    {error && <div className="admin-error" role="alert">{error}</div>}{message && <div className="ac-notice" role="status">{message}</div>}
    {definition.sheets.length > 0 && <>
      <div className="ac-run-bar"><span>Read-Only / Admin Access</span><div className="ac-actions">
        <button className="ghost-button" disabled={!!busy || !columns.length} onClick={() => void preview(true)}><Table2 size={15} /> Try Sample Data</button>
        <button className="solid-button" disabled={!!busy || !columns.length} onClick={() => void preview(false)}><ArrowRight size={15} /> Preview Fulcrum Data</button>
        <button className="ghost-button" disabled={!!busy || !run} onClick={() => void download()}><Download size={15} /> Download Excel</button>
        {(busy === 'sample' || busy === 'live') && <button className="ghost-button" onClick={() => controller.current?.abort()}>Cancel</button>}</div></div>
      {busy && <p className="ac-notice" role="status">{busy === 'live' ? 'Finding matching records and related information...' : 'Working...'}</p>}
      <section className="ac-preview" aria-label="Report preview">
        <div className="ac-preview-meta"><strong>{run ? run.sample ? 'Sample Report' : 'Fulcrum Report' : 'Report Preview'}</strong>{run && <span>{currentResult?.rows.length ?? 0} Rows / {run.requestCount} API Requests</span>}</div>
        {run && <>{run.sample && <p className="ac-note">Invented sample data. No Fulcrum API calls were made; filters are not applied to sample records.</p>}
          {run.sheets.length > 1 && <label className="ac-table-picker"><span>Preview Table</span><select value={resultIndex} onChange={e => { setResultIndex(Number(e.target.value)); setPage(0) }}>{run.sheets.map((s, i) => <option key={s.id} value={i}>{s.name}</option>)}</select></label>}</>}
        {bomSheet && <label className="ac-check ac-preview-toggle"><input type="checkbox" checked={showUnmapped} onChange={e => setShowUnmapped(e.target.checked)} /> Show Unmapped Template Columns <small>Excel includes all {columns.length} selected columns.</small></label>}
        <div className="ac-preview-scroll"><table style={currentResult ? { width: previewColumns.reduce((sum, { column }) => sum + column.width * 8, 0), tableLayout: 'fixed' } : undefined}>
          {currentResult && <colgroup>{previewColumns.map(({ column, index }) => <col key={index} style={{ width: column.width * 8 }} />)}</colgroup>}
          <thead><tr>{previewColumns.map(({ column, index }) => <th key={index}>{column.header}</th>)}</tr></thead>
          <tbody>{currentResult?.rows.slice(page * 50, page * 50 + 50).map((row, i) => <tr key={i}>{previewColumns.map(({ column, index }) => <td key={index}>{displayCell(row[index], column.format)}</td>)}</tr>)}</tbody>
        </table></div>
        {!run && <p className="ac-empty">Your selected fields are ready. Try sample data or preview matching Fulcrum records.</p>}
        {currentResult?.rows.length === 0 && <p className="ac-empty">No matching records.</p>}
        {run && <><div className="ac-pagination"><button className="ghost-button" disabled={!page} onClick={() => setPage(p => p - 1)}>Previous</button><span>Page {page + 1} Of {Math.max(1, Math.ceil((currentResult?.rows.length ?? 0) / 50))}</span><button className="ghost-button" disabled={(page + 1) * 50 >= (currentResult?.rows.length ?? 0)} onClick={() => setPage(p => p + 1)}>Next</button></div>
          <p className="ac-note">{run.warnings.filter(w => !w.startsWith('SAMPLE DATA')).join(' ')} Download includes every row. Preview expires after 15 minutes or a new preview.</p></>}
      </section>
    </>}
    {picker && <BuilderDialog title={picker.type === 'columns' ? 'Customize Columns' : picker.type === 'mapping' ? `Choose Data For ${columns[picker.columnIndex ?? 0]?.header}` : picker.type === 'source' ? selectedSheet ? `Add Information Related To ${selectedSheet.name}` : 'Choose Your Starting Records' : picker.type === 'fields' ? `Choose Fields From ${selectedSheet?.name}` : `Filter ${selectedSheet?.name}`} close={() => setPicker(null)}>
      {picker.type === 'columns' && selectedSource && <><p>Choose what each column contains. Unmapped columns stay blank. Changes affect this report only, never Fulcrum.</p>
        {columns.some(c => selectedSource.fields.some(f => f.path === c.path && f.availability === 'unmapped')) && <button className="ghost-button" onClick={() => changeColumns(columns.filter(c => !selectedSource.fields.some(f => f.path === c.path && f.availability === 'unmapped')))}>Remove Unmapped Columns</button>}
        <div className="ac-mapping-list">{columns.map((column, index) => {
          const field = selectedSource.fields.find(f => f.path === column.path)
          return <article key={index}>
            <span className="ac-column-position">{index + 1}</span>
            <label><span>Column Heading</span><input aria-label={`Column ${index + 1} heading`} maxLength={120} value={column.header} onChange={e => changeColumns(columns.map((c, i) => i === index ? { ...c, header: e.target.value } : c))} /></label>
            <button className={`ac-mapping-source ${field?.availability === 'unmapped' ? 'ac-unmapped' : ''}`} onClick={() => openPicker({ type: 'mapping', sheetId: selectedSheet!.id, columnIndex: index })} aria-label={`Change data for column ${index + 1}`} title={field?.description}><small>Fulcrum Data</small><strong>{field?.availability === 'unmapped' ? 'Not Mapped / Blank' : field?.label ?? column.path}</strong><span>Change Field <ArrowRight size={12} /></span></button>
            <label><span>Display</span><select aria-label={`Column ${index + 1} format`} value={column.format} onChange={e => changeColumns(columns.map((c, i) => i === index ? { ...c, format: e.target.value } : c))}>{['text', 'number', 'currency', 'date', 'duration', 'boolean'].map(f => <option key={f} value={f}>{f === 'duration' ? 'Time (H:M:S)' : f[0].toUpperCase() + f.slice(1)}</option>)}</select></label>
            <div className="ac-actions"><button className="ghost-button" aria-label={`Move column ${index + 1} earlier`} disabled={index === 0} onClick={() => reorder(index, index - 1)}><ArrowLeft size={13} /></button><button className="ghost-button" aria-label={`Move column ${index + 1} later`} disabled={index === columns.length - 1} onClick={() => reorder(index, index + 1)}><ArrowRight size={13} /></button><button className="ghost-button" aria-label={`Remove column ${index + 1}`} onClick={() => changeColumns(columns.filter((_, i) => i !== index))}><X size={14} /></button></div>
          </article>
        })}</div><button className="ghost-button" onClick={() => openPicker({ type: 'fields', sheetId: selectedSheet!.id })}><Plus size={14} /> Add Fulcrum Fields</button>
      </>}
      {picker.type === 'mapping' && selectedSheet && selectedSource && <><p>Select a field for this column. The column heading is kept; its display format follows the selected field.</p>
        <label className="ac-search"><Search size={16} /><input autoFocus aria-label="Search mapping fields" placeholder="Search all available BOM and Fulcrum fields..." value={search} onChange={e => setSearch(e.target.value)} /></label>
        <div className="ac-source-list">{fields.filter(f => f.availability !== 'unmapped').map(field => <button key={field.path} onClick={() => { changeColumns(columns.map((c, i) => i === picker.columnIndex ? { ...columnFor(field, selectedSheet.id), header: c.header } : c)); openPicker({ type: 'columns', sheetId: selectedSheet.id }) }}><div><strong>{field.label}</strong><p>{field.description}</p></div><ArrowRight size={15} /></button>)}</div>
        {selectedSource.fields.some(f => f.path.endsWith('customFields')) && <div className="ac-custom-mapping"><label><span>Or Use A Fulcrum Custom Field</span><input aria-label="Map custom field name" placeholder="Exact custom field name" value={customKey} onChange={e => setCustomKey(e.target.value)} maxLength={120} /></label><button className="ghost-button" disabled={!customKey.trim()} onClick={() => { const path = `${selectedSource.fields.find(f => f.path.endsWith('customFields'))!.path}.${customKey.trim()}`; changeColumns(columns.map((c, i) => i === picker.columnIndex ? { ...c, sheetId: selectedSheet.id, path, format: 'text' } : c)); openPicker({ type: 'columns', sheetId: selectedSheet.id }) }}>Use Custom Field</button></div>}
        <button className="ghost-button" onClick={() => openPicker({ type: 'columns', sheetId: selectedSheet.id })}>Back To Columns</button>
      </>}
      {picker.type === 'source' && <><p>{selectedSheet ? 'Choose what to find. Arda will connect each result to the correct parent record.' : 'Choose a record type. You can add related information after this step.'}</p>
        <label className="ac-search"><Search size={16} /><input autoFocus aria-label="Search available information" placeholder="Search records or information..." value={search} onChange={e => setSearch(e.target.value)} /></label>
        {!selectedSheet && <label className="ac-check"><input type="checkbox" checked={showSpecific} onChange={e => setShowSpecific(e.target.checked)} /> Include Sources Requiring A Specific Record ID</label>}
        <div className="ac-source-list">{matches.map(source => <button key={source.id} title={source.description} onClick={() => { const next = addSource(definition, catalogue, source, selectedSheet); change(next); openPicker({ type: 'fields', sheetId: next.sheets.at(-1)!.id }) }}>
          <div><strong>{sourceName(source)}</strong><small>{source.category}</small><p>{source.description}</p></div><Plus size={17} /></button>)}{!matches.length && <p>No matching relationships are available. Try another search or add information from another selected record.</p>}</div>
      </>}
      {picker.type === 'fields' && selectedSheet && selectedSource && <><p>Choose fields to display. IDs needed for relationships are used automatically, whether displayed or not.</p>
        <label className="ac-search"><Search size={16} /><input autoFocus aria-label="Search fields" placeholder="Search field names..." value={search} onChange={e => setSearch(e.target.value)} /></label>
        <div className="ac-field-list">{fields.map(field => {
          const checked = columns.some(c => c.sheetId === selectedSheet.id && c.path === field.path)
          const heading = fieldHeading(field.path, field.label, selectedSheet.name)
          return <label key={field.path} title={field.description}><input type="checkbox" checked={checked} disabled={!checked && columns.length >= 80} onChange={() => changeColumns(checked ? columns.filter(c => c.sheetId !== selectedSheet.id || c.path !== field.path) : [...columns, { ...columnFor(field, selectedSheet.id), header: heading }])} /><span><strong>{heading}</strong><small>{field.availability === 'unmapped' ? 'Template Column / Not Mapped' : field.type === 'duration' ? 'Time (Hours:Minutes:Seconds)' : field.type === 'object' || field.type === 'array' ? 'Includes A List / Group Of Values' : field.type === 'boolean' ? 'Yes / No' : ['integer', 'number'].includes(field.type) ? 'Number' : 'Text'}</small></span>{checked && <Check size={14} />}</label>
        })}</div>
        {selectedSource.fields.some(f => f.path === 'customFields') && <details className="ac-format-options"><summary>Add A Custom Field</summary><div className="ac-custom-key"><input aria-label="Custom field key" placeholder="Exact custom field name" value={customKey} onChange={e => setCustomKey(e.target.value)} /><button className="ghost-button" disabled={!customKey.trim()} onClick={() => { const path = `customFields.${customKey.trim()}`; if (!columns.some(c => c.sheetId === selectedSheet.id && c.path === path)) changeColumns([...columns, { sheetId: selectedSheet.id, path, header: customKey.trim(), format: 'text', width: 24 }]); setCustomKey('') }}>Add Field</button></div></details>}
      </>}
      {picker.type === 'filters' && selectedSheet && selectedSource && <FilterEditor sheet={selectedSheet} source={selectedSource} change={updateSheet} />}
      <footer><button className="solid-button" onClick={() => setPicker(null)}>Done</button></footer>
    </BuilderDialog>}
    {history && <BuilderDialog title="Report Activity" close={() => setHistory(null)}><div className="ac-history">{history.length ? history.map(item => <article key={item.id}><strong>{item.action}</strong><span>{item.reportName}</span><small>{item.actor} / {new Date(item.occurredAt).toLocaleString()}</small></article>) : <p>No activity yet.</p>}</div></BuilderDialog>}
    {confirm && <BuilderDialog title={confirm === 'new' ? 'Start A New Report?' : 'Delete Saved Report?'} close={() => setConfirm(null)}>
      <p>{confirm === 'new' ? 'Unsaved edits will be cleared. Your saved reports will remain available.' : 'This removes the saved report definition only. It does not delete any Fulcrum records.'}</p>
      <footer><button className="ghost-button" onClick={() => setConfirm(null)}>Cancel</button><button className="solid-button" disabled={!!busy} onClick={() => { if (confirm === 'delete') void removeSaved(); else { change(blankReport()); setSavedId(''); setSavedVersion(0); setConfirm(null) } }}>{confirm === 'new' ? 'Start New Report' : 'Delete Report'}</button></footer>
    </BuilderDialog>}
  </div>
}
