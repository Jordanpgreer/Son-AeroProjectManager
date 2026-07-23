import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle, CheckCircle2, ClipboardCheck, Database, Edit3, FilePlus2, FileText,
  History, Link2, MapPin, Plus, Search, ShieldCheck, Trash2, Upload, X,
} from 'lucide-react'

interface DrawingList {
  id: number; drawingNumber: string; title: string; customer: string; partNumbers: string[]; approvalStatus: string
  currentRevision: string | null; currentRevisionDate: string | null; effectiveDate: string | null; isObsolete: boolean
  physicalMylarLocation: string | null; isMylarCheckedOut: boolean; createdAt: string
}
interface Revision {
  id: number; revisionNumber: string; revisionDate: string; uploadedAt: string; effectiveDate: string | null
  approvalDate: string | null; changeDescription: string; status: string; originalFileName: string; fileType: string
  fileSize: number; fileHash: string; hasPdf: boolean; hasSourceFile: boolean; uploadedBy: string; approvedBy: string | null
  approvalComments: string | null; notes: string | null
}
interface DocumentLink { id: number; kind: string; referenceNumber: string; title: string | null; location: string | null }
interface Audit { id: number; revisionNumber: string | null; action: string; details: string; actor: string; occurredAt: string }
interface Validation { id: number; validationType: string; result: string; notes: string | null; validatedBy: string; validatedAt: string }
interface MylarEvent { id: number; type: string; person: string; purpose: string | null; location: string | null; recordedBy: string; recordedAt: string }
interface DrawingDetail extends DrawingList {
  notes: string | null; fileLocation: string | null; mylarCheckedOutBy: string | null; mylarCheckedOutAt: string | null
  createdBy: string; approvedBy: string | null; approvedAt: string | null; currentApprovedRevisionId: number | null
  revisions: Revision[]; relatedDocuments: DocumentLink[]; validations: Validation[]; mylarHistory: MylarEvent[]; auditHistory: Audit[]
}
interface StorageStatus { configured: boolean; isNetworkPath: boolean; available: boolean; message: string }
interface ReviewItem {
  revisionId: number; drawingId: number; drawingNumber: string; drawingTitle: string; customer: string; revisionNumber: string
  revisionDate: string; uploadedAt: string; uploadedBy: string; changeDescription: string; notes: string | null; hasPdf: boolean
}
interface DeleteTarget { kind: 'drawing' | 'revision'; id: number; label: string; matchValue: string }

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: 'include', ...init })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? body?.detail ?? `Request failed (${response.status}).`)
  }
  return response.status === 204 ? undefined as T : response.json()
}

function uploadWithProgress<T>(url: string, form: FormData, onProgress: (percent: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open('POST', url)
    request.withCredentials = true
    request.upload.onprogress = event => {
      if (event.lengthComputable) onProgress(Math.round((event.loaded / event.total) * 100))
    }
    request.onerror = () => reject(new Error('The upload could not reach the Engineering Hub.'))
    request.onload = () => {
      const body = request.responseText ? JSON.parse(request.responseText) : null
      if (request.status >= 200 && request.status < 300) resolve(body as T)
      else reject(new Error(body?.message ?? body?.detail ?? `Upload failed (${request.status}).`))
    }
    request.send(form)
  })
}

