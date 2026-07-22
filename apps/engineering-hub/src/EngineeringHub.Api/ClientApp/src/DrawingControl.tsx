import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { AlertTriangle, CheckCircle2, Database, FilePlus2, FileText, History, MapPin, Plus, Search, ShieldCheck, Trash2, Upload, X } from 'lucide-react'

interface DrawingList {
  id: number; drawingNumber: string; title: string; customer: string; partNumbers: string[]; approvalStatus: string
  currentRevision: string | null; currentRevisionDate: string | null; effectiveDate: string | null; isObsolete: boolean
  physicalMylarLocation: string | null; isMylarCheckedOut: boolean; createdAt: string
}
interface Revision { id: number; revisionNumber: string; revisionDate: string; uploadedAt: string; effectiveDate: string | null; approvalDate: string | null; changeDescription: string; status: string; originalFileName: string; fileType: string; fileSize: number; fileHash: string; hasSourceFile: boolean; uploadedBy: string; approvedBy: string | null; approvalComments: string | null; notes: string | null }
interface Audit { id: number; revisionNumber: string | null; action: string; details: string; actor: string; occurredAt: string }
interface Validation { id: number; validationType: string; result: string; notes: string | null; validatedBy: string; validatedAt: string }
interface MylarEvent { id: number; type: string; person: string; purpose: string | null; location: string | null; recordedBy: string; recordedAt: string }
interface DrawingDetail extends DrawingList { notes: string | null; fileLocation: string | null; mylarCheckedOutBy: string | null; mylarCheckedOutAt: string | null; createdBy: string; approvedBy: string | null; approvedAt: string | null; currentApprovedRevisionId: number | null; revisions: Revision[]; relatedDocuments: { id: number; kind: string; referenceNumber: string; title: string | null; location: string | null }[]; validations: Validation[]; mylarHistory: MylarEvent[]; auditHistory: Audit[] }
interface StorageStatus { configured: boolean; isNetworkPath: boolean; available: boolean; message: string }
interface DeleteTarget { kind: 'drawing' | 'revision'; id: number; label: string; matchValue: string }

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: 'include', ...init })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? `Request failed (${response.status}).`)
  }
  return response.status === 204 ? undefined as T : response.json()
}
const date = (value: string | null) => value ? new Date(value).toLocaleDateString() : '—'

