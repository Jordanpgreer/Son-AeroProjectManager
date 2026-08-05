import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle, Archive, ArrowRight, CalendarDays, CheckCircle2, ChevronDown, FilePlus2, FileText,
  FolderOpen, History, LogIn, LogOut, MapPin, Pencil, Trash2, UserRound, X,
} from 'lucide-react'
import ActionFeedbackDialog from './ActionFeedbackDialog'
import type { ActionFeedback } from './ActionFeedbackDialog'
import { DRAWING_FILE_ACCEPT, SUPPLEMENTAL_FILE_ACCEPT, EngineeringDatePicker, FilePicker, RevisionUploadForm } from './EngineeringFormControls'
import EngineeringTagEditor from './EngineeringTagEditor'
import { engineeringPermissionKeys, hasEngineeringPermission } from './permissions'

interface DrawingList {
  id: number; drawingNumber: string; title: string; customer: string; partNumbers: string[]; approvalStatus: string
  currentRevision: string | null; currentRevisionDate: string | null; effectiveDate: string | null; isObsolete: boolean
  physicalMylarLocation: string | null; isMylarCheckedOut: boolean; mylarCount: number; checkedOutMylarCount: number; createdAt: string
  revisionCount: number; attachmentRevisionId: number | null; attachmentFileName: string | null; attachmentStatus: string | null
}
interface Revision {
  id: number; revisionNumber: string; revisionDate: string; uploadedAt: string; effectiveDate: string | null
  approvalDate: string | null; changeDescription: string; status: string; originalFileName: string; fileType: string
  fileSize: number; fileHash: string; hasPdf: boolean; controlledFilePath: string | null; hasSourceFile: boolean
  uploadedBy: string; approvedBy: string | null
  approvalComments: string | null; notes: string | null
}
interface DocumentLink { id: number; drawingRevisionId: number | null; kind: string; referenceNumber: string; title: string | null; location: string | null }
interface Audit { id: number; revisionNumber: string | null; action: string; details: string; actor: string; occurredAt: string }
interface DrawingMetadataSnapshot {
  Title?: string | null
  Customer?: string | null
  Parts?: string[]
  Notes?: string | null
  PhysicalMylarLocation?: string | null
  Links?: string[]
}
interface AuditMetadataChange { label: string; before: string; after: string }
interface DrawingMylar {
  id: number; mylarNumber: string; isCheckedOut: boolean; currentLocation: string | null
  checkedOutBy: string | null; checkedOutAt: string | null; createdBy: string; createdAt: string; movementCount: number
}
interface MylarEvent {
  id: number; mylarId: number | null; mylarNumber: string; type: string; actor: string
  note: string | null; location: string | null; recordedAt: string
}
interface DrawingDetail extends DrawingList {
  notes: string | null; fileLocation: string | null; mylarCheckedOutBy: string | null; mylarCheckedOutAt: string | null
  createdBy: string; approvedBy: string | null; approvedAt: string | null; currentApprovedRevisionId: number | null
  revisions: Revision[]; relatedDocuments: DocumentLink[]; mylars: DrawingMylar[]; mylarHistory: MylarEvent[]; auditHistory: Audit[]
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
  mylarCount: number
  checkedOutMylarCount: number
  notes: string | null
  pendingReviewRevision: string | null
  isMetadataEditing: boolean
}
interface DrawingWorkspaceProps {
  drawingId: number | null
  initialCreate?: boolean
  onOpenDrawing: (drawingId: number) => void
  onBackToDashboard: () => void
  onRecordChange: (record: DrawingRecordHeader | null) => void
  editRequest: number
  archiveRequest: number
  auditRequest: number
  actorName: string
  permissions: string[]
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
const dateInputValue = (value: string | null) => value ? value.slice(0, 10) : ''
const statusLabel = (value: string) => value === 'Obsolete' ? 'Archived' : value.replace(/([a-z])([A-Z])/g, '$1 $2')
const auditActionLabel = (value: string) => (
  value === 'DrawingObsoleted' ? 'DrawingArchived' :
  value === 'MylarReturned' ? 'MylarCheckedIn' :
  value
)
  .replace(/([a-z])([A-Z])/g, '$1 $2')
const auditDetails = (item: Audit) => {
  if (item.action === 'DrawingObsoleted') {
    return item.details.replace(/^Drawing marked obsolete\./i, 'Drawing archived.')
  }
  if (item.action === 'MylarReturned') {
    return item.details.replace(/Physical Mylar returned by/i, 'Physical Mylar checked in by')
  }
  return item.details
}
const mylarTypeLabel = (value: string) => value === 'Returned'
  ? 'Checked in'
  : value.replace(/([a-z])([A-Z])/g, '$1 $2')
const auditTimestamp = (value: string) => {
  const timestamp = new Date(value)
  return {
    date: timestamp.toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric' }),
    time: timestamp.toLocaleTimeString(undefined, {
      hour: 'numeric',
      minute: '2-digit',
      second: '2-digit',
      timeZoneName: 'short',
    }),
  }
}
const readableValue = (value: string | null | undefined) => value?.trim() || 'Not set'
const readableList = (value: string[] | undefined) => value?.length ? value.join(', ') : 'None'
const controlledFolderHref = (filePath: string | null) => {
  if (!filePath) return null
  const folderPath = filePath.replace(/[\\/][^\\/]+$/, '')
  return `sonaero-folder://open?path=${encodeURIComponent(folderPath)}`
}
const linkReferences = (links: string[] | undefined, kind: string) => (links ?? [])
  .map(link => link.split(':'))
  .filter(parts => parts[0] === kind && parts[1])
  .map(parts => parts[1])

function extractJsonObjects(value: string) {
  const objects: string[] = []
  let start = -1
  let depth = 0
  let inString = false
  let escaped = false

  for (let index = 0; index < value.length; index += 1) {
    const character = value[index]
    if (inString) {
      if (escaped) escaped = false
      else if (character === '\\') escaped = true
      else if (character === '"') inString = false
      continue
    }
    if (character === '"') {
      inString = true
      continue
    }
    if (character === '{') {
      if (depth === 0) start = index
      depth += 1
    } else if (character === '}' && depth > 0) {
      depth -= 1
      if (depth === 0 && start >= 0) {
        objects.push(value.slice(start, index + 1))
        start = -1
      }
    }
  }
  return objects
}

function metadataSnapshots(details: string): [DrawingMetadataSnapshot, DrawingMetadataSnapshot] | null {
  try {
    const structured = JSON.parse(details) as {
      schema?: string
      before?: DrawingMetadataSnapshot
      after?: DrawingMetadataSnapshot
    }
    if (structured.schema === 'DrawingMetadataChange/v1' && structured.before && structured.after) {
      return [structured.before, structured.after]
    }
  } catch {
    // Older records embed two JSON snapshots in a sentence.
  }

  const objects = extractJsonObjects(details)
  if (objects.length < 2) return null
  try {
    return [
      JSON.parse(objects[0]) as DrawingMetadataSnapshot,
      JSON.parse(objects[1]) as DrawingMetadataSnapshot,
    ]
  } catch {
    return null
  }
}

function metadataChanges(item: Audit): AuditMetadataChange[] | null {
  if (item.action !== 'DrawingMetadataUpdated') return null
  const snapshots = metadataSnapshots(item.details)
  if (!snapshots) return null
  const [before, after] = snapshots
  const fields = [
    { label: 'Title', before: readableValue(before.Title), after: readableValue(after.Title) },
    { label: 'Design authority', before: readableValue(before.Customer), after: readableValue(after.Customer) },
    { label: 'Part numbers', before: readableList(before.Parts), after: readableList(after.Parts) },
    { label: 'Notes', before: readableValue(before.Notes), after: readableValue(after.Notes) },
    { label: 'Mylar location', before: readableValue(before.PhysicalMylarLocation), after: readableValue(after.PhysicalMylarLocation) },
    { label: 'Specifications', before: readableList(linkReferences(before.Links, 'Specification')), after: readableList(linkReferences(after.Links, 'Specification')) },
    { label: 'Work orders', before: readableList(linkReferences(before.Links, 'WorkOrder')), after: readableList(linkReferences(after.Links, 'WorkOrder')) },
    { label: 'Work instructions', before: readableList(linkReferences(before.Links, 'WorkInstruction')), after: readableList(linkReferences(after.Links, 'WorkInstruction')) },
    { label: 'Supporting documents', before: readableList(linkReferences(before.Links, 'SupplementalDocument')), after: readableList(linkReferences(after.Links, 'SupplementalDocument')) },
  ]
  return fields.filter(field => field.before !== field.after)
}
const commaValues = (value: FormDataEntryValue | null) => String(value ?? '').split(',').map(item => item.trim()).filter(Boolean)
const linksFromForm = (form: FormData) =>
  commaValues(form.get('specifications')).map(referenceNumber => ({ kind: 'Specification', referenceNumber, title: null, location: null }))
const linkValuesOfKind = (drawing: DrawingDetail, kind: string) => drawing.relatedDocuments.filter(link => link.kind === kind).map(link => link.referenceNumber)

export default function DrawingWorkspace({
  drawingId,
  initialCreate = false,
  onOpenDrawing,
  onBackToDashboard,
  onRecordChange,
  editRequest,
  archiveRequest,
  auditRequest,
  actorName,
  permissions,
}: DrawingWorkspaceProps) {
  const can = (permission: string) => hasEngineeringPermission(permissions, permission)
  const canCreateDrawing = can(engineeringPermissionKeys.drawingCreate)
  const canEditDrawingMetadata = can(engineeringPermissionKeys.drawingMetadataEdit)
  const canEditSpecifications = can(engineeringPermissionKeys.specificationsEdit)
  const canOpenMetadataEditor = canEditDrawingMetadata || canEditSpecifications
  const canArchiveDrawing = can(engineeringPermissionKeys.drawingArchive)
  const canDeleteDrawing = can(engineeringPermissionKeys.drawingDelete)
  const canCreateRevision = can(engineeringPermissionKeys.revisionCreate)
  const canEditRevision = can(engineeringPermissionKeys.revisionEdit)
  const canSubmitRevision = can(engineeringPermissionKeys.revisionSubmit)
  const canApproveRevision = can(engineeringPermissionKeys.revisionApprove)
  const canMakeRevisionCurrent = can(engineeringPermissionKeys.revisionMakeCurrent)
  const canDeleteRevision = can(engineeringPermissionKeys.revisionDelete)
  const canViewSpecifications = can(engineeringPermissionKeys.specificationsView)
  const canViewSupportingDocuments = can(engineeringPermissionKeys.supportingDocumentsView)
  const canManageSupportingDocuments = can(engineeringPermissionKeys.supportingDocumentsManage)
  const canViewMylar = can(engineeringPermissionKeys.mylarView)
  const canManageMylar = can(engineeringPermissionKeys.mylarManage)
  const canViewAudit = can(engineeringPermissionKeys.auditView)
  const [selected, setSelected] = useState<DrawingDetail | null>(null)
  const [showCreate, setShowCreate] = useState(initialCreate)
  const [recordLoading, setRecordLoading] = useState(false)
  const [showEdit, setShowEdit] = useState(false)
  const [showArchive, setShowArchive] = useState(false)
  const [showRevisionUpload, setShowRevisionUpload] = useState(false)
  const [showMylarCustody, setShowMylarCustody] = useState(false)
  const [activeRevisionId, setActiveRevisionId] = useState<number | null>(null)
  const [previewRevisionId, setPreviewRevisionId] = useState<number | null>(null)
  const [activateTarget, setActivateTarget] = useState<Revision | null>(null)
  const [editRevisionTarget, setEditRevisionTarget] = useState<Revision | null>(null)
  const [revisionSaveIntent, setRevisionSaveIntent] = useState<'draft' | 'approval' | null>(null)
  const [revisionEditError, setRevisionEditError] = useState<string | null>(null)
  const [archiveReason, setArchiveReason] = useState('')
  const [auditOpen, setAuditOpen] = useState(false)
  const [mylarMessage, setMylarMessage] = useState<string | null>(null)
  const [mylarError, setMylarError] = useState<string | null>(null)
  const [supplementalError, setSupplementalError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [uploadProgress, setUploadProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<ActionFeedback | null>(null)
  const [designAuthorities, setDesignAuthorities] = useState<string[]>([])
  const [designAuthoritiesLoading, setDesignAuthoritiesLoading] = useState(false)
  const [designAuthorityError, setDesignAuthorityError] = useState<string | null>(null)
  const [reviewComments, setReviewComments] = useState<Record<number, string>>({})
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [deleteAcknowledged, setDeleteAcknowledged] = useState(false)
  const [deleteConfirmation, setDeleteConfirmation] = useState('')
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const handledEditRequest = useRef(0)
  const handledArchiveRequest = useRef(0)
  const handledAuditRequest = useRef(0)
  const auditDrawerRef = useRef<HTMLElement | null>(null)
  const mylarDrawerRef = useRef<HTMLElement | null>(null)
  const revisionUploadDialogRef = useRef<HTMLElement | null>(null)
  const openDrawingIdRef = useRef<number | null>(null)

  useEffect(() => {
    if (!canCreateDrawing && !canEditDrawingMetadata) return
    let active = true
    setDesignAuthoritiesLoading(true)
    setDesignAuthorityError(null)
    void api<string[]>('/api/design-authorities')
      .then(authorities => { if (active) setDesignAuthorities(authorities) })
      .catch(cause => {
        if (active) setDesignAuthorityError(cause instanceof Error
          ? cause.message
          : 'Unable to load approved Design Authorities.')
      })
      .finally(() => { if (active) setDesignAuthoritiesLoading(false) })
    return () => { active = false }
  }, [canCreateDrawing, canEditDrawingMetadata])

  async function open(id: number) {
    const drawing = await api<DrawingDetail>(`/api/drawings/${id}`)
    const sameDrawing = openDrawingIdRef.current === id
    const defaultRevisionId = drawing.revisions.find(revision => revision.status === 'UnderReview')?.id
      ?? drawing.currentApprovedRevisionId
      ?? drawing.revisions[0]?.id
      ?? null
    openDrawingIdRef.current = id
    setSelected(drawing)
    setActiveRevisionId(current => sameDrawing && current && drawing.revisions.some(revision => revision.id === current)
      ? current
      : defaultRevisionId)
    setPreviewRevisionId(current => sameDrawing && current && drawing.revisions.some(revision => revision.id === current && revision.hasPdf)
      ? current
      : null)
    setShowEdit(false)
    setShowArchive(false)
    setActivateTarget(null)
    setEditRevisionTarget(null)
    setRevisionSaveIntent(null)
    setRevisionEditError(null)
    setArchiveReason('')
    setAuditOpen(false)
    setMylarMessage(null)
    setMylarError(null)
    setSupplementalError(null)
  }
  async function refresh() {
    if (selected) await open(selected.id)
  }

  useEffect(() => {
    if (drawingId) {
      setSelected(null)
      setShowCreate(false)
      setShowRevisionUpload(false)
      setShowMylarCustody(false)
      setActiveRevisionId(null)
      setPreviewRevisionId(null)
      setRecordLoading(true)
      void open(drawingId)
        .catch(cause => setError(cause instanceof Error ? cause.message : 'Unable to open drawing record.'))
        .finally(() => setRecordLoading(false))
    } else {
      openDrawingIdRef.current = null
      setSelected(null)
      setRecordLoading(false)
      setShowCreate(initialCreate && canCreateDrawing)
    }
  }, [drawingId, initialCreate, canCreateDrawing])
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
      mylarCount: selected.mylarCount,
      checkedOutMylarCount: selected.checkedOutMylarCount,
      notes: selected.notes,
      pendingReviewRevision: selected.isObsolete
        ? null
        : selected.revisions.find(revision => revision.status === 'UnderReview')?.revisionNumber ?? null,
      isMetadataEditing: showEdit,
    } : null)
  }, [onRecordChange, selected, showEdit])
  useEffect(() => {
    if (editRequest <= handledEditRequest.current) {
      handledEditRequest.current = editRequest
      return
    }
    handledEditRequest.current = editRequest
    if (canOpenMetadataEditor && selected && !selected.isObsolete) {
      setShowEdit(current => {
        const next = !current
        if (next) {
          window.setTimeout(() => document.querySelector<HTMLElement>('.metadata-edit-form input')?.focus(), 0)
        }
        return next
      })
    }
  }, [canOpenMetadataEditor, editRequest, selected])
  useEffect(() => {
    if (archiveRequest <= handledArchiveRequest.current) {
      handledArchiveRequest.current = archiveRequest
      return
    }
    handledArchiveRequest.current = archiveRequest
    if (canArchiveDrawing && selected && !selected.isObsolete) setShowArchive(true)
  }, [archiveRequest, canArchiveDrawing, selected])
  useEffect(() => {
    if (auditRequest <= handledAuditRequest.current) {
      handledAuditRequest.current = auditRequest
      return
    }
    handledAuditRequest.current = auditRequest
    if (canViewAudit && selected) setAuditOpen(true)
  }, [auditRequest, canViewAudit, selected])
  useEffect(() => {
    if (!showArchive && !activateTarget && !editRevisionTarget && !auditOpen && !showRevisionUpload && !showMylarCustody) return
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const close = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) {
        if (showRevisionUpload) closeRevisionUpload()
        else if (showMylarCustody) closeMylarCustody()
        else if (showArchive) closeArchiveDialog()
        else if (activateTarget) closeActivationDialog()
        else if (editRevisionTarget) closeRevisionEditor()
        else if (auditOpen) closeAuditDrawer()
        return
      }
      if (event.key !== 'Tab') return
      const focusContainer = showRevisionUpload
        ? revisionUploadDialogRef.current
        : showMylarCustody
          ? mylarDrawerRef.current
          : auditOpen
            ? auditDrawerRef.current
            : null
      if (!focusContainer) return
      const focusable = Array.from(focusContainer.querySelectorAll<HTMLElement>(
        'button:not(:disabled), a[href], input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex="-1"])',
      ))
      if (!focusable.length) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    window.addEventListener('keydown', close)
    return () => {
      document.body.style.overflow = previousOverflow
      window.removeEventListener('keydown', close)
    }
  }, [activateTarget, auditOpen, busy, editRevisionTarget, showArchive, showMylarCustody, showRevisionUpload])
  useEffect(() => {
    if (!deleteTarget) return
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) closeDeleteDialog() }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [deleteTarget, busy])

  async function createDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const pdf = form.get('pdf') as File | null
    const drawingNumber = String(form.get('drawingNumber') ?? '').trim()
    const currentRevision = String(form.get('revisionNumber') ?? '').trim()
    if (!currentRevision) {
      setError('Current revision is required.')
      return
    }
    form.set('relatedDocumentsJson', JSON.stringify(linksFromForm(form)))
    setBusy(true); setError(null); setUploadProgress(0)
    try {
      const created = await uploadWithProgress<{ id: number }>('/api/drawings/create-with-revision', form, setUploadProgress)
      formElement.reset()
      setShowCreate(false)
      setFeedback({
        kind: 'success',
        title: `${drawingNumber} created successfully`,
        message: pdf?.size
          ? `The drawing and its current revision ${currentRevision} file were saved. The drawing workspace is now open for further edits.`
          : `The drawing was saved at current revision ${currentRevision}. The drawing workspace is now open for further edits.`,
      })
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
          title: canEditDrawingMetadata ? form.get('title') : selected.title,
          customer: canEditDrawingMetadata ? form.get('customer') : selected.customer,
          partNumbers: canEditDrawingMetadata ? commaValues(form.get('partNumbers')) : selected.partNumbers,
          notes: canEditDrawingMetadata ? form.get('notes') : selected.notes,
          physicalMylarLocation: selected.physicalMylarLocation,
          relatedDocuments: canEditSpecifications
            ? linksFromForm(form)
            : null,
        }),
      })
      setShowEdit(false)
      await refresh()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to update drawing metadata.') }
    finally { setBusy(false) }
  }

  async function uploadSupplementalDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const label = String(form.get('label') ?? '').trim()
    const document = form.get('document')
    if (!label || !(document instanceof File) || document.size === 0) {
      setSupplementalError('Add a label and select a supporting document before uploading.')
      return
    }

    setBusy(true)
    setSupplementalError(null)
    setUploadProgress(0)
    try {
      await uploadWithProgress<DocumentLink>(
        `/api/drawings/${selected.id}/supplemental-documents`,
        form,
        setUploadProgress,
      )
      formElement.reset()
      await refresh()
      setFeedback({
        kind: 'success',
        title: 'Supporting document uploaded',
        message: `${label} was added to Revision ${activeRevision?.revisionNumber ?? ''} of ${selected.drawingNumber}.`,
      })
    } catch (cause) {
      setSupplementalError(cause instanceof Error ? cause.message : 'Unable to upload the supporting document.')
    } finally {
      setBusy(false)
      setUploadProgress(null)
    }
  }

  async function recordMylarMovement(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected || !mylar) return
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const location = String(form.get('location') ?? '').trim()
    const note = String(form.get('note') ?? '').trim()
    const checkingOut = !mylar.isCheckedOut
    if (!location) {
      setMylarError('A location is required for every Mylar custody record.')
      return
    }

    setBusy(true)
    setMylarError(null)
    setMylarMessage(null)
    try {
      await api(`/api/drawings/${selected.id}/mylars/${mylar.id}/${checkingOut ? 'checkout' : 'checkin'}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ note: note || null, location }),
      })
      formElement.reset()
      await refresh()
      setMylarMessage(checkingOut
        ? `${mylar.mylarNumber} checked out by ${actorName} to ${location}.`
        : `${mylar.mylarNumber} checked in by ${actorName} at ${location}.`)
    } catch (cause) {
      setMylarError(cause instanceof Error ? cause.message : `Unable to check ${checkingOut ? 'out' : 'in'} the Mylar.`)
    } finally {
      setBusy(false)
    }
  }

  async function registerMylar(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    const formElement = event.currentTarget
    const form = new FormData(formElement)
    const mylarNumber = String(form.get('mylarNumber') ?? '').trim()
    const location = String(form.get('location') ?? '').trim()
    const note = String(form.get('note') ?? '').trim()
    if (!mylarNumber || !location) {
      setMylarError('Mylar number and initial storage location are required.')
      return
    }

    setBusy(true)
    setMylarError(null)
    setMylarMessage(null)
    try {
      const registered = await api<DrawingMylar>(`/api/drawings/${selected.id}/mylars`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mylarNumber, location, note: note || null }),
      })
      formElement.reset()
      await refresh()
      setMylarMessage(`${registered.mylarNumber} is registered and checked in at ${location}.`)
    } catch (cause) {
      setMylarError(cause instanceof Error ? cause.message : 'Unable to register the Mylar.')
    } finally {
      setBusy(false)
    }
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
      (!(pdf instanceof File) || pdf.size === 0) && 'drawing file',
      !changeDescription && 'revision change summary',
    ].filter(Boolean) as string[]
    if (missingFields.length) {
      setFeedback({
        kind: 'error',
        title: 'Complete the required revision details',
        message: `Add ${missingFields.join(', ')} before uploading this new revision.`,
      })
      return
    }
    setBusy(true); setError(null); setUploadProgress(0)
    try {
      const uploaded = await uploadWithProgress<{ id: number }>(`/api/drawings/${selected.id}/revisions`, form, setUploadProgress)
      formElement.reset()
      await refresh()
      setActiveRevisionId(uploaded.id)
      setPreviewRevisionId(null)
      setShowRevisionUpload(false)
      setFeedback({
        kind: 'success',
        title: `Revision ${revisionNumber} uploaded as a draft`,
        message: 'A new revision and its file package were added to permanent drawing history. Edit the revision when it is ready to submit for approval.',
      })
    } catch (cause) {
      setFeedback({
        kind: 'error',
        title: 'Revision was not submitted',
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
    if (!hasPdf) { setError('A metadata-only revision cannot be approved. Upload a drawing file first.'); return }
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

  function openRevisionEditor(revision: Revision) {
    setEditRevisionTarget(revision)
    setRevisionSaveIntent(null)
    setRevisionEditError(null)
  }
  function closeRevisionEditor() {
    setEditRevisionTarget(null)
    setRevisionSaveIntent(null)
    setRevisionEditError(null)
    window.setTimeout(() => document.querySelector<HTMLElement>('.revision-edit-button')?.focus(), 0)
  }
  function openRevisionUpload() {
    setShowRevisionUpload(true)
    window.setTimeout(() => revisionUploadDialogRef.current?.querySelector<HTMLElement>('input, button')?.focus(), 0)
  }
  function closeRevisionUpload() {
    if (busy) return
    setShowRevisionUpload(false)
    window.setTimeout(() => document.querySelector<HTMLElement>('.drawing-upload-button')?.focus(), 0)
  }
  function openMylarCustody() {
    setShowMylarCustody(true)
    window.setTimeout(() => mylarDrawerRef.current?.querySelector<HTMLElement>(
      '.mylar-register-form input, .mylar-action-form input, button',
    )?.focus(), 0)
  }
  function closeMylarCustody() {
    if (busy) return
    setShowMylarCustody(false)
    window.setTimeout(() => document.querySelector<HTMLElement>('.drawing-mylar-button')?.focus(), 0)
  }
  function selectRevision(revision: Revision) {
    setActiveRevisionId(revision.id)
    setPreviewRevisionId(null)
  }
  function previewRevision(revision: Revision) {
    if (!revision.hasPdf) return
    setActiveRevisionId(revision.id)
    setPreviewRevisionId(revision.id)
    window.setTimeout(() => document.querySelector<HTMLElement>('.drawing-document-viewer')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 0)
  }
  async function saveRevisionDraft(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!editRevisionTarget) return
    const target = editRevisionTarget
    const submitter = (event.nativeEvent as SubmitEvent).submitter as HTMLButtonElement | null
    const submitForApproval = submitter?.value === 'approval'
    const form = new FormData(event.currentTarget)
    const revisionNumber = String(form.get('revisionNumber') ?? '').trim()
    const revisionDate = String(form.get('revisionDate') ?? '').trim()
    const changeDescription = String(form.get('changeDescription') ?? '').trim()
    const pdf = form.get('pdf')
    const missing = [
      !revisionNumber && 'revision number',
      !revisionDate && 'revision date',
      !changeDescription && 'revision change summary',
      submitForApproval && !target.hasPdf && (!(pdf instanceof File) || pdf.size === 0) && 'drawing file',
    ].filter(Boolean) as string[]
    if (missing.length) {
      setRevisionEditError(`Add ${missing.join(', ')} before saving this revision.`)
      return
    }

    setRevisionSaveIntent(submitForApproval ? 'approval' : 'draft')
    setBusy(true)
    setRevisionEditError(null)
    setUploadProgress(0)
    try {
      const saved = await uploadWithProgress<{ revisionId: number; hasPdf: boolean }>(
        `/api/drawing-revisions/${target.id}/editable-draft`,
        form,
        setUploadProgress,
      )
      let submitted = false
      if (submitForApproval) {
        try {
          await api(`/api/drawing-revisions/${saved.revisionId}/status`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ status: 'UnderReview', comments: '' }),
          })
          submitted = true
        } catch (cause) {
          closeRevisionEditor()
          await refresh()
          setFeedback({
            kind: 'error',
            title: `Revision ${revisionNumber} was saved as a draft`,
            message: cause instanceof Error
              ? `The draft was saved, but approval submission needs attention: ${cause.message}`
              : 'The draft was saved, but it could not be submitted for approval.',
          })
          return
        }
      }
      closeRevisionEditor()
      await refresh()
      setFeedback({
        kind: 'success',
        title: `Revision ${revisionNumber} ${submitted ? 'submitted for approval' : 'saved as a draft'}`,
        message: submitted
          ? 'The selected revision was updated and moved into the approval queue.'
          : 'The selected revision was updated in place and remains a draft.',
      })
    } catch (cause) {
      setRevisionEditError(cause instanceof Error ? cause.message : 'Unable to save the revision draft.')
    } finally {
      setBusy(false)
      setRevisionSaveIntent(null)
      setUploadProgress(null)
    }
  }

  function closeArchiveDialog() {
    setShowArchive(false)
    setArchiveReason('')
    window.setTimeout(() => document.querySelector<HTMLElement>('.record-header-archive')?.focus(), 0)
  }
  function closeActivationDialog() {
    setActivateTarget(null)
    window.setTimeout(() => document.querySelector<HTMLElement>('.revision-make-current')?.focus(), 0)
  }
  function closeAuditDrawer() {
    setAuditOpen(false)
    window.setTimeout(() => document.querySelector<HTMLElement>('.record-header-audit')?.focus(), 0)
  }

  async function archiveDrawing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!selected) return
    const reason = archiveReason.trim()
    if (!reason) return
    setBusy(true); setError(null)
    try {
      await api(`/api/drawings/${selected.id}/archive`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason }),
      })
      setShowArchive(false)
      setArchiveReason('')
      await refresh()
      setFeedback({
        kind: 'success',
        title: `${selected.drawingNumber} archived`,
        message: 'The drawing and its revision history are preserved and removed from active engineering workflows.',
      })
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to archive drawing.') }
    finally { setBusy(false) }
  }

  async function makeRevisionCurrent() {
    if (!activateTarget) return
    const revisionNumber = activateTarget.revisionNumber
    setBusy(true)
    setError(null)
    try {
      await api(`/api/drawing-revisions/${activateTarget.id}/make-current`, { method: 'POST' })
      setActivateTarget(null)
      await refresh()
      setFeedback({
        kind: 'success',
        title: `Revision ${revisionNumber} is current`,
        message: 'The selected revision is now the controlled drawing. Any prior current revision remains preserved in permanent history.',
      })
    } catch (cause) {
      setActivateTarget(null)
      setFeedback({
        kind: 'error',
        title: `Revision ${revisionNumber} was not activated`,
        message: cause instanceof Error ? cause.message : 'Unable to make this revision current.',
      })
    } finally {
      setBusy(false)
    }
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
    if (!deleteTarget || !deleteAcknowledged) return
    if (deleteTarget.kind === 'drawing' && deleteConfirmation !== deleteTarget.matchValue) return
    setBusy(true); setDeleteError(null)
    try {
      const revision = deleteTarget.kind === 'revision'
      await api(revision ? `/api/drawing-revisions/${deleteTarget.id}` : `/api/drawings/${deleteTarget.id}`, {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(revision ? { confirmed: true } : { confirmed: true, drawingNumber: deleteConfirmation }),
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

  const activeRevision = selected
    ? selected.revisions.find(revision => revision.id === activeRevisionId)
      ?? selected.revisions.find(revision => revision.id === selected.currentApprovedRevisionId)
      ?? selected.revisions[0]
      ?? null
    : null
  const previewedRevision = selected?.revisions.find(revision => revision.id === previewRevisionId && revision.hasPdf) ?? null
  const mylar = selected?.mylars[0] ?? null
  const summaryMylarEvent = mylar
    ? selected?.mylarHistory.find(item => item.mylarId === mylar.id) ?? null
    : null
  const specificationTags = selected ? linkValuesOfKind(selected, 'Specification') : []
  const supplementalDocuments = selected?.relatedDocuments.filter(link =>
    link.kind === 'SupplementalDocument' && link.drawingRevisionId === activeRevision?.id) ?? []

  return <div className="drawing-workspace">
    {error && <div className="inline-alert" role="alert">{error}<button type="button" onClick={() => setError(null)}><X size={15}/></button></div>}

    {uploadProgress !== null && <div className="upload-progress" role="status"><span style={{ width: `${uploadProgress}%` }}/><strong>{uploadProgress}%</strong><small>Transferring controlled file package</small></div>}

    {canCreateDrawing && showCreate && <form className="panel record-form" onSubmit={createDrawing}>
      <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Controlled drawing creation</span><h2>Create drawing record</h2><p>Record the drawing's current revision and optionally attach its current PDF or image file. Future changes are added through the revision workflow.</p></div></div>
      {designAuthorityError && <p className="form-error" role="alert"><AlertTriangle size={15}/> {designAuthorityError}</p>}
      <div className="form-grid">
        <label>Drawing number<input name="drawingNumber" required/></label><label>Title / description<input name="title" required/></label>
        <label>Design authority<select name="customer" required defaultValue="" disabled={designAuthoritiesLoading || !designAuthorities.length}><option value="" disabled>{designAuthoritiesLoading ? 'Indexing storage folders...' : designAuthorities.length ? 'Select approved authority' : 'No approved authorities configured'}</option>{designAuthorities.map(authority => <option key={authority} value={authority}>{authority}</option>)}</select><small>Managed in Engineering Admin / File Storage</small></label><label>Linked part numbers<input name="partNumbers" placeholder="PN-1001, PN-1002"/></label>
        {canEditSpecifications && <EngineeringTagEditor name="specifications" label="Specification tags" placeholder="Example: SPEC-100"/>}
        <label>Current revision<input name="revisionNumber" placeholder="A" required/></label>
        <EngineeringDatePicker name="effectiveDate" label="Effective date"/>
        <FilePicker name="pdf" label="Upload current drawing file (PDF or image)" accept={DRAWING_FILE_ACCEPT} className="wide"/>
        <label className="wide">Drawing notes<textarea name="notes" rows={2}/></label>
      </div>
      <div className="form-actions"><button className="button" disabled={busy || designAuthoritiesLoading || !designAuthorities.length}><FilePlus2 size={15}/> Create drawing</button><button className="button ghost" type="button" onClick={() => { setShowCreate(false); if (!selected) onBackToDashboard() }}>Cancel</button></div>
    </form>}

    {recordLoading ? <article className="panel skeleton-panel drawing-record-loading" aria-label="Loading drawing record">
      <div className="skeleton-line lg"/>
      <div className="skeleton-line"/>
      <div className="skeleton-line" style={{ width: '58%' }}/>
    </article> : !selected && !showCreate ? <article className="panel drawing-empty"><FileText size={30}/><h2>No drawing selected</h2><p>Return to the drawing register to search and open a controlled record.</p><button className="button ghost" type="button" onClick={onBackToDashboard}>Open drawing register</button></article> : selected ? <article className="drawing-detail">
        {canDeleteDrawing && selected.approvalStatus === 'Draft' && selected.revisions.length === 0 && <div className="record-destructive-actions"><button className="button danger" type="button" onClick={requestDrawingDelete}><Trash2 size={14}/> Delete draft</button></div>}

        {canOpenMetadataEditor && showEdit && <form id="drawing-metadata-editor" className="panel record-form metadata-edit-form" onSubmit={updateDrawing}>
          <div className="panel-head compact"><div className="panel-head-text"><span className="eyebrow">Audited metadata update</span><h2>Edit drawing record</h2></div></div>
          {designAuthorityError && <p className="form-error" role="alert"><AlertTriangle size={15}/> {designAuthorityError}</p>}
          <div className="form-grid">
            <label>Title / description<input name="title" defaultValue={selected.title} required disabled={!canEditDrawingMetadata}/></label><label>Design authority<select name="customer" defaultValue={selected.customer} required disabled={!canEditDrawingMetadata || designAuthoritiesLoading || !designAuthorities.length}>{!designAuthorities.some(authority => authority.toLowerCase() === selected.customer.toLowerCase()) && <option value={selected.customer} disabled>{selected.customer} (not indexed)</option>}{designAuthorities.map(authority => <option key={authority} value={authority}>{authority}</option>)}</select><small>Only approved storage folders can be selected</small></label>
            <label>Part numbers<input name="partNumbers" defaultValue={selected.partNumbers.join(', ')} disabled={!canEditDrawingMetadata}/></label>
            {canViewSpecifications && <EngineeringTagEditor
              name="specifications"
              label="Specification tags"
              initialValues={linkValuesOfKind(selected, 'Specification')}
              placeholder="Example: SPEC-100"
              disabled={!canEditSpecifications}
            />}
            <label className="wide">Notes<textarea name="notes" rows={3} defaultValue={selected.notes ?? ''} disabled={!canEditDrawingMetadata}/></label>
          </div>
          <div className="form-actions"><button className="button" disabled={busy || (canEditDrawingMetadata && (designAuthoritiesLoading || !designAuthorities.length))}>Save Changes</button><button className="button ghost" type="button" onClick={() => setShowEdit(false)}>Cancel</button></div>
        </form>}

        <section className="panel drawing-control-commandbar">
          <div>
            <h2>Drawing workspace</h2>
          </div>
          {canCreateRevision && !selected.isObsolete && <button className="button drawing-upload-button" type="button" onClick={openRevisionUpload}>
            <FilePlus2 size={15}/> Upload revision
          </button>}
        </section>

        <div className="drawing-document-layout">
          <section className="panel drawing-document-viewer" tabIndex={-1} aria-labelledby="drawing-document-title">
            <header className="drawing-document-header">
              <div>
                <span className="eyebrow">Selected revision</span>
                <h2 id="drawing-document-title">{activeRevision ? `Revision ${activeRevision.revisionNumber}` : 'No revision selected'}</h2>
              </div>
              {activeRevision && <span className={`revision-state revision-state-${activeRevision.status.toLowerCase()}`}>
                <i aria-hidden="true"/>
                {statusLabel(activeRevision.status)}
              </span>}
            </header>

            {activeRevision ? <>
              <div className="drawing-selected-revision">
                <div className="drawing-selected-copy">
                  <strong>{activeRevision.changeDescription}</strong>
                  <div className="revision-upload-meta">
                    <span><CalendarDays size={13}/><small>Uploaded</small><strong>{auditTimestamp(activeRevision.uploadedAt).date}</strong></span>
                    <span><UserRound size={13}/><small>Uploaded by</small><strong title={activeRevision.uploadedBy}>{activeRevision.uploadedBy}</strong></span>
                  </div>
                </div>
                <dl className="drawing-revision-facts">
                  <div><dt>Revision date</dt><dd>{date(activeRevision.revisionDate)}</dd></div>
                  <div><dt>Effective date</dt><dd>{date(activeRevision.effectiveDate)}</dd></div>
                  <div><dt>File</dt><dd title={activeRevision.originalFileName}>{activeRevision.hasPdf ? activeRevision.originalFileName : 'Drawing file not attached'}</dd></div>
                </dl>
              </div>

              {canApproveRevision && activeRevision.status === 'UnderReview' && <label className="drawing-review-comment">
                <span>Reviewer comment</span>
                <textarea value={reviewComments[activeRevision.id] ?? ''} onChange={event => setReviewComments(current => ({ ...current, [activeRevision.id]: event.target.value }))} placeholder="Add a disposition note for this review"/>
              </label>}

              <div className="drawing-revision-toolbar" aria-label={`Actions for revision ${activeRevision.revisionNumber}`}>
                {activeRevision.hasPdf && <button className="button revision-pdf-button" type="button" onClick={() => previewRevision(activeRevision)}>
                  <FileText size={14}/> Preview file
                </button>}
                {activeRevision.hasPdf && <a className="button ghost" href={`/api/drawing-revisions/${activeRevision.id}/file`} target="_blank" rel="noreferrer">
                  <ArrowRight size={14}/> Open file
                </a>}
                {canEditRevision && <button className="button ghost revision-edit-button" type="button" disabled={busy} onClick={() => openRevisionEditor(activeRevision)}>
                  <Pencil size={14}/> Edit revision
                </button>}
                {activeRevision.hasPdf && controlledFolderHref(activeRevision.controlledFilePath) && <a
                  className="button ghost"
                  href={controlledFolderHref(activeRevision.controlledFilePath) ?? undefined}
                  title="Open the controlled storage folder in File Explorer"
                >
                  <FolderOpen size={14}/> Open folder
                </a>}
                {canMakeRevisionCurrent && activeRevision.hasPdf && (activeRevision.status === 'Superseded' || activeRevision.status === 'Obsolete') && <button
                  className="button ghost revision-make-current"
                  type="button"
                  disabled={busy}
                  onClick={() => setActivateTarget(activeRevision)}
                >
                  <CheckCircle2 size={14}/> Make current
                </button>}
                {canSubmitRevision && activeRevision.status === 'Draft' && <button className="button ghost" type="button" disabled={busy || !activeRevision.hasPdf} title={!activeRevision.hasPdf ? 'Attach a drawing file before review.' : undefined} onClick={() => void setRevisionStatus(activeRevision, 'UnderReview')}>Submit review</button>}
                {canEditRevision && activeRevision.status === 'UnderReview' && <button className="button ghost" type="button" disabled={busy} onClick={() => void setRevisionStatus(activeRevision, 'Draft')}>Return to draft</button>}
                {canApproveRevision && activeRevision.status === 'UnderReview' && <button className="button" type="button" disabled={busy || !activeRevision.hasPdf} onClick={() => void approveRevision(activeRevision)}><CheckCircle2 size={14}/> Approve</button>}
                {canDeleteRevision && activeRevision.id !== selected.currentApprovedRevisionId && activeRevision.status !== 'Approved' && <button className="button danger" type="button" disabled={busy} onClick={() => requestRevisionDelete(activeRevision)}><Trash2 size={14}/> Delete</button>}
              </div>

              <div className={`drawing-pdf-stage ${previewedRevision?.id === activeRevision.id ? 'is-previewing' : ''}`}>
                {previewedRevision?.id === activeRevision.id ? <>
                  <div className="drawing-pdf-stagebar">
                    <span><FileText size={14}/> Controlled drawing file · Revision {previewedRevision.revisionNumber}</span>
                    <button type="button" onClick={() => setPreviewRevisionId(null)}><X size={14}/> Close preview</button>
                  </div>
                  <iframe title={`Controlled drawing file, Revision ${previewedRevision.revisionNumber}`} src={`/api/drawing-revisions/${previewedRevision.id}/file${previewedRevision.fileType === 'application/pdf' ? '#toolbar=1' : ''}`}/>
                </> : <div className="drawing-pdf-empty">
                  <span aria-hidden="true"><FileText size={29}/></span>
                  <strong>{activeRevision.hasPdf ? 'Controlled drawing file ready' : 'No drawing file attached'}</strong>
                  <p>{activeRevision.hasPdf
                    ? 'Preview the selected revision here without leaving the drawing record.'
                    : 'This revision remains a metadata record until a controlled drawing file is attached.'}</p>
                  {activeRevision.hasPdf && <button className="button" type="button" onClick={() => previewRevision(activeRevision)}><FileText size={14}/> Preview file</button>}
                </div>}
              </div>
            </> : <div className="drawing-pdf-empty drawing-no-revisions">
              <span aria-hidden="true"><FilePlus2 size={29}/></span>
              <strong>No revisions recorded</strong>
              <p>Upload the first controlled drawing file to begin permanent revision history.</p>
              {canCreateRevision && !selected.isObsolete && <button className="button" type="button" onClick={openRevisionUpload}><FilePlus2 size={14}/> Upload first revision</button>}
            </div>}
          </section>

          <div className="drawing-control-rail">
          <aside className="panel drawing-revision-history" aria-labelledby="drawing-history-title">
            <header className="drawing-history-header">
              <h2 id="drawing-history-title">Revisions</h2>
            </header>
            {selected.revisions.length ? <div className="drawing-history-list" role="list">
              {selected.revisions.map(revision => <article className={`drawing-history-card ${activeRevision?.id === revision.id ? 'is-selected' : ''}`} role="listitem" key={revision.id}>
                <button
                  type="button"
                  className="drawing-history-select"
                  aria-current={activeRevision?.id === revision.id ? 'true' : undefined}
                  onClick={() => selectRevision(revision)}
                >
                  <span className="drawing-history-revision">
                    <strong>Rev {revision.revisionNumber}</strong>
                    <span className={`revision-state revision-state-${revision.status.toLowerCase()}`}><i aria-hidden="true"/>{statusLabel(revision.status)}</span>
                  </span>
                  <span className="drawing-history-description">{revision.changeDescription}</span>
                  <span className="revision-upload-meta">
                    <span><CalendarDays size={12}/><small>Uploaded</small><strong>{auditTimestamp(revision.uploadedAt).date}</strong></span>
                    <span><UserRound size={12}/><small>Uploaded by</small><strong title={revision.uploadedBy}>{revision.uploadedBy}</strong></span>
                  </span>
                  <span className="drawing-history-file">{revision.hasPdf ? <><FileText size={12}/>{revision.originalFileName}</> : <><AlertTriangle size={12}/>Drawing file not attached</>}</span>
                  {revision.id === selected.currentApprovedRevisionId && <span className="drawing-current-marker"><CheckCircle2 size={12}/> Current controlled revision</span>}
                </button>
              </article>)}
            </div> : <div className="drawing-history-empty"><History size={22}/><strong>No revision history</strong><p>The first uploaded revision will appear here.</p></div>}
          </aside>

          {canViewMylar && <section className={`panel drawing-mylar-card ${!mylar ? 'is-unregistered' : mylar.isCheckedOut ? 'is-out' : 'is-in'}`} aria-labelledby="drawing-mylar-title">
            <header className="drawing-mylar-card-header">
              <h2 id="drawing-mylar-title">Mylar custody</h2>
              <span className="drawing-mylar-state"><i aria-hidden="true"/>{!mylar
                ? 'Not registered'
                : mylar.isCheckedOut
                  ? 'Checked out'
                  : 'Checked in'}</span>
            </header>
            <div className="drawing-mylar-card-body">
              <div className="drawing-mylar-overview">
                <span className="drawing-mylar-icon" aria-hidden="true"><MapPin size={19}/></span>
                <div>
                  <strong className={mylar ? 'technical-id' : undefined}>{mylar?.mylarNumber ?? 'No physical Mylar registered'}</strong>
                  <p>{mylar
                    ? mylar.isCheckedOut
                      ? `Held by ${mylar.checkedOutBy || 'an unrecorded user'} at ${mylar.currentLocation || 'an unrecorded destination'}`
                      : `Available in controlled storage at ${mylar.currentLocation || 'an unrecorded location'}`
                    : 'Register the drawing’s physical Mylar before recording custody movements.'}</p>
                </div>
              </div>
              {mylar ? <dl>
                <div><dt>Current location</dt><dd>{mylar.currentLocation || 'Not recorded'}</dd></div>
                <div><dt>{mylar.isCheckedOut ? 'Held by' : 'Custody'}</dt><dd>{mylar.checkedOutBy || 'Controlled storage'}</dd></div>
                <div><dt>Latest activity</dt><dd>{summaryMylarEvent ? `${mylarTypeLabel(summaryMylarEvent.type)} · ${date(summaryMylarEvent.recordedAt)}` : 'No movement recorded'}</dd></div>
              </dl> : <dl className="drawing-mylar-empty-history">
                <div><dt>Custody history</dt><dd>{selected.mylarHistory.length} retained record{selected.mylarHistory.length === 1 ? '' : 's'}</dd></div>
              </dl>}
              {(canManageMylar || mylar) && <button className="button ghost drawing-mylar-button" type="button" onClick={openMylarCustody}>
                {!canManageMylar
                  ? <><MapPin size={14}/> View custody</>
                  : !mylar
                    ? <><FilePlus2 size={14}/> Register Mylar</>
                    : mylar.isCheckedOut
                      ? <><LogIn size={14}/> Check in Mylar</>
                      : <><LogOut size={14}/> Check out Mylar</>}
              </button>}
            </div>
          </section>}

          {(canViewSpecifications || canViewSupportingDocuments) && <details className="panel drawing-reference-panel">
            <summary className="drawing-reference-header">
              <div><span className="eyebrow">Revision support</span><h2>Specifications and supporting documents</h2></div>
              <span className="drawing-reference-summary-count">{specificationTags.length + supplementalDocuments.length} linked <ChevronDown size={16}/></span>
            </summary>
            <div className="drawing-reference-grid">
              {canViewSpecifications && <article className="drawing-specification-card">
                <header><h3>Specification tags</h3><span>{specificationTags.length}</span></header>
                {specificationTags.length ? <div className="drawing-spec-tags">
                  {specificationTags.map(specification => <span key={specification}>{specification}</span>)}
                </div> : <p>No specification tags applied.</p>}
                {canEditSpecifications && !selected.isObsolete && <small>Use Edit metadata to add or remove specification tags.</small>}
              </article>}

              {canViewSupportingDocuments && <article className="drawing-supplemental-card">
                <header><h3>Revision {activeRevision?.revisionNumber ?? '—'} supporting documents</h3><span>{supplementalDocuments.length}</span></header>
                {supplementalDocuments.length ? <div className="drawing-supplemental-list">
                  {supplementalDocuments.map(document => <div key={document.id}>
                    <span><strong>{document.referenceNumber}</strong><small title={document.title ?? undefined}>{document.title ?? 'Legacy document reference'}</small></span>
                    {document.location
                      ? <a className="button ghost" href={`/api/drawing-documents/${document.id}/file`} target="_blank" rel="noreferrer"><ArrowRight size={13}/> Open</a>
                      : <em>Reference only</em>}
                  </div>)}
                </div> : <p>No supporting documents are attached to this revision.</p>}

                {canManageSupportingDocuments && !selected.isObsolete && activeRevision && activeRevision.status !== 'Approved' && activeRevision.status !== 'Superseded' && activeRevision.status !== 'Obsolete' && <form className="supplemental-upload-form" onSubmit={uploadSupplementalDocument}>
                  <input type="hidden" name="revisionId" value={activeRevision.id}/>
                  <label><span>Document label</span><input name="label" required placeholder="Example: Stress analysis"/></label>
                  <FilePicker name="document" label="Supporting file" accept={SUPPLEMENTAL_FILE_ACCEPT} required/>
                  {supplementalError && <div className="inline-alert" role="alert">{supplementalError}</div>}
                  <button className="button" type="submit" disabled={busy}><FilePlus2 size={14}/>{busy ? 'Uploading...' : 'Upload document'}</button>
                </form>}
              </article>}
            </div>
          </details>}
          </div>
        </div>

        {canCreateRevision && showRevisionUpload && <div className="revision-upload-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeRevisionUpload() }}>
          <section ref={revisionUploadDialogRef} className="revision-upload-dialog" role="dialog" aria-modal="true" aria-labelledby="revision-upload-title" aria-describedby="revision-upload-description">
            <header className="revision-upload-header">
              <span className="revision-upload-icon" aria-hidden="true"><FilePlus2 size={20}/></span>
              <div>
                <span className="eyebrow">Permanent revision record</span>
                <h2 id="revision-upload-title">Upload drawing revision</h2>
                <p id="revision-upload-description">Add a controlled PDF or image file to {selected.drawingNumber}.</p>
              </div>
              <button autoFocus type="button" className="delete-dialog-close" aria-label="Close revision upload" disabled={busy} onClick={closeRevisionUpload}><X size={18}/></button>
            </header>
            <div className="revision-upload-body">
              <RevisionUploadForm
                busy={busy}
                onSubmit={uploadRevision}
                onCancel={closeRevisionUpload}
                sourceRevisionNumber={activeRevision?.revisionNumber}
                supportingDocuments={(canManageSupportingDocuments ? supplementalDocuments : []).map(document => ({
                  id: document.id,
                  label: document.referenceNumber,
                  fileName: document.title,
                }))}
              />
            </div>
          </section>
        </div>}

        {showMylarCustody && <div className="mylar-drawer-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeMylarCustody() }}>
          <aside ref={mylarDrawerRef} className="mylar-drawer" role="dialog" aria-modal="true" aria-labelledby="mylar-drawer-title">
            <header className="mylar-drawer-header">
              <span className="mylar-drawer-icon" aria-hidden="true"><MapPin size={20}/></span>
              <div>
                <h2 id="mylar-drawer-title">Mylar custody</h2>
                <p className="technical-id">{selected.drawingNumber}</p>
              </div>
              <button autoFocus type="button" className="delete-dialog-close" aria-label="Close Mylar custody" disabled={busy} onClick={closeMylarCustody}><X size={18}/></button>
            </header>
            <div className="mylar-drawer-body">
              {!mylar && <section className="mylar-registration-section">
                <header>
                  <small>First-time setup</small>
                  <h3>Register the physical Mylar</h3>
                  <p>Registration establishes the Mylar’s identity and initial checked-in storage location for this drawing.</p>
                </header>

                {canManageMylar && !selected.isObsolete ? <form className="mylar-register-form" onSubmit={registerMylar}>
                  <div className="mylar-action-fields">
                    <label><span>Mylar number</span><input autoFocus name="mylarNumber" required placeholder="MY-001"/></label>
                    <label><span>Initial storage location</span><input name="location" required placeholder="Cabinet and slot"/></label>
                    <label className="wide"><span>Registration note <small>Optional</small></span><textarea name="note" rows={2} placeholder="Condition, copy designation, or other identifying detail"/></label>
                  </div>
                  <div className="mylar-action-footer">
                    <p>Registration will be permanently recorded under <strong>{actorName}</strong>.</p>
                    <button className="button" type="submit" disabled={busy}><FilePlus2 size={14}/>{busy ? 'Registering...' : 'Register Mylar'}</button>
                  </div>
                </form> : <div className="mylar-registration-unavailable">
                  <MapPin size={21}/>
                  <strong>{selected.isObsolete ? 'Archived drawing' : 'No Mylar registered'}</strong>
                  <p>{selected.isObsolete
                    ? 'A physical Mylar can no longer be registered for this archived drawing.'
                    : 'An Editor or Admin can register the physical Mylar for this drawing.'}</p>
                </div>}
              </section>}

              {mylarMessage && <div className="mylar-action-success" role="status"><CheckCircle2 size={15}/>{mylarMessage}</div>}
              {mylarError && <div className="mylar-action-error" role="alert"><AlertTriangle size={16}/><div><strong>Custody record stopped</strong><p>{mylarError}</p></div></div>}

              {mylar && <>
                <div className={`mylar-current-state ${mylar.isCheckedOut ? 'is-out' : 'is-in'}`}>
                  <span className="mylar-current-icon" aria-hidden="true"><MapPin size={19}/></span>
                  <div>
                    <small>Physical Mylar</small>
                    <strong className="technical-id">{mylar.mylarNumber}</strong>
                    <span>{mylar.isCheckedOut && mylar.checkedOutAt
                      ? `Checked out ${auditTimestamp(mylar.checkedOutAt).date} at ${auditTimestamp(mylar.checkedOutAt).time}`
                      : 'Checked in and available for controlled use'}</span>
                  </div>
                  <dl>
                    <div><dt>{mylar.isCheckedOut ? 'Current destination' : 'Storage location'}</dt><dd>{mylar.currentLocation || 'Not recorded'}</dd></div>
                    <div><dt>{mylar.isCheckedOut ? 'Checked out by' : 'Custody'}</dt><dd>{mylar.checkedOutBy || 'Engineering control'}</dd></div>
                  </dl>
                </div>

                {!canManageMylar ? null : selected.isObsolete && !mylar.isCheckedOut ? <div className="mylar-archived-notice">
                  <Archive size={17}/>
                  <div><strong>Archived drawing</strong><p>This Mylar remains in custody history and cannot be checked out again.</p></div>
                </div> : <form
                  key={`${selected.id}-${mylar.id}-${mylar.isCheckedOut ? 'checkin' : 'checkout'}-${selected.mylarHistory[0]?.id ?? 0}`}
                  className="mylar-action-form"
                  onSubmit={recordMylarMovement}
                >
                  <header>
                    <span aria-hidden="true">{mylar.isCheckedOut ? <LogIn size={18}/> : <LogOut size={18}/>}</span>
                    <div>
                      <small>Custody action</small>
                      <h3>{mylar.isCheckedOut ? `Check in ${mylar.mylarNumber}` : `Check out ${mylar.mylarNumber}`}</h3>
                      <p>This action will be permanently recorded under <strong>{actorName}</strong>.</p>
                    </div>
                  </header>
                  <div className="mylar-action-fields">
                    <label className="wide">
                      <span>{mylar.isCheckedOut ? 'Return storage location' : 'Checkout destination'}</span>
                      <input autoFocus name="location" required placeholder={mylar.isCheckedOut ? 'Cabinet and slot' : 'Department, desk, or work area'}/>
                    </label>
                    <label className="wide">
                      <span>Note <small>Optional</small></span>
                      <textarea name="note" rows={2} placeholder={mylar.isCheckedOut ? 'Condition or return note' : 'Purpose or handling instructions'}/>
                    </label>
                  </div>
                  <div className="mylar-action-footer">
                    <p>User, Mylar number, location, date, time, and note are permanently retained.</p>
                    <button className="button" type="submit" disabled={busy}>
                      {mylar.isCheckedOut ? <LogIn size={15}/> : <LogOut size={15}/>}
                      {busy ? 'Recording...' : mylar.isCheckedOut ? 'Check in Mylar' : 'Check out Mylar'}
                    </button>
                  </div>
                </form>}
              </>}

              <section className="mylar-history-section">
                <header>
                  <div><small>Permanent custody log</small><h3>Mylar history</h3></div>
                  <span>{selected.mylarHistory.length} record{selected.mylarHistory.length === 1 ? '' : 's'}</span>
                </header>
                {selected.mylarHistory.length ? <div className="mylar-history-table-wrap">
                  <table className="mylar-history-table">
                    <thead><tr><th>Activity</th><th>Location</th><th>Date and time</th><th>Note</th><th>Recorded by</th></tr></thead>
                    <tbody>{selected.mylarHistory.map(item => {
                      const timestamp = auditTimestamp(item.recordedAt)
                      return <tr key={item.id}>
                        <td><span className={`mylar-event-type ${item.type === 'CheckedOut' ? 'is-out' : 'is-in'}`}>{mylarTypeLabel(item.type)}</span></td>
                        <td>{item.location || 'Not recorded'}</td>
                        <td><time dateTime={item.recordedAt}><span>{timestamp.date}</span><small>{timestamp.time}</small></time></td>
                        <td>{item.note || 'None'}</td>
                        <td>{item.actor}</td>
                      </tr>
                    })}</tbody>
                  </table>
                </div> : <div className="mylar-history-empty"><History size={21}/><strong>No custody activity yet</strong><p>Registering the physical Mylar will start its permanent custody history.</p></div>}
              </section>
            </div>
          </aside>
        </div>}

      </article> : null}

    {canArchiveDrawing && showArchive && selected && <div className="archive-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeArchiveDialog() }}>
      <section className="archive-dialog" role="dialog" aria-modal="true" aria-labelledby="archive-dialog-title" aria-describedby="archive-dialog-description">
        <header className="archive-dialog-header">
          <span className="archive-dialog-icon"><Archive size={21}/></span>
          <div><span className="eyebrow">Controlled record retention</span><h2 id="archive-dialog-title">Archive {selected.drawingNumber}?</h2></div>
          <button type="button" className="delete-dialog-close" aria-label="Close archive dialog" disabled={busy} onClick={closeArchiveDialog}><X size={18}/></button>
        </header>
        <div className="archive-dialog-notice" id="archive-dialog-description">
          <strong>This drawing will leave active engineering workflows.</strong>
          <p>Its revisions, files, approvals, and audit history will remain preserved and searchable under Archived drawings.</p>
        </div>
        <form className="archive-dialog-form" onSubmit={archiveDrawing}>
          <label className="archive-reason-field">
            <span>Reason for archiving</span>
            <textarea autoFocus required rows={4} value={archiveReason} onChange={event => setArchiveReason(event.target.value)} placeholder="Explain why this drawing is no longer active."/>
            <small>This explanation is recorded permanently in the audit history.</small>
          </label>
          <div className="archive-dialog-actions">
            <button type="button" className="button ghost" disabled={busy} onClick={closeArchiveDialog}>Cancel</button>
            <button type="submit" className="button danger" disabled={busy || !archiveReason.trim()}><Archive size={15}/>{busy ? 'Archiving...' : 'Archive drawing'}</button>
          </div>
        </form>
      </section>
    </div>}

    {canMakeRevisionCurrent && activateTarget && selected && <div className="archive-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeActivationDialog() }}>
      <section className="archive-dialog activation-dialog" role="dialog" aria-modal="true" aria-labelledby="activation-dialog-title" aria-describedby="activation-dialog-description">
        <header className="archive-dialog-header">
          <span className="activation-dialog-icon"><CheckCircle2 size={21}/></span>
          <div><span className="eyebrow">Controlled revision change</span><h2 id="activation-dialog-title">Make revision {activateTarget.revisionNumber} current?</h2></div>
          <button type="button" className="delete-dialog-close" aria-label="Close activation dialog" disabled={busy} onClick={closeActivationDialog}><X size={18}/></button>
        </header>
        <div className="activation-dialog-notice" id="activation-dialog-description">
          <strong>This changes the controlled drawing revision.</strong>
          <p>{selected.isObsolete && selected.currentApprovedRevisionId === activateTarget.id
            ? `Revision ${activateTarget.revisionNumber} will be restored as the current revision and this drawing will return to the active register.`
            : `Revision ${activateTarget.revisionNumber} will become current. Revision ${selected.currentRevision ?? 'currently approved'} will remain in permanent history as superseded.`}</p>
        </div>
        <div className="archive-dialog-actions activation-dialog-actions">
          <button type="button" className="button ghost" disabled={busy} onClick={closeActivationDialog}>Cancel</button>
          <button type="button" className="button" disabled={busy} onClick={() => void makeRevisionCurrent()}>
            <CheckCircle2 size={15}/>{busy ? 'Updating…' : 'Make current'}
          </button>
        </div>
      </section>
    </div>}

    {canEditRevision && editRevisionTarget && <div className="revision-edit-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeRevisionEditor() }}>
      <section className="revision-edit-dialog" role="dialog" aria-modal="true" aria-labelledby="revision-edit-title">
        <header className="revision-edit-header">
          <span className="revision-edit-icon" aria-hidden="true"><Pencil size={20}/></span>
          <div>
            <span className="eyebrow">Controlled revision workflow</span>
            <h2 id="revision-edit-title">Edit Revision {editRevisionTarget.revisionNumber}</h2>
          </div>
          <button type="button" className="delete-dialog-close" aria-label="Close revision editor" disabled={busy} onClick={closeRevisionEditor}><X size={18}/></button>
        </header>
        <div className="revision-edit-notice">
          <strong>Changes apply to this revision.</strong>
          <p>Update Revision {editRevisionTarget.revisionNumber}, then save it as a draft or submit the same revision for approval. Use Upload revision to create a separate revision.</p>
        </div>
        <form className="record-form revision-edit-form" noValidate onSubmit={saveRevisionDraft}>
          <div className="form-grid">
            <label>Revision number<input autoFocus name="revisionNumber" defaultValue={editRevisionTarget.revisionNumber} required/></label>
            <EngineeringDatePicker name="revisionDate" label="Revision date" required initialValue={dateInputValue(editRevisionTarget.revisionDate)}/>
            <EngineeringDatePicker name="effectiveDate" label="Effective date" initialValue={dateInputValue(editRevisionTarget.effectiveDate)}/>
            <div className="revision-source-file">
              <span>Current drawing file</span>
              <strong>{editRevisionTarget.hasPdf ? editRevisionTarget.originalFileName : 'No drawing file attached'}</strong>
            </div>
            <FilePicker
              name="pdf"
              label={editRevisionTarget.hasPdf ? 'Replace drawing file (optional)' : 'Attach drawing file (required for approval)'}
              accept={DRAWING_FILE_ACCEPT}
              className="wide"
            />
            <label className="wide">Revision change summary<textarea name="changeDescription" defaultValue={editRevisionTarget.changeDescription} required rows={2}/></label>
            <label className="wide">Notes<textarea name="notes" defaultValue={editRevisionTarget.notes ?? ''} rows={2}/></label>
          </div>
          {revisionEditError && <div className="inline-alert" role="alert">{revisionEditError}</div>}
          <div className="revision-edit-actions">
            <button type="button" className="button ghost" disabled={busy} onClick={closeRevisionEditor}>Cancel</button>
            <button type="submit" name="revisionIntent" value="draft" className="button ghost revision-save-draft" disabled={busy}>
              <Pencil size={15}/>{busy && revisionSaveIntent === 'draft' ? 'Saving…' : 'Save as draft'}
            </button>
            {canSubmitRevision && <button type="submit" name="revisionIntent" value="approval" className="button revision-submit-approval" disabled={busy}>
              <CheckCircle2 size={15}/>{busy && revisionSaveIntent === 'approval' ? 'Submitting…' : 'Submit for approval'}
            </button>}
          </div>
        </form>
      </section>
    </div>}

    {canViewAudit && auditOpen && selected && <div className="audit-drawer-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeAuditDrawer() }}>
      <aside ref={auditDrawerRef} className="audit-drawer" role="dialog" aria-modal="true" aria-labelledby="audit-drawer-title">
        <header className="audit-drawer-header">
          <span className="audit-drawer-icon" aria-hidden="true"><History size={19}/></span>
          <div>
            <span className="eyebrow">Controlled record history</span>
            <h2 id="audit-drawer-title">Audit history</h2>
            <p className="technical-id">{selected.drawingNumber}</p>
          </div>
          <button autoFocus type="button" className="delete-dialog-close" aria-label="Close audit history" onClick={closeAuditDrawer}><X size={18}/></button>
        </header>
        <div className="audit-drawer-list" aria-live="polite">
          {selected.auditHistory.length ? selected.auditHistory.map(item => {
            const timestamp = auditTimestamp(item.occurredAt)
            const changes = metadataChanges(item)
            return <article className="audit-drawer-entry" key={item.id}>
              <span className="audit-drawer-marker" aria-hidden="true"><History size={13}/></span>
              <div>
                <header>
                  <strong>{auditActionLabel(item.action)}</strong>
                  <time dateTime={item.occurredAt}>
                    <span>{timestamp.date}</span>
                    <small>{timestamp.time}</small>
                  </time>
                </header>
                <div className="audit-drawer-meta">
                  <span className="audit-actor"><small>Changed by</small><strong>{item.actor}</strong></span>
                  {item.revisionNumber && <span className="audit-revision">Rev {item.revisionNumber}</span>}
                </div>
                {changes ? <div className="audit-change-summary">
                  <span>{changes.length} field{changes.length === 1 ? '' : 's'} changed</span>
                  <dl>
                    {changes.map(change => <div key={change.label}>
                      <dt>{change.label}</dt>
                      <dd>
                        <span className="audit-value-before">{change.before}</span>
                        <ArrowRight size={13} aria-hidden="true"/>
                        <span className="audit-value-after">{change.after}</span>
                      </dd>
                    </div>)}
                  </dl>
                </div> : <p>{auditDetails(item)}</p>}
              </div>
            </article>
          }) : <div className="audit-drawer-empty"><History size={24}/><strong>No audit entries recorded</strong><p>Future drawing and revision changes will appear here.</p></div>}
        </div>
      </aside>
    </div>}

    {feedback && <ActionFeedbackDialog feedback={feedback} onClose={() => setFeedback(null)}/>}
    {deleteTarget && <div className="delete-dialog-backdrop" onMouseDown={event => { if (event.target === event.currentTarget && !busy) closeDeleteDialog() }}><section className="delete-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title" aria-describedby="delete-dialog-description"><header className="delete-dialog-header"><span className="delete-dialog-icon"><AlertTriangle size={21}/></span><div><span className="eyebrow">Permanent deletion</span><h2 id="delete-dialog-title">Delete {deleteTarget.label}?</h2></div><button type="button" className="delete-dialog-close" aria-label="Close deletion dialog" disabled={busy} onClick={closeDeleteDialog}><X size={18}/></button></header><div className="delete-warning" id="delete-dialog-description"><strong>This action cannot be undone.</strong><p>{deleteTarget.kind === 'revision' ? 'This non-current revision and its stored file package will be permanently removed. The audit entry will remain.' : 'This empty draft drawing record will be permanently removed.'}</p></div><form className="delete-dialog-form" onSubmit={confirmDelete}><label className="delete-acknowledgment"><input autoFocus={deleteTarget.kind === 'revision'} type="checkbox" checked={deleteAcknowledged} onChange={event => setDeleteAcknowledged(event.target.checked)}/><span><strong>I understand this deletion is permanent</strong><small>{deleteTarget.kind === 'revision' ? `File: ${deleteTarget.matchValue}` : 'This is the first required confirmation.'}</small></span></label>{deleteTarget.kind === 'drawing' && <label className="delete-confirmation-field"><span>Type the exact drawing number</span><code>{deleteTarget.matchValue}</code><input autoFocus autoComplete="off" spellCheck={false} value={deleteConfirmation} onChange={event => setDeleteConfirmation(event.target.value)} placeholder={deleteTarget.matchValue}/>{deleteConfirmation && deleteConfirmation !== deleteTarget.matchValue && <small className="delete-match-error">The value does not match exactly.</small>}</label>}{deleteError && <div className="inline-alert" role="alert">{deleteError}</div>}<div className="delete-dialog-actions"><button type="button" className="button ghost" disabled={busy} onClick={closeDeleteDialog}>Cancel</button><button type="submit" className="button danger" disabled={busy || !deleteAcknowledged || (deleteTarget.kind === 'drawing' && deleteConfirmation !== deleteTarget.matchValue)}><Trash2 size={15}/>{busy ? 'Deleting…' : 'Permanently delete'}</button></div></form></section></div>}
  </div>
}