const date = (value: string | null) => value ? new Date(value).toLocaleDateString() : '—'
const commaValues = (value: FormDataEntryValue | null) => String(value ?? '').split(',').map(item => item.trim()).filter(Boolean)
const linksFromForm = (form: FormData) => [
  ...commaValues(form.get('specifications')).map(referenceNumber => ({ kind: 'Specification', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('workOrders')).map(referenceNumber => ({ kind: 'WorkOrder', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('workInstructions')).map(referenceNumber => ({ kind: 'WorkInstruction', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('supplementalDocuments')).map(referenceNumber => ({ kind: 'SupplementalDocument', referenceNumber, title: null, location: null })),
]
const linksOfKind = (drawing: DrawingDetail, kind: string) => drawing.relatedDocuments.filter(link => link.kind === kind).map(link => link.referenceNumber).join(', ')

export default function DrawingWorkspace() {
  const [drawings, setDrawings] = useState<DrawingList[]>([])
  const [selected, setSelected] = useState<DrawingDetail | null>(null)
  const [reviews, setReviews] = useState<ReviewItem[]>([])
  const [query, setQuery] = useState('')
  const [showCreate, setShowCreate] = useState(false)
  const [showEdit, setShowEdit] = useState(false)
  const [showObsolete, setShowObsolete] = useState(false)
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [uploadProgress, setUploadProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reviewComments, setReviewComments] = useState<Record<number, string>>({})
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [deleteAcknowledged, setDeleteAcknowledged] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const [deleteError, setDeleteError] = useState<string | null>(null)

  async function loadList(search = query) {
    const records = await api<DrawingList[]>(`/api/drawings?query=${encodeURIComponent(search)}`)
    setDrawings(records)
  }
  async function loadReviews() { setReviews(await api<ReviewItem[]>('/api/drawing-review-queue')) }
  async function open(id: number) { setSelected(await api<DrawingDetail>(`/api/drawings/${id}`)); setShowEdit(false); setShowObsolete(false) }
  async function refresh() {
    await Promise.all([loadList(), loadReviews()])
    if (selected) await open(selected.id)
  }

  useEffect(() => {
    void Promise.all([
      loadList(''),
      loadReviews(),
      api<StorageStatus>('/api/drawing-storage/status').then(setStorage),
    ]).then(() => {
      const pendingId = Number(sessionStorage.getItem('engineering:open-drawing'))
      if (pendingId) { sessionStorage.removeItem('engineering:open-drawing'); void open(pendingId) }
    }).catch(cause => setError(cause instanceof Error ? cause.message : 'Unable to load drawing control.'))
  }, [])
  useEffect(() => {
    const timer = window.setTimeout(() => void loadList(query).catch(cause => setError(cause.message)), 180)
    return () => window.clearTimeout(timer)
  }, [query])
  useEffect(() => {
    if (!deleteTarget) return
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) closeDeleteDialog() }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [deleteTarget, busy])

  async function createDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const pdf = form.get('pdf') as File | null
    if (pdf?.size && (!String(form.get('revisionNumber') ?? '').trim() || !String(form.get('changeDescription') ?? '').trim())) {
      setError('Revision number and change description are required when an initial PDF is selected.')
      return
    }
    form.set('relatedDocumentsJson', JSON.stringify(linksFromForm(form)))
    setBusy(true); setError(null); setUploadProgress(0)
    try {
      const created = await uploadWithProgress<{ id: number }>('/api/drawings/create-with-revision', form, setUploadProgress)
      event.currentTarget.reset()
      setShowCreate(false)
      await Promise.all([loadList(''), loadReviews()])
      await open(created.id)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to create drawing.') }
    finally { setBusy(false); setUploadProgress(null) }
  }

  async function updateDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    const form = new FormData(event.currentTarget)
    setBusy(true); setError(null)
    try {
      await api(`/api/drawings/${selected.id}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: form.get('title'),
          customer: form.get('customer'),
          partNumbers: commaValues(form.get('partNumbers')),
          notes: form.get('notes'),
          physicalMylarLocation: form.get('mylarLocation'),
          relatedDocuments: linksFromForm(form),
        }),
      })
      setShowEdit(false)
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to update drawing metadata.') }
    finally { setBusy(false) }
  }

  async function uploadRevision(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    setBusy(true); setError(null); setUploadProgress(0)
    try {
      await uploadWithProgress(`/api/drawings/${selected.id}/revisions`, new FormData(event.currentTarget), setUploadProgress)
      event.currentTarget.reset()
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to upload revision.') }
    finally { setBusy(false); setUploadProgress(null) }
  }

  async function setRevisionStatus(revision: Revision | ReviewItem, status: 'Draft' | 'UnderReview') {
    const revisionId = 'id' in revision ? revision.id : revision.revisionId
    setBusy(true); setError(null)
    try {
      await api(`/api/drawing-revisions/${revisionId}/status`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status, comments: reviewComments[revisionId] ?? '' }),
      })
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to update revision status.') }
    finally { setBusy(false) }
  }

  async function approveRevision(revision: Revision | ReviewItem) {
    const revisionId = 'id' in revision ? revision.id : revision.revisionId
    const hasPdf = revision.hasPdf
    if (!hasPdf) { setError('A metadata-only demo revision cannot be approved. Upload a real PDF revision first.'); return }
    setBusy(true); setError(null)
    try {
      await api(`/api/drawing-revisions/${revisionId}/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ effectiveDate: 'effectiveDate' in revision ? revision.effectiveDate : null, comments: reviewComments[revisionId] ?? '' }),
      })
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to approve revision.') }
    finally { setBusy(false) }
  }

  async function obsoleteDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    const form = new FormData(event.currentTarget)
    setBusy(true); setError(null)
    try {
      await api(`/api/drawings/${selected.id}/obsolete`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason: form.get('reason') }),
      })
      setShowObsolete(false)
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to obsolete drawing.') }
    finally { setBusy(false) }
  }

  function requestRevisionDelete(revision: Revision) {
    openDeleteDialog({ kind: 'revision', id: revision.id, label: `Revision ${revision.revisionNumber}`, matchValue: revision.originalFileName })
  }
  function requestDrawingDelete() {
    if (selected) openDeleteDialog({ kind: 'drawing', id: selected.id, label: 'Draft drawing record', matchValue: selected.drawingNumber })
  }
  function openDeleteDialog(target: DeleteTarget) {
    setDeleteTarget(target); setDeleteAcknowledged(false); setDeleteConfirmation(''); setDeleteError(null)
  }
  function closeDeleteDialog() {
    setDeleteTarget(null); setDeleteAcknowledged(false); setDeleteConfirmation(''); setDeleteError(null)
  }
  async function confirmDelete(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!deleteTarget || !deleteAcknowledged || deleteConfirmation !== deleteTarget.matchValue) return
    setBusy(true); setDeleteError(null)
    try {
      const revision = deleteTarget.kind === 'revision'
      await api(revision ? `/api/drawing-revisions/${deleteTarget.id}` : `/api/drawings/${deleteTarget.id}`, {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(revision ? { confirmed: true, fileName: deleteConfirmation } : { confirmed: true, drawingNumber: deleteConfirmation }),
      })
      closeDeleteDialog()
      if (!revision) setSelected(null)
      await refresh()
    } catch (cause) { setDeleteError(cause instanceof Error ? cause.message : 'Unable to permanently delete this item.') }
    finally { setBusy(false) }
  }

  return <div className="drawing-workspace">
    {error && <div className="inline-alert" role="alert">{error}<button type="button" onClick={() => setError(null)}><X size={15}/></button></div>}
    <section className="drawing-toolbar">
      <label className="topbar-search drawing-search"><Search size={15}/><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search drawing, title, customer, part, spec, work order, or notes"/></label>
      {storage && <span className={`storage-mode ${storage.available ? 'available' : 'unavailable'}`} title={storage.message}><Database size={14}/>{storage.isNetworkPath ? 'Network storage' : 'Local test storage'}</span>}
      <button className="button" type="button" onClick={() => setShowCreate(value => !value)}><Plus size={15}/> New drawing</button>
    </section>

    {uploadProgress !== null && <div className="upload-progress" role="status"><span style={{ width: `${uploadProgress}%` }}/><strong>{uploadProgress}%</strong><small>Transferring controlled file package</small></div>}

    {showCreate && <form className="panel record-form" onSubmit={createDrawing}>
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Atomic creation</span><h2>Create drawing and optional initial PDF</h2><p>Without a PDF, this creates a metadata-only Draft. With a PDF, the drawing and revision succeed or roll back together.</p></div></div>
      <div className="form-grid">
        <label>Drawing number<input name="drawingNumber" required/></label><label>Title<input name="title" required/></label>
        <label>Customer<input name="customer" required/></label><label>Linked part numbers<input name="partNumbers" placeholder="PN-1001, PN-1002"/></label>
        <label>Specifications<input name="specifications" placeholder="SPEC-100, SPEC-200"/></label><label>Work orders<input name="workOrders" placeholder="WO-12345"/></label>
        <label>Work instructions<input name="workInstructions" placeholder="WI-100-MFG"/></label><label>Supplemental documents<input name="supplementalDocuments" placeholder="DOC-100-CALC"/></label>
        <label>Physical Mylar location<input name="mylarLocation"/></label><label>Initial PDF<input name="pdf" type="file" accept="application/pdf,.pdf"/></label>
        <label>Initial revision<input name="revisionNumber" placeholder="A"/></label><label>Revision date<input name="revisionDate" type="date"/></label>
        <label>Effective date<input name="effectiveDate" type="date"/></label><label>Original source file<input name="source" type="file"/></label>
        <label className="wide">Change description<textarea name="changeDescription" rows={2}/></label>
        <label className="wide">Drawing notes<textarea name="notes" rows={2}/></label>
        <label className="wide">Revision notes<textarea name="revisionNotes" rows={2}/></label>
      </div>
      <div className="form-actions"><button className="button" disabled={busy}><FilePlus2 size={15}/> Create drawing</button><button className="button ghost" type="button" onClick={() => setShowCreate(false)}>Cancel</button></div>
    </form>}

    {reviews.length > 0 && <section className="panel review-queue">
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Approval work queue</span><h2>{reviews.length} revision{reviews.length === 1 ? '' : 's'} under review</h2></div><ClipboardCheck size={19}/></div>
      <div className="review-queue-list">{reviews.map(item => <article key={item.revisionId} className="review-card"><button type="button" className="review-card-title" onClick={() => void open(item.drawingId)}><strong>{item.drawingNumber} · Rev {item.revisionNumber}</strong><span>{item.customer} · {item.drawingTitle}</span></button><p>{item.changeDescription}</p>{!item.hasPdf && <span className="metadata-only-badge">PDF required before approval</span>}<textarea value={reviewComments[item.revisionId] ?? ''} onChange={event => setReviewComments(current => ({ ...current, [item.revisionId]: event.target.value }))} placeholder="Reviewer comments"/><div className="form-actions"><button className="button ghost" type="button" disabled={busy} onClick={() => void setRevisionStatus(item, 'Draft')}>Return to draft</button><button className="button" type="button" disabled={busy || !item.hasPdf} onClick={() => void approveRevision(item)}><CheckCircle2 size={14}/> Approve</button></div></article>)}</div>
    </section>}

    <section className="drawing-layout">
      <article className="panel drawing-register">
        <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Drawing register</span><h2>{drawings.length} controlled record{drawings.length === 1 ? '' : 's'}</h2></div></div>
        <div className="drawing-list">{drawings.map(item => <button key={item.id} type="button" className={`drawing-row ${selected?.id === item.id ? 'selected' : ''}`} onClick={() => void open(item.id)}><span><strong>{item.drawingNumber}</strong><small>{item.title}</small></span><span><small>{item.customer}</small><small>{item.partNumbers.join(', ') || 'No linked parts'}</small></span><span className={`status-pill status-${item.approvalStatus.toLowerCase()}`}>{item.approvalStatus}</span><span><strong>{item.currentRevision ?? '—'}</strong><small>Current rev</small></span></button>)}</div>
        {!drawings.length && <div className="empty-search-state"><strong>No drawings found</strong><p>Create a drawing or adjust the search.</p></div>}
      </article>

      {!selected ? <article className="panel drawing-empty"><FileText size={30}/><h2>Select a drawing</h2><p>Open a record to manage metadata, references, revisions, review status, and audit history.</p></article> : <article className="drawing-detail">
        <section className="panel detail-summary"><div><span className="eyebrow">{selected.customer}</span><h2>{selected.drawingNumber} · {selected.title}</h2><p>{selected.notes || 'No drawing notes recorded.'}</p></div><div className="detail-summary-actions"><span className={`status-pill status-${selected.approvalStatus.toLowerCase()}`}>{selected.approvalStatus}</span><button className="button ghost" type="button" disabled={selected.isObsolete} onClick={() => setShowEdit(value => !value)}><Edit3 size={14}/> Edit metadata</button>{selected.approvalStatus === 'Draft' && selected.revisions.length === 0 && <button className="button danger" type="button" onClick={requestDrawingDelete}><Trash2 size={14}/> Delete draft</button>}</div></section>

        {showEdit && <form className="panel record-form metadata-edit-form" onSubmit={updateDrawing}>
          <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Audited metadata update</span><h2>Edit drawing record</h2></div></div>
          <div className="form-grid">
            <label>Title<input name="title" defaultValue={selected.title} required/></label><label>Customer<input name="customer" defaultValue={selected.customer} required/></label>
            <label>Part numbers<input name="partNumbers" defaultValue={selected.partNumbers.join(', ')}/></label><label>Mylar location<input name="mylarLocation" defaultValue={selected.physicalMylarLocation ?? ''}/></label>
            <label>Specifications<input name="specifications" defaultValue={linksOfKind(selected, 'Specification')}/></label><label>Work orders<input name="workOrders" defaultValue={linksOfKind(selected, 'WorkOrder')}/></label>
            <label>Work instructions<input name="workInstructions" defaultValue={linksOfKind(selected, 'WorkInstruction')}/></label><label>Supplemental documents<input name="supplementalDocuments" defaultValue={linksOfKind(selected, 'SupplementalDocument')}/></label>
            <label className="wide">Notes<textarea name="notes" rows={3} defaultValue={selected.notes ?? ''}/></label>
          </div>
          <div className="form-actions"><button className="button" disabled={busy}>Save audited changes</button><button className="button ghost" type="button" onClick={() => setShowEdit(false)}>Cancel</button></div>
        </form>}

        <section className="detail-kpis"><div><small>Current revision</small><strong>{selected.revisions.find(revision => revision.id === selected.currentApprovedRevisionId)?.revisionNumber ?? 'None'}</strong></div><div><small>Effective date</small><strong>{date(selected.effectiveDate)}</strong></div><div><small>Linked parts</small><strong>{selected.partNumbers.length}</strong></div><div><small>Mylar</small><strong>{selected.isMylarCheckedOut ? `Out: ${selected.mylarCheckedOutBy ?? 'Unknown'}` : selected.physicalMylarLocation || 'Not tracked'}</strong></div></section>

        <section className="panel linked-records"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Cross references</span><h2>Linked engineering records</h2></div><Link2 size={18}/></div><div className="linked-record-grid"><div><small>Parts</small>{selected.partNumbers.map(item => <button key={item} type="button" onClick={() => setQuery(item)}>{item}</button>)}</div>{['Specification', 'WorkOrder', 'WorkInstruction', 'SupplementalDocument'].map(kind => <div key={kind}><small>{kind.replace(/([A-Z])/g, ' $1').trim()}</small>{selected.relatedDocuments.filter(link => link.kind === kind).map(link => <button key={link.id} type="button" title={link.title ?? undefined} onClick={() => setQuery(link.referenceNumber)}>{link.referenceNumber}</button>)}</div>)}</div></section>

        {selected.currentApprovedRevisionId && selected.revisions.some(revision => revision.id === selected.currentApprovedRevisionId && revision.hasPdf) && <section className="panel pdf-panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Approved PDF</span><h2>Controlled viewer</h2></div><a className="button ghost" href={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file`} target="_blank">Open PDF</a></div><iframe title="Approved drawing PDF" src={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file#toolbar=1`}/></section>}

        {!selected.isObsolete && <form className="panel record-form" onSubmit={uploadRevision}><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Permanent revision record</span><h2>Upload drawing revision</h2></div><Upload size={18}/></div><div className="form-grid"><label>Revision number<input name="revisionNumber" required/></label><label>Revision date<input name="revisionDate" type="date" required/></label><label>Effective date<input name="effectiveDate" type="date"/></label><label>Approved-view PDF<input name="pdf" type="file" accept="application/pdf,.pdf" required/></label><label>Original source file<input name="source" type="file"/></label><label className="wide">Change description<textarea name="changeDescription" required rows={2}/></label><label className="wide">Notes<textarea name="notes" rows={2}/></label></div><button className="button" disabled={busy}><FilePlus2 size={15}/> Store revision</button></form>}

        <section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Permanent history</span><h2>Drawing revisions</h2></div><History size={18}/></div><div className="revision-list">{selected.revisions.map(revision => <div className="revision-row" key={revision.id}><div><strong>Rev {revision.revisionNumber}</strong><small>{revision.changeDescription}</small><small>Uploaded {date(revision.uploadedAt)} by {revision.uploadedBy}{revision.fileHash ? ` · SHA-256 ${revision.fileHash.slice(0, 12)}…` : ''}</small>{!revision.hasPdf && <span className="metadata-only-badge">Metadata-only demo revision</span>}</div><span className={`status-pill status-${revision.status.toLowerCase()}`}>{revision.status}</span><div className="revision-actions">{revision.hasPdf && <a className="button ghost" href={`/api/drawing-revisions/${revision.id}/file`} target="_blank">PDF</a>}{revision.hasSourceFile && <a className="button ghost" href={`/api/drawing-revisions/${revision.id}/source`}>Source</a>}{revision.status === 'Draft' && <button className="button ghost" type="button" disabled={busy || !revision.hasPdf} title={!revision.hasPdf ? 'Upload a real PDF as a new revision before review.' : undefined} onClick={() => void setRevisionStatus(revision, 'UnderReview')}>Submit review</button>}{revision.status === 'UnderReview' && <><textarea className="inline-review-comment" value={reviewComments[revision.id] ?? ''} onChange={event => setReviewComments(current => ({ ...current, [revision.id]: event.target.value }))} placeholder="Review comments"/><button className="button ghost" type="button" disabled={busy} onClick={() => void setRevisionStatus(revision, 'Draft')}>Return</button><button className="button" type="button" disabled={busy || !revision.hasPdf} onClick={() => void approveRevision(revision)}><CheckCircle2 size={14}/> Approve</button></>}{(revision.status === 'Draft' || revision.status === 'UnderReview') && <button className="button danger" type="button" disabled={busy} onClick={() => requestRevisionDelete(revision)}><Trash2 size={14}/> Delete</button>}</div></div>)}</div></section>

        <section className="detail-columns"><section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Physical control</span><h2>Mylar tracking</h2></div><MapPin size={18}/></div><p className="section-copy">Location: {selected.physicalMylarLocation || 'Not recorded'}{selected.mylarCheckedOutBy ? ` · Held by ${selected.mylarCheckedOutBy}` : ''}</p>{selected.mylarHistory.map(item => <div className="history-line" key={item.id}><strong>{item.type}</strong><span>{item.person} · {date(item.recordedAt)}</span></div>)}</section><section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Traceability</span><h2>Validations</h2></div><ShieldCheck size={18}/></div>{selected.validations.length ? selected.validations.map(item => <div className="history-line" key={item.id}><strong>{item.validationType}: {item.result}</strong><span>{item.validatedBy} · {date(item.validatedAt)}</span></div>) : <p className="section-copy">No validation records yet.</p>}</section></section>

        {!selected.isObsolete && <section className="panel obsolete-control"><div><span className="eyebrow">Lifecycle control</span><h2>Obsolete this drawing</h2><p>Preserves all records and permanently closes active revisions.</p></div><button className="button ghost" type="button" onClick={() => setShowObsolete(value => !value)}>Start obsolescence</button>{showObsolete && <form onSubmit={obsoleteDrawing}><label>Required reason<textarea name="reason" required rows={2}/></label><div className="form-actions"><button className="button danger" disabled={busy}>Mark obsolete</button><button className="button ghost" type="button" onClick={() => setShowObsolete(false)}>Cancel</button></div></form>}</section>}

        <section className="panel"><div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Append-only log</span><h2>Complete audit history</h2></div><ShieldCheck size={18}/></div><div className="audit-list">{selected.auditHistory.map(item => <div className="audit-row" key={item.id}><span className="audit-dot"/><div><strong>{item.action}{item.revisionNumber ? ` · Rev ${item.revisionNumber}` : ''}</strong><p>{item.details}</p><small>{item.actor} · {new Date(item.occurredAt).toLocaleString()}</small></div></div>)}</div></section>
      </article>}
    </section>

    {deleteTarget && <div className="delete-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeDeleteDialog() }}><section className="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title" aria-describedby="delete-dialog-description"><header className="delete-dialog-header"><span className="delete-dialog-icon"><AlertTriangle size={21}/></span><div><span className="eyebrow">Permanent deletion</span><h2 id="delete-dialog-title">Delete {deleteTarget.label}?</h2></div><button type="button" className="delete-dialog-close" aria-label="Close deletion dialog" disabled={busy} onClick={closeDeleteDialog}><X size={18}/></button></header><div className="delete-warning" id="delete-dialog-description"><strong>This action cannot be undone.</strong><p>{deleteTarget.kind === 'revision' ? 'The eligible draft revision and any stored file package will be removed.' : 'This empty draft drawing record will be permanently removed.'}</p></div><form className="delete-dialog-form" onSubmit={confirmDelete}><label className="delete-acknowledgment"><input type="checkbox" checked={deleteAcknowledged} onChange={event => setDeleteAcknowledged(event.target.checked)}/><span><strong>I understand this deletion is permanent</strong><small>This is the first required confirmation.</small></span></label><label className="delete-confirmation-field"><span>Type the exact {deleteTarget.kind === 'revision' ? 'filename' : 'drawing number'}</span><code>{deleteTarget.matchValue}</code><input autoFocus autoComplete="off" spellCheck={false} value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} placeholder={deleteTarget.matchValue}/>{deleteConfirmation && deleteConfirmation !== deleteTarget.matchValue && <small className="delete-match-error">The value does not match exactly.</small>}</label>{deleteError && <div className="inline-alert" role="alert">{deleteError}</div>}<div className="delete-dialog-actions"><button type="button" className="button ghost" disabled={busy} onClick={closeDeleteDialog}>Cancel</button><button type="submit" className="button danger" disabled={busy || !deleteAcknowledged || deleteConfirmation !== deleteTarget.matchValue}><Trash2 size={15}/>{busy ? 'Deleting…' : 'Permanently delete'}</button></div></form></section></div>}
  </div>
}