export default function DrawingControl() {
  const [drawings, setDrawings] = useState<DrawingList[]>([])
  const [selected, setSelected] = useState<DrawingDetail | null>(null)
  const [query, setQuery] = useState('')
  const [showCreate, setShowCreate] = useState(false)
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [deleteAcknowledged, setDeleteAcknowledged] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const [deleteError, setDeleteError] = useState<string | null>(null)

  async function loadList(search = query) {
    setError(null)
    try { setDrawings(await api(`/api/drawings?query=${encodeURIComponent(search)}`)) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to load drawings.') }
  }
  async function open(id: number) { setSelected(await api(`/api/drawings/${id}`)) }
  async function refreshSelected() { if (selected) await open(selected.id); await loadList() }
  useEffect(() => {
    void loadList('')
    void api<StorageStatus>('/api/drawing-storage/status').then(setStorage).catch(() => setStorage(null))
  }, [])
  useEffect(() => { const timer = window.setTimeout(() => void loadList(query), 180); return () => window.clearTimeout(timer) }, [query])
  useEffect(() => {
    if (!deleteTarget) return
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) closeDeleteDialog() }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [deleteTarget, busy])

  async function createDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setError(null)
    const form = new FormData(event.currentTarget)
    try {
      const created = await api<DrawingDetail>('/api/drawings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ drawingNumber: form.get('drawingNumber'), title: form.get('title'), customer: form.get('customer'), partNumbers: String(form.get('partNumbers') ?? '').split(',').map(x => x.trim()).filter(Boolean), notes: form.get('notes'), physicalMylarLocation: form.get('mylarLocation'), relatedDocuments: [] }) })
      setShowCreate(false); await loadList(''); await open(created.id)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to create drawing.') }
    finally { setBusy(false) }
  }

  async function uploadRevision(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!selected) return; setBusy(true); setError(null)
    try { await api(`/api/drawings/${selected.id}/revisions`, { method: 'POST', body: new FormData(event.currentTarget) }); event.currentTarget.reset(); await refreshSelected() }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to upload revision.') }
    finally { setBusy(false) }
  }

  async function status(revision: Revision, next: string) {
    setBusy(true); setError(null)
    try {
      if (next === 'Approved') await api(`/api/drawing-revisions/${revision.id}/approve`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ effectiveDate: revision.effectiveDate, comments: 'Approved through Drawing Control.' }) })
      else await api(`/api/drawing-revisions/${revision.id}/status`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ status: next }) })
      await refreshSelected()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to update revision.') }
    finally { setBusy(false) }
  }

  function requestRevisionDelete(revision: Revision) {
    openDeleteDialog({ kind: 'revision', id: revision.id, label: `Revision ${revision.revisionNumber} file`, matchValue: revision.originalFileName })
  }

  function requestDrawingDelete() {
    if (selected) openDeleteDialog({ kind: 'drawing', id: selected.id, label: 'Draft drawing record', matchValue: selected.drawingNumber })
  }

  function openDeleteDialog(target: DeleteTarget) {
    setDeleteTarget(target)
    setDeleteAcknowledged(false)
    setDeleteConfirmation('')
    setDeleteError(null)
  }

  function closeDeleteDialog() {
    setDeleteTarget(null)
    setDeleteAcknowledged(false)
    setDeleteConfirmation('')
    setDeleteError(null)
  }

  async function confirmDelete(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!deleteTarget || !deleteAcknowledged || deleteConfirmation !== deleteTarget.matchValue) return
    setBusy(true); setDeleteError(null); setError(null)
    try {
      const isRevision = deleteTarget.kind === 'revision'
      await api(isRevision ? `/api/drawing-revisions/${deleteTarget.id}` : `/api/drawings/${deleteTarget.id}`, {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(isRevision
          ? { confirmed: true, fileName: deleteConfirmation }
          : { confirmed: true, drawingNumber: deleteConfirmation }),
      })
      closeDeleteDialog()
      if (isRevision) await refreshSelected()
      else { setSelected(null); await loadList() }
    } catch (cause) { setDeleteError(cause instanceof Error ? cause.message : 'Unable to permanently delete this item.') }
    finally { setBusy(false) }
  }

  async function mylar(checkout: boolean) {
    if (!selected) return
    const person = window.prompt(checkout ? 'Who is checking out the Mylar?' : 'Who returned the Mylar?')
    if (!person) return
    const purpose = checkout ? window.prompt('Purpose (optional)') : null
    setBusy(true)
    try { await api(`/api/drawings/${selected.id}/mylar/${checkout ? 'checkout' : 'return'}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ person, purpose, location: selected.physicalMylarLocation }) }); await refreshSelected() }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to update Mylar.') }
    finally { setBusy(false) }
  }

  return <div className="drawing-workspace">
    {error && <div className="inline-alert" role="alert">{error}</div>}
    <section className="drawing-toolbar">
      <label className="topbar-search drawing-search"><Search size={15}/><input value={query} onChange={e => setQuery(e.target.value)} placeholder="Search drawing number, title, customer, part number, or notes" /></label>
      {storage && <span className={`storage-mode ${storage.available ? 'available' : 'unavailable'}`} title={storage.message}>
        <Database size={14}/>{storage.isNetworkPath ? 'Network storage' : 'Local test storage'}
      </span>}
      <button className="button" onClick={() => setShowCreate(value => !value)}><Plus size={15}/> New drawing</button>
    </section>

    {showCreate && <form className="panel record-form" onSubmit={createDrawing}>
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Controlled record</span><h2>Create drawing record</h2></div></div>
      <div className="form-grid"><label>Drawing number<input name="drawingNumber" required /></label><label>Title<input name="title" required /></label><label>Customer<input name="customer" required /></label><label>Linked part numbers<input name="partNumbers" placeholder="PN-1001, PN-1002" /></label><label>Physical Mylar location<input name="mylarLocation" /></label><label className="wide">Notes<textarea name="notes" rows={3}/></label></div>
      <div className="form-actions"><button className="button" disabled={busy}>Create record</button><button type="button" className="button ghost" onClick={() => setShowCreate(false)}>Cancel</button></div>
    </form>}

    <section className="drawing-layout">
      <article className="panel drawing-register">
        <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Drawing register</span><h2>{drawings.length} controlled record{drawings.length === 1 ? '' : 's'}</h2></div></div>
        <div className="drawing-list">{drawings.map(item => <button key={item.id} className={`drawing-row ${selected?.id === item.id ? 'selected' : ''}`} onClick={() => void open(item.id)}>
          <span><strong>{item.drawingNumber}</strong><small>{item.title}</small></span><span><small>{item.customer}</small><small>{item.partNumbers.join(', ') || 'No linked parts'}</small></span><span className={`status-pill status-${item.approvalStatus.toLowerCase()}`}>{item.approvalStatus}</span><span><strong>{item.currentRevision ?? '—'}</strong><small>Current rev</small></span>
        </button>)}</div>
        {!drawings.length && <div className="empty-search-state"><strong>No drawings found</strong><p>Create the first controlled drawing record or adjust the search.</p></div>}
      </article>

      {!selected ? <article className="panel drawing-empty"><FileText size={30}/><h2>Select a drawing</h2><p>Open a record to view its approved PDF, revisions, Mylar status, validations, and audit history.</p></article> : <article className="drawing-detail">
        <section className="panel detail-summary"><div><span className="eyebrow">{selected.customer}</span><h2>{selected.drawingNumber} · {selected.title}</h2><p>{selected.notes || 'No drawing notes recorded.'}</p></div><div className="detail-summary-actions"><span className={`status-pill status-${selected.approvalStatus.toLowerCase()}`}>{selected.approvalStatus}</span>{selected.approvalStatus === 'Draft' && selected.revisions.length === 0 && <button type="button" className="button danger" disabled={busy} onClick={requestDrawingDelete}><Trash2 size={14}/> Delete draft</button>}</div></section>
        <section className="detail-kpis"><div><small>Current revision</small><strong>{selected.revisions.find(r => r.id === selected.currentApprovedRevisionId)?.revisionNumber ?? 'None'}</strong></div><div><small>Effective date</small><strong>{date(selected.effectiveDate)}</strong></div><div><small>Linked parts</small><strong>{selected.partNumbers.length}</strong></div><div><small>Mylar</small><strong>{selected.isMylarCheckedOut ? 'Checked out' : selected.physicalMylarLocation || 'Not tracked'}</strong></div></section>
        {selected.currentApprovedRevisionId && <section className="panel pdf-panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Approved PDF</span><h2>Controlled viewer</h2></div><a className="button ghost" href={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file`} target="_blank">Open PDF</a></div><iframe title="Approved drawing PDF" src={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file#toolbar=1`} /></section>}
        <form className="panel record-form" onSubmit={uploadRevision}><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">New permanent record</span><h2>Upload drawing revision</h2></div><Upload size={18}/></div><div className="form-grid"><label>Revision number<input name="revisionNumber" required /></label><label>Revision date<input name="revisionDate" type="date" required /></label><label>Effective date<input name="effectiveDate" type="date" /></label><label>Approved-view PDF<input name="pdf" type="file" accept="application/pdf,.pdf" required /></label><label>Original source file<input name="source" type="file" /></label><label className="wide">Change description<textarea name="changeDescription" required rows={2}/></label><label className="wide">Notes<textarea name="notes" rows={2}/></label></div><button className="button" disabled={busy}><FilePlus2 size={15}/> Store revision</button></form>
        <section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Permanent history</span><h2>Drawing revisions</h2></div><History size={18}/></div><div className="revision-list">{selected.revisions.map(rev => <div className="revision-row" key={rev.id}><div><strong>Rev {rev.revisionNumber}</strong><small>{rev.changeDescription}</small><small>Uploaded {date(rev.uploadedAt)} by {rev.uploadedBy} · SHA-256 {rev.fileHash.slice(0, 12)}…</small></div><span className={`status-pill status-${rev.status.toLowerCase()}`}>{rev.status}</span><div className="revision-actions"><a className="button ghost" href={`/api/drawing-revisions/${rev.id}/file`} target="_blank">PDF</a>{rev.hasSourceFile && <a className="button ghost" href={`/api/drawing-revisions/${rev.id}/source`}>Source</a>}{rev.status === 'Draft' && <button type="button" className="button ghost" disabled={busy} onClick={() => void status(rev, 'UnderReview')}>Submit review</button>}{rev.status === 'UnderReview' && <button type="button" className="button" disabled={busy} onClick={() => void status(rev, 'Approved')}><CheckCircle2 size={14}/> Approve</button>}{(rev.status === 'Draft' || rev.status === 'UnderReview') && <button type="button" className="button danger" disabled={busy} onClick={() => requestRevisionDelete(rev)}><Trash2 size={14}/> Delete file</button>}</div></div>)}</div></section>
        <section className="detail-columns"><section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Physical control</span><h2>Mylar tracking</h2></div><MapPin size={18}/></div><p className="section-copy">Location: {selected.physicalMylarLocation || 'Not recorded'}{selected.mylarCheckedOutBy ? ` · Held by ${selected.mylarCheckedOutBy}` : ''}</p><button className="button ghost" disabled={busy} onClick={() => void mylar(!selected.isMylarCheckedOut)}>{selected.isMylarCheckedOut ? 'Record return' : 'Check out Mylar'}</button>{selected.mylarHistory.map(item => <div className="history-line" key={item.id}><strong>{item.type}</strong><span>{item.person} · {date(item.recordedAt)}</span></div>)}</section><section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Traceability</span><h2>Validations</h2></div><ShieldCheck size={18}/></div>{selected.validations.length ? selected.validations.map(item => <div className="history-line" key={item.id}><strong>{item.validationType}: {item.result}</strong><span>{item.validatedBy} · {date(item.validatedAt)}</span></div>) : <p className="section-copy">No validation records yet.</p>}</section></section>
        <section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Append-only log</span><h2>Complete audit history</h2></div><ShieldCheck size={18}/></div><div className="audit-list">{selected.auditHistory.map(item => <div className="audit-row" key={item.id}><span className="audit-dot"/><div><strong>{item.action}{item.revisionNumber ? ` · Rev ${item.revisionNumber}` : ''}</strong><p>{item.details}</p><small>{item.actor} · {new Date(item.occurredAt).toLocaleString()}</small></div></div>)}</div></section>
      </article>}
    </section>

    {deleteTarget && <div className="delete-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeDeleteDialog() }}>
      <section className="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title" aria-describedby="delete-dialog-description">
        <header className="delete-dialog-header"><span className="delete-dialog-icon"><AlertTriangle size={21}/></span><div><span className="eyebrow">Permanent deletion</span><h2 id="delete-dialog-title">Delete {deleteTarget.label}?</h2></div><button type="button" className="delete-dialog-close" aria-label="Close deletion dialog" disabled={busy} onClick={closeDeleteDialog}><X size={18}/></button></header>
        <div className="delete-warning" id="delete-dialog-description"><strong>This action cannot be undone.</strong><p>{deleteTarget.kind === 'revision' ? 'The PDF and its complete revision package will be permanently removed from the system and configured file storage.' : 'This empty draft drawing record will be permanently removed from the system.'}</p></div>
        <form className="delete-dialog-form" onSubmit={confirmDelete}>
          <label className="delete-acknowledgment"><input type="checkbox" checked={deleteAcknowledged} onChange={event => setDeleteAcknowledged(event.target.checked)}/><span><strong>I understand this deletion is permanent</strong><small>This is the first required confirmation.</small></span></label>
          <label className="delete-confirmation-field"><span>Type the exact {deleteTarget.kind === 'revision' ? 'PDF filename' : 'drawing number'}</span><code>{deleteTarget.matchValue}</code><input autoFocus autoComplete="off" spellCheck={false} value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} placeholder={deleteTarget.matchValue}/>{deleteConfirmation && deleteConfirmation !== deleteTarget.matchValue && <small className="delete-match-error">The value does not match exactly.</small>}</label>
          {deleteError && <div className="inline-alert" role="alert">{deleteError}</div>}
          <div className="delete-dialog-actions"><button type="button" className="button ghost" disabled={busy} onClick={closeDeleteDialog}>Cancel</button><button type="submit" className="button danger" disabled={busy || !deleteAcknowledged || deleteConfirmation !== deleteTarget.matchValue}><Trash2 size={15}/>{busy ? 'Deleting…' : 'Permanently delete'}</button></div>
        </form>
      </section>
    </div>}
  </div>
}
