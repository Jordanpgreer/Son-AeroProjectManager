import { useEffect, useId, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import {
  AlertTriangle, CheckCircle2, ChevronDown, FilePlus2, FileText,
  Trash2, X,
} from 'lucide-react'
import ActionFeedbackDialog from './ActionFeedbackDialog'
import type { ActionFeedback } from './ActionFeedbackDialog'
import { EngineeringDatePicker, FilePicker, RevisionUploadForm } from './EngineeringFormControls'

interface DrawingList {
  id: number; drawingNumber: string; title: string; customer: string; partNumbers: string[]; approvalStatus: string
  currentRevision: string | null; currentRevisionDate: string | null; effectiveDate: string | null; isObsolete: boolean
  physicalMylarLocation: string | null; isMylarCheckedOut: boolean; createdAt: string
  revisionCount: number; attachmentRevisionId: number | null; attachmentFileName: string | null; attachmentStatus: string | null
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
interface DeleteTarget { kind: 'drawing' | 'revision'; id: number; label: string; matchValue: string }
export interface DrawingRecordHeader {
  drawingNumber: string
  title: string
  customer: string
  approvalStatus: string
  currentRevision: string | null
  effectiveDate: string | null
  partNumbers: string[]
  isObsolete: boolean
  isMylarCheckedOut: boolean
  mylarCheckedOutBy: string | null
  physicalMylarLocation: string | null
  notes: string | null
}
interface DrawingWorkspaceProps {
  drawingId: number | null
  initialCreate?: boolean
  onOpenDrawing: (drawingId: number) => void
  onBackToDashboard: () => void
  onRecordChange: (record: DrawingRecordHeader | null) => void
  editRequest: number
}

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
const statusLabel = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2')
const commaValues = (value: FormDataEntryValue | null) => String(value ?? '').split(',').map(item => item.trim()).filter(Boolean)
const linksFromForm = (form: FormData) => [
  ...commaValues(form.get('specifications')).map(referenceNumber => ({ kind: 'Specification', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('workOrders')).map(referenceNumber => ({ kind: 'WorkOrder', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('workInstructions')).map(referenceNumber => ({ kind: 'WorkInstruction', referenceNumber, title: null, location: null })),
  ...commaValues(form.get('supplementalDocuments')).map(referenceNumber => ({ kind: 'SupplementalDocument', referenceNumber, title: null, location: null })),
]
const linksOfKind = (drawing: DrawingDetail, kind: string) => drawing.relatedDocuments.filter(link => link.kind === kind).map(link => link.referenceNumber).join(', ')

type SectionTone = 'steel' | 'gold' | 'graphite' | 'teal' | 'green' | 'red'

interface CollapsibleSectionProps {
  eyebrow: string
  title: string
  tone: SectionTone
  defaultOpen?: boolean
  className?: string
  children: ReactNode
}

function CollapsibleSection({
  eyebrow,
  title,
  tone,
  defaultOpen = true,
  className = '',
  children,
}: CollapsibleSectionProps) {
  const [open, setOpen] = useState(defaultOpen)
  const contentId = useId()

  return <section className={`panel collapsible-section section-tone-${tone} ${open ? 'is-open' : 'is-collapsed'} ${className}`.trim()}>
    <button
      type="button"
      className="section-titlebar"
      aria-expanded={open}
      aria-controls={contentId}
      onClick={() => setOpen(value => !value)}
    >
      <span className="section-titlebar-copy">
        <span className="eyebrow">{eyebrow}</span>
        <span className="section-title" role="heading" aria-level={2}>{title}</span>
      </span>
      <span className="section-titlebar-tools" aria-hidden="true">
        <ChevronDown className="section-chevron" size={17}/>
      </span>
    </button>
    <div id={contentId} className="section-content" hidden={!open}>{children}</div>
  </section>
}

export default function DrawingWorkspace({
  drawingId,
  initialCreate = false,
  onOpenDrawing,
  onBackToDashboard,
  onRecordChange,
  editRequest,
}: DrawingWorkspaceProps) {
  const [selected, setSelected] = useState<DrawingDetail | null>(null)
  const [showCreate, setShowCreate] = useState(initialCreate)
  const [recordLoading, setRecordLoading] = useState(false)
  const [showEdit, setShowEdit] = useState(false)
  const [showObsolete, setShowObsolete] = useState(false)
  const [busy, setBusy] = useState(false)
  const [uploadProgress, setUploadProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<ActionFeedback | null>(null)
  const [reviewComments, setReviewComments] = useState<Record<number, string>>({})
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [deleteAcknowledged, setDeleteAcknowledged] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const handledEditRequest = useRef(0)

  async function open(id: number) {
    setSelected(await api<DrawingDetail>(`/api/drawings/${id}`))
    setShowEdit(false)
    setShowObsolete(false)
  }
  async function refresh() {
    if (selected) await open(selected.id)
  }

  useEffect(() => {
    if (drawingId) {
      setSelected(null)
      setShowCreate(false)
      setRecordLoading(true)
      void open(drawingId)
        .catch(cause => setError(cause instanceof Error ? cause.message : 'Unable to open drawing record.'))
        .finally(() => setRecordLoading(false))
    } else {
      setSelected(null)
      setRecordLoading(false)
      setShowCreate(initialCreate)
    }
  }, [drawingId, initialCreate])
  useEffect(() => {
    onRecordChange(selected ? {
      drawingNumber: selected.drawingNumber,
      title: selected.title,
      customer: selected.customer,
      approvalStatus: selected.approvalStatus,
      currentRevision: selected.currentRevision,
      effectiveDate: selected.effectiveDate,
      partNumbers: selected.partNumbers,
      isObsolete: selected.isObsolete,
      isMylarCheckedOut: selected.isMylarCheckedOut,
      mylarCheckedOutBy: selected.mylarCheckedOutBy,
      physicalMylarLocation: selected.physicalMylarLocation,
      notes: selected.notes,
    } : null)
  }, [onRecordChange, selected])
  useEffect(() => {
    if (editRequest <= handledEditRequest.current) {
      handledEditRequest.current = editRequest
      return
    }
    handledEditRequest.current = editRequest
    if (selected && !selected.isObsolete) setShowEdit(true)
  }, [editRequest, selected])
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
      onOpenDrawing(created.id)
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
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const revisionNumber = String(form.get('revisionNumber') ?? '').trim()
    const revisionDate = String(form.get('revisionDate') ?? '').trim()
    const changeDescription = String(form.get('changeDescription') ?? '').trim()
    const pdf = form.get('pdf')
    const missingFields = [
      !revisionNumber && 'revision number',
      !revisionDate && 'revision date',
      (!(pdf instanceof File) || pdf.size === 0) && 'approved-view PDF',
      !changeDescription && 'change description',
    ].filter(Boolean) as string[]
    if (missingFields.length) {
      setFeedback({
        kind: 'error',
        title: 'Complete the required revision details',
        message: `Add ${missingFields.join(', ')} before storing this controlled revision.`,
      })
      return
    }
    setBusy(true); setError(null); setUploadProgress(0)
    try {
      await uploadWithProgress(`/api/drawings/${selected.id}/revisions`, form, setUploadProgress)
      formElement.reset()
      await refresh()
      setFeedback({
        kind: 'success',
        title: `Revision ${revisionNumber} stored`,
        message: 'The controlled revision and its file package were added to the permanent drawing history.',
      })
    } catch (cause) {
      setFeedback({
        kind: 'error',
        title: 'Revision was not stored',
        message: cause instanceof Error ? cause.message : 'Unable to upload revision.',
      })
    }
    finally { setBusy(false); setUploadProgress(null) }
  }

  async function setRevisionStatus(revision: Revision, status: 'Draft' | 'UnderReview') {
    const revisionId = revision.id
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

  async function approveRevision(revision: Revision) {
    const revisionId = revision.id
    const hasPdf = revision.hasPdf
    if (!hasPdf) { setError('A metadata-only demo revision cannot be approved. Upload a real PDF revision first.'); return }
    setBusy(true); setError(null)
    try {
      await api(`/api/drawing-revisions/${revisionId}/approve`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ effectiveDate: revision.effectiveDate, comments: reviewComments[revisionId] ?? '' }),
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
      if (!revision) {
        setSelected(null)
        onBackToDashboard()
      } else {
        await refresh()
      }
    } catch (cause) { setDeleteError(cause instanceof Error ? cause.message : 'Unable to permanently delete this item.') }
    finally { setBusy(false) }
  }

  return <div className="drawing-workspace">
    {error && <div className="inline-alert" role="alert">{error}<button type="button" onClick={() => setError(null)}><X size={15}/></button></div>}

    {uploadProgress !== null && <div className="upload-progress" role="status"><span style={{ width: `${uploadProgress}%` }}/><strong>{uploadProgress}%</strong><small>Transferring controlled file package</small></div>}

    {showCreate && <form className="panel record-form" onSubmit={createDrawing}>
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Atomic creation</span><h2>Create drawing and optional initial PDF</h2><p>Without a PDF, this creates a metadata-only Draft. With a PDF, the drawing and revision succeed or roll back together.</p></div></div>
      <div className="form-grid">
        <label>Drawing number<input name="drawingNumber" required/></label><label>Title<input name="title" required/></label>
        <label>Customer<input name="customer" required/></label><label>Linked part numbers<input name="partNumbers" placeholder="PN-1001, PN-1002"/></label>
        <label>Specifications<input name="specifications" placeholder="SPEC-100, SPEC-200"/></label><label>Work orders<input name="workOrders" placeholder="WO-12345"/></label>
        <label>Work instructions<input name="workInstructions" placeholder="WI-100-MFG"/></label><label>Supplemental documents<input name="supplementalDocuments" placeholder="DOC-100-CALC"/></label>
        <label>Physical Mylar location<input name="mylarLocation"/></label><FilePicker name="pdf" label="Initial PDF" accept="application/pdf,.pdf"/>
        <label>Initial revision<input name="revisionNumber" placeholder="A"/></label><EngineeringDatePicker name="revisionDate" label="Revision date"/>
        <EngineeringDatePicker name="effectiveDate" label="Effective date"/><FilePicker name="source" label="Original source file"/>
        <label className="wide">Change description<textarea name="changeDescription" rows={2}/></label>
        <label className="wide">Drawing notes<textarea name="notes" rows={2}/></label>
        <label className="wide">Revision notes<textarea name="revisionNotes" rows={2}/></label>
      </div>
      <div className="form-actions"><button className="button" disabled={busy}><FilePlus2 size={15}/> Create drawing</button><button className="button ghost" type="button" onClick={() => { setShowCreate(false); if (!selected) onBackToDashboard() }}>Cancel</button></div>
    </form>}

    {recordLoading ? <article className="panel skeleton-panel drawing-record-loading" aria-label="Loading drawing record">
      <div className="skeleton-line lg"/>
      <div className="skeleton-line"/>
      <div className="skeleton-line" style={{ width: '58%' }}/>
    </article> : !selected && !showCreate ? <article className="panel drawing-empty"><FileText size={30}/><h2>No drawing selected</h2><p>Return to the drawing register to search and open a controlled record.</p><button className="button ghost" type="button" onClick={onBackToDashboard}>Open drawing register</button></article> : selected ? <article className="drawing-detail">
        {selected.approvalStatus === 'Draft' && selected.revisions.length === 0 && <div className="record-destructive-actions"><button className="button danger" type="button" onClick={requestDrawingDelete}><Trash2 size={14}/> Delete draft</button></div>}

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

        <CollapsibleSection eyebrow="Cross references" title="Linked engineering records" tone="steel" className="linked-records">
          <div className="linked-record-grid"><div><small>Parts</small>{selected.partNumbers.map(item => <span className="linked-record-chip" key={item}>{item}</span>)}</div>{['Specification', 'WorkOrder', 'WorkInstruction', 'SupplementalDocument'].map(kind => <div key={kind}><small>{kind.replace(/([A-Z])/g, ' $1').trim()}</small>{selected.relatedDocuments.filter(link => link.kind === kind).map(link => <span className="linked-record-chip" key={link.id} title={link.title ?? undefined}>{link.referenceNumber}</span>)}</div>)}</div>
        </CollapsibleSection>

        {selected.currentApprovedRevisionId && selected.revisions.some(revision => revision.id === selected.currentApprovedRevisionId && revision.hasPdf) && <CollapsibleSection eyebrow="Approved PDF" title="Controlled viewer" tone="steel" className="pdf-panel">
          <div className="section-inline-actions"><a className="button ghost" href={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file`} target="_blank">Open PDF</a></div>
          <iframe title="Approved drawing PDF" src={`/api/drawing-revisions/${selected.currentApprovedRevisionId}/file#toolbar=1`}/>
        </CollapsibleSection>}

        {!selected.isObsolete && <CollapsibleSection eyebrow="Permanent revision record" title="Upload drawing revision" tone="gold" defaultOpen={false}>
          <RevisionUploadForm busy={busy} onSubmit={uploadRevision}/>
        </CollapsibleSection>}

        <CollapsibleSection eyebrow="Permanent history" title="Drawing revisions" tone="graphite">
          <div className="revision-list">{selected.revisions.map(revision => <div className="revision-row" key={revision.id}>
            <div className="revision-info">
              <div className="revision-heading">
                <strong>Rev {revision.revisionNumber}</strong>
                <span className={`revision-state revision-state-${revision.status.toLowerCase()}`}>
                  <i aria-hidden="true"/>
                  {statusLabel(revision.status)}
                </span>
              </div>
              <small>{revision.changeDescription}</small>
              <small>Uploaded {date(revision.uploadedAt)} by {revision.uploadedBy}{revision.fileHash && <> · SHA-256 <span className="technical-id">{revision.fileHash.slice(0, 12)}…</span></>}</small>
              {!revision.hasPdf && <span className="revision-file-state"><AlertTriangle size={12}/> PDF required before approval</span>}
            </div>
            <div className="revision-review-area">
              {revision.status === 'UnderReview' && <label className="revision-comment-field">
                <span>Reviewer comment</span>
                <textarea className="inline-review-comment" value={reviewComments[revision.id] ?? ''} onChange={event => setReviewComments(current => ({ ...current, [revision.id]: event.target.value }))} placeholder="Add a review comment"/>
              </label>}
              <div className="revision-actions">
                {revision.hasPdf && <a className="button ghost" href={`/api/drawing-revisions/${revision.id}/file`} target="_blank">PDF</a>}
                {revision.hasSourceFile && <a className="button ghost" href={`/api/drawing-revisions/${revision.id}/source`}>Source</a>}
                {revision.status === 'Draft' && <button className="button ghost" type="button" disabled={busy || !revision.hasPdf} title={!revision.hasPdf ? 'Upload a real PDF as a new revision before review.' : undefined} onClick={() => void setRevisionStatus(revision, 'UnderReview')}>Submit review</button>}
                {revision.status === 'UnderReview' && <>
                  <button className="button ghost" type="button" disabled={busy} onClick={() => void setRevisionStatus(revision, 'Draft')}>Return</button>
                  <button className="button" type="button" disabled={busy || !revision.hasPdf} onClick={() => void approveRevision(revision)}><CheckCircle2 size={14}/> Approve</button>
                </>}
                {(revision.status === 'Draft' || revision.status === 'UnderReview') && <button className="button danger" type="button" disabled={busy} onClick={() => requestRevisionDelete(revision)}><Trash2 size={14}/> Delete</button>}
              </div>
            </div>
          </div>)}</div>
        </CollapsibleSection>

        <section className="detail-columns">
          <CollapsibleSection eyebrow="Physical control" title="Mylar tracking" tone="teal" defaultOpen={false}><p className="section-copy">Location: {selected.physicalMylarLocation || 'Not recorded'}{selected.mylarCheckedOutBy ? ` · Held by ${selected.mylarCheckedOutBy}` : ''}</p>{selected.mylarHistory.map(item => <div className="history-line" key={item.id}><strong>{item.type}</strong><span>{item.person} · {date(item.recordedAt)}</span></div>)}</CollapsibleSection>
          <CollapsibleSection eyebrow="Traceability" title="Validations" tone="green" defaultOpen={false}>{selected.validations.length ? selected.validations.map(item => <div className="history-line" key={item.id}><strong>{item.validationType}: {item.result}</strong><span>{item.validatedBy} · {date(item.validatedAt)}</span></div>) : <p className="section-copy">No validation records yet.</p>}</CollapsibleSection>
        </section>

        {!selected.isObsolete && <CollapsibleSection eyebrow="Lifecycle control" title="Obsolete this drawing" tone="red" defaultOpen={false} className="obsolete-control">
          <p>Preserves all records and permanently closes active revisions.</p><button className="button ghost" type="button" onClick={() => setShowObsolete(value => !value)}>Start obsolescence</button>{showObsolete && <form onSubmit={obsoleteDrawing}><label>Required reason<textarea name="reason" required rows={2}/></label><div className="form-actions"><button className="button danger" disabled={busy}>Mark obsolete</button><button className="button ghost" type="button" onClick={() => setShowObsolete(false)}>Cancel</button></div></form>}
        </CollapsibleSection>}

        <CollapsibleSection eyebrow="Append-only log" title="Complete audit history" tone="graphite" defaultOpen={false}>
          <div className="audit-list">{selected.auditHistory.map(item => <div className="audit-row" key={item.id}><span className="audit-dot"/><div><strong>{item.action}{item.revisionNumber ? ` · Rev ${item.revisionNumber}` : ''}</strong><p>{item.details}</p><small>{item.actor} · {new Date(item.occurredAt).toLocaleString()}</small></div></div>)}</div>
        </CollapsibleSection>
      </article> : null}

    {feedback && <ActionFeedbackDialog feedback={feedback} onClose={() => setFeedback(null)}/>}
    {deleteTarget && <div className="delete-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeDeleteDialog() }}><section className="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title" aria-describedby="delete-dialog-description"><header className="delete-dialog-header"><span className="delete-dialog-icon"><AlertTriangle size={21}/></span><div><span className="eyebrow">Permanent deletion</span><h2 id="delete-dialog-title">Delete {deleteTarget.label}?</h2></div><button type="button" className="delete-dialog-close" aria-label="Close deletion dialog" disabled={busy} onClick={closeDeleteDialog}><X size={18}/></button></header><div className="delete-warning" id="delete-dialog-description"><strong>This action cannot be undone.</strong><p>{deleteTarget.kind === 'revision' ? 'The eligible draft revision and any stored file package will be removed.' : 'This empty draft drawing record will be permanently removed.'}</p></div><form className="delete-dialog-form" onSubmit={confirmDelete}><label className="delete-acknowledgment"><input type="checkbox" checked={deleteAcknowledged} onChange={event => setDeleteAcknowledged(event.target.checked)}/><span><strong>I understand this deletion is permanent</strong><small>This is the first required confirmation.</small></span></label><label className="delete-confirmation-field"><span>Type the exact {deleteTarget.kind === 'revision' ? 'filename' : 'drawing number'}</span><code>{deleteTarget.matchValue}</code><input autoFocus autoComplete="off" spellCheck={false} value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} placeholder={deleteTarget.matchValue}/>{deleteConfirmation && deleteConfirmation !== deleteTarget.matchValue && <small className="delete-match-error">The value does not match exactly.</small>}</label>{deleteError && <div className="inline-alert" role="alert">{deleteError}</div>}<div className="delete-dialog-actions"><button type="button" className="button ghost" disabled={busy} onClick={closeDeleteDialog}>Cancel</button><button type="submit" className="button danger" disabled={busy || !deleteAcknowledged || deleteConfirmation !== deleteTarget.matchValue}><Trash2 size={15}/>{busy ? 'Deleting…' : 'Permanently delete'}</button></div></form></section></div>}
  </div>
}
