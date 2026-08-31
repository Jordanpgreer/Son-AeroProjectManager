import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import {
  AlertTriangle,
  Archive,
  ArrowLeft,
  ArrowUpRight,
  Building2,
  CheckCircle2,
  ClipboardCheck,
  Download,
  Eye,
  FileSpreadsheet,
  FileUp,
  History,
  LoaderCircle,
  MapPin,
  PackageCheck,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  ShieldCheck,
  Truck,
  UserRoundCheck,
  Warehouse,
  Wrench,
  X,
} from 'lucide-react'
import HighlightedText from './HighlightedText'
import { SearchableToolLocation, ToolOverview } from './ToolingRecordComponents'
import { engineeringPermissionKeys, hasEngineeringPermission } from './permissions'
import './tooling.css'

interface ToolSummary {
  id: number
  toolNumber: string
  name: string
  toolType: string
  owner: string
  isArchived: boolean
  custodyStatus: 'InStorage' | 'CheckedOut' | 'OutsideProcessing'
  homeLocationId: number | null
  homeLocation: string | null
  currentLocationId: number | null
  currentLocation: string | null
  currentHolder: string | null
  currentVendor: string | null
  checkedOutAt: string | null
  lastAuditDate: string | null
  partNumbers: string[]
  documentCount: number
  notes: string | null
}

interface ToolDashboard {
  tools: ToolSummary[]
  total: number
  inStorage: number
  checkedOut: number
  outsideProcessing: number
  auditOverdue: number
}

interface ToolMovement {
  id: number
  type: string
  locationCode: string | null
  vendor: string | null
  person: string | null
  purpose: string | null
  inspectionConfirmed: boolean | null
  inspectionNotes: string | null
  signedOffBy: string
  recordedAt: string
}

interface ToolDocument {
  id: number
  kind: string
  documentNumber: string | null
  originalFileName: string
  fileType: string
  fileSize: number
  fileHash: string
  notes: string | null
  documentDate: string
  uploadedBy: string
  uploadedAt: string
}

interface ToolAuditEntry { id: number; action: string; details: string; actor: string; occurredAt: string }
interface ToolDetail {
  tool: ToolSummary
  description: string | null
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  version: number
  movements: ToolMovement[]
  documents: ToolDocument[]
  auditHistory: ToolAuditEntry[]
}
interface ToolLocation {
  id: number
  code: string
  description: string | null
  isActive: boolean
  toolCount: number
  assignedToolCount: number
  createdBy: string
  createdAt: string
}

interface ToolCatalogIssue { row: number; column: string | null; message: string }
interface ToolCatalogReview {
  reviewId: string
  expiresAt: string
  fileName: string
  totalRows: number
  newRecords: number
  updatedRecords: number
  unchangedRecords: number
  fieldChanges: number
  errorRows: number
  errors: ToolCatalogIssue[]
  reviewWorkbookUrl: string
  canApply: boolean
}

interface ToolCatalogApplyResult { added: number; updated: number; skipped: number; fieldChanges: number }

export interface ToolRecordHeader {
  toolNumber: string
  name: string
  custodyStatus: ToolSummary['custodyStatus']
  isArchived: boolean
}

type DialogKind = 'create' | 'edit' | 'archive' | 'restore' | 'checkout' | 'checkin' | 'document' | 'locations' | 'import' | null
type ToolFilter = 'active' | 'checkedOut' | 'outsideProcessing' | 'auditAttention'

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: 'include', ...init })
  if (!response.ok) {
    let message = `Request failed (${response.status}).`
    try {
      const body = await response.json() as { message?: string; detail?: string }
      message = body.message ?? body.detail ?? message
    } catch {
      // Keep the status-based fallback.
    }
    throw new Error(message)
  }
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

function displayStatus(status: ToolSummary['custodyStatus']) {
  if (status === 'InStorage') return 'In storage'
  if (status === 'OutsideProcessing') return 'Outside processing'
  return 'Checked out'
}

function statusClass(status: ToolSummary['custodyStatus']) {
  if (status === 'InStorage') return 'tool-status-ok'
  if (status === 'OutsideProcessing') return 'tool-status-vendor'
  return 'tool-status-out'
}

function destination(tool: ToolSummary) {
  if (tool.custodyStatus === 'OutsideProcessing') return tool.currentVendor ?? 'Vendor not specified'
  return tool.currentLocation ?? tool.currentHolder ?? 'Location not assigned'
}

function shortDate(value: string | null) {
  return value ? new Date(value).toLocaleDateString() : 'Not recorded'
}

function longDate(value: string) {
  return new Date(value).toLocaleString()
}

function fileSize(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${Math.round(value / 1024)} KB`
  return `${(value / 1024 / 1024).toFixed(1)} MB`
}

function todayForDateInput() {
  const today = new Date()
  today.setMinutes(today.getMinutes() - today.getTimezoneOffset())
  return today.toISOString().slice(0, 10)
}

function needsAudit(tool: ToolSummary) {
  if (!tool.lastAuditDate) return true
  const auditLimit = new Date()
  auditLimit.setFullYear(auditLimit.getFullYear() - 1)
  return new Date(tool.lastAuditDate) < auditLimit
}

function NativeDialog({ title, eyebrow, onClose, children, wide = false }: {
  title: string
  eyebrow: string
  onClose: () => void
  children: ReactNode
  wide?: boolean
}) {
  return <div className="tool-dialog-backdrop" role="presentation" onMouseDown={event => {
    if (event.currentTarget === event.target) onClose()
  }}>
    <section className={`tool-dialog ${wide ? 'is-wide' : ''}`.trim()} role="dialog" aria-modal="true" aria-labelledby="tool-dialog-title">
      <header>
        <div><span className="eyebrow">{eyebrow}</span><h2 id="tool-dialog-title">{title}</h2></div>
        <button type="button" className="tool-dialog-close" aria-label="Close" onClick={onClose}><X size={18}/></button>
      </header>
      {children}
    </section>
  </div>
}

function splitPartNumbers(value: string) {
  const seen = new Set<string>()
  return value.split(/[;,\n\r]+/).map(part => part.trim()).filter(part => {
    const key = part.toUpperCase().replace(/[^A-Z0-9]/g, '')
    if (!key || seen.has(key)) return false
    seen.add(key)
    return true
  })
}

function PartNumberTagEditor({ initialValues }: { initialValues: string[] }) {
  const [tags, setTags] = useState(initialValues)
  const [draft, setDraft] = useState('')

  function addDraft(value = draft) {
    const additions = splitPartNumbers(value)
    if (!additions.length) return
    setTags(current => splitPartNumbers([...current, ...additions].join(';')))
    setDraft('')
  }

  return <label className="wide tool-part-editor">
    Associated part numbers
    <input type="hidden" name="partNumbers" value={tags.join(';')}/>
    <div className="tool-part-input" onClick={event => event.currentTarget.querySelector('input')?.focus()}>
      {tags.map(tag => <span className="tool-part-tag" key={tag}>{tag}<button type="button" aria-label={`Remove part number ${tag}`} onClick={() => setTags(current => current.filter(value => value !== tag))}><X size={11}/></button></span>)}
      <input
        name="partNumberDraft"
        value={draft}
        required={tags.length === 0}
        maxLength={100}
        placeholder={tags.length ? 'Add another part number' : 'Enter a part number, then press Enter'}
        onChange={event => setDraft(event.target.value)}
        onBlur={() => addDraft()}
        onPaste={event => {
          const pasted = event.clipboardData.getData('text')
          if (!/[;,\n\r]/.test(pasted)) return
          event.preventDefault()
          addDraft(`${draft};${pasted}`)
        }}
        onKeyDown={event => {
          if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); addDraft() }
          if (event.key === 'Backspace' && !draft && tags.length) setTags(current => current.slice(0, -1))
        }}/>
    </div>
    <small className="tool-field-help">Required. Add one or more searchable part numbers; Enter, comma, or semicolon creates a tag.</small>
  </label>
}

function ToolFields({ detail, locations }: { detail?: ToolDetail | null; locations: ToolLocation[] }) {
  return <div className="tool-form-grid">
    <label>Tool number<input name="toolNumber" required maxLength={100} defaultValue={detail?.tool.toolNumber ?? ''} placeholder="TL-204"/></label>
    <label>Tool name<input name="name" required defaultValue={detail?.tool.name ?? ''} placeholder="Housing fixture set"/></label>
    <label>Tool type<input name="toolType" required defaultValue={detail?.tool.toolType ?? ''} placeholder="Machining fixture"/></label>
    <label>Ownership<input name="owner" required defaultValue={detail?.tool.owner ?? 'Son-Aero'} placeholder="Son-Aero or customer name"/></label>
    <label>Default check-in location<select name="homeLocationId" required defaultValue={detail?.tool.homeLocationId ?? ''}>
      <option value="" disabled>Select location</option>
      {locations.filter(location => location.isActive || location.id === detail?.tool.homeLocationId).map(location => <option key={location.id} value={location.id}>{location.code} · {location.description ?? 'No description'}</option>)}
    </select></label>
    <PartNumberTagEditor initialValues={detail?.tool.partNumbers ?? []}/>
    <label className="wide">Description<textarea name="description" rows={3} defaultValue={detail?.description ?? ''} placeholder="Purpose, construction, or identifying details"/></label>
    <label className="wide">Notes<textarea name="notes" rows={3} defaultValue={detail?.tool.notes ?? ''} placeholder="Searchable notes and handling information"/></label>
  </div>
}

export default function ToolingManagement({
  toolId,
  actorName,
  permissions,
  onOpenTool,
  onBack,
  onRecordChange,
  editRequest,
  auditRequest,
  archiveRequest,
}: {
  toolId: number | null
  actorName: string
  permissions: string[]
  onOpenTool: (id: number) => void
  onBack: () => void
  onRecordChange: (header: ToolRecordHeader | null) => void
  editRequest: number
  auditRequest: number
  archiveRequest: number
}) {
  const [dashboard, setDashboard] = useState<ToolDashboard | null>(null)
  const [detail, setDetail] = useState<ToolDetail | null>(null)
  const [locations, setLocations] = useState<ToolLocation[]>([])
  const [query, setQuery] = useState('')
  const [includeArchived, setIncludeArchived] = useState(false)
  const [toolFilter, setToolFilter] = useState<ToolFilter>('active')
  const [previewId, setPreviewId] = useState<number | null>(null)
  const [auditOpen, setAuditOpen] = useState(false)
  const [locationQuery, setLocationQuery] = useState('')
  const [locationStatus, setLocationStatus] = useState<'active' | 'inactive' | 'all'>('active')
  const [locationFeedback, setLocationFeedback] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)
  const [catalogReview, setCatalogReview] = useState<ToolCatalogReview | null>(null)
  const [confirmCatalogErrors, setConfirmCatalogErrors] = useState(false)
  const [dialog, setDialog] = useState<DialogKind>(null)
  const [destinationType, setDestinationType] = useState<'location' | 'vendor'>('location')
  const [destinationLocationId, setDestinationLocationId] = useState<number | null>(null)
  const [checkoutFeedback, setCheckoutFeedback] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const handledEditRequest = useRef(0)
  const handledAuditRequest = useRef(0)
  const handledArchiveRequest = useRef(0)
  const can = (permission: string) => hasEngineeringPermission(permissions, permission)

  async function loadLocations() {
    setLocations(await api<ToolLocation[]>('/api/tool-locations'))
  }

  async function loadDetail(id: number) {
    setLoading(true)
    setError(null)
    try { setDetail(await api<ToolDetail>(`/api/tools/${id}`)) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to load the tool record.') }
    finally { setLoading(false) }
  }

  useEffect(() => { void loadLocations().catch(() => setLocations([])) }, [])

  useEffect(() => {
    if (!toolId || !detail) {
      onRecordChange(null)
      return
    }
    onRecordChange({
      toolNumber: detail.tool.toolNumber,
      name: detail.tool.name,
      custodyStatus: detail.tool.custodyStatus,
      isArchived: detail.tool.isArchived,
    })
    return () => onRecordChange(null)
  }, [detail, onRecordChange, toolId])

  useEffect(() => {
    if (editRequest <= handledEditRequest.current) {
      handledEditRequest.current = editRequest
      return
    }
    handledEditRequest.current = editRequest
    if (detail) setDialog('edit')
  }, [detail, editRequest])

  useEffect(() => {
    if (auditRequest <= handledAuditRequest.current) {
      handledAuditRequest.current = auditRequest
      return
    }
    handledAuditRequest.current = auditRequest
    if (detail) setAuditOpen(true)
  }, [auditRequest, detail])

  useEffect(() => {
    if (archiveRequest <= handledArchiveRequest.current) {
      handledArchiveRequest.current = archiveRequest
      return
    }
    handledArchiveRequest.current = archiveRequest
    if (detail) setDialog(detail.tool.isArchived ? 'restore' : 'archive')
  }, [archiveRequest, detail])

  useEffect(() => {
    if (previewId === null && !auditOpen) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setPreviewId(null)
        setAuditOpen(false)
      }
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [auditOpen, previewId])

  useEffect(() => {
    if (toolId) { void loadDetail(toolId); return }
    setDetail(null)
    const controller = new AbortController()
    const timer = window.setTimeout(async () => {
      setLoading(true)
      setError(null)
      const parameters = new URLSearchParams()
      if (query.trim()) parameters.set('query', query.trim())
      if (includeArchived) parameters.set('includeArchived', 'true')
      try {
        const response = await fetch(`/api/tools?${parameters}`, { credentials: 'include', signal: controller.signal })
        if (!response.ok) throw new Error(`Tool register responded ${response.status}.`)
        setDashboard(await response.json() as ToolDashboard)
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to load tooling.')
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }, 140)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [includeArchived, query, toolId])

  async function refreshDashboard() {
    const parameters = new URLSearchParams()
    if (query.trim()) parameters.set('query', query.trim())
    if (includeArchived) parameters.set('includeArchived', 'true')
    setDashboard(await api<ToolDashboard>(`/api/tools?${parameters}`))
  }

  function selectFilter(filter: ToolFilter) {
    setToolFilter(filter)
    setIncludeArchived(false)
  }

  async function runAction(action: () => Promise<void>, success: string) {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await action()
      setDialog(null)
      setNotice(success)
      if (toolId) await loadDetail(toolId)
      else await refreshDashboard()
      await loadLocations()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The tooling action could not be completed.')
    } finally { setBusy(false) }
  }

  function toolPayload(form: HTMLFormElement) {
    const values = new FormData(form)
    const partNumbers = splitPartNumbers(`${String(values.get('partNumbers') ?? '')};${String(values.get('partNumberDraft') ?? '')}`)
    return {
      toolNumber: String(values.get('toolNumber') ?? ''),
      name: String(values.get('name') ?? ''),
      toolType: String(values.get('toolType') ?? ''),
      owner: String(values.get('owner') ?? ''),
      description: String(values.get('description') ?? ''),
      notes: String(values.get('notes') ?? ''),
      homeLocationId: Number(values.get('homeLocationId')) || detail?.tool.homeLocationId || null,
      partNumbers,
      version: detail?.version ?? null,
    }
  }

  function submitTool(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const payload = toolPayload(event.currentTarget)
    void runAction(async () => {
      if (dialog === 'edit' && detail) {
        await api(`/api/tools/${detail.tool.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) })
      } else {
        const created = await api<ToolDetail>('/api/tools', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) })
        onOpenTool(created.tool.id)
      }
    }, dialog === 'edit' ? 'Tool record updated.' : 'Tool record created.')
  }

  function submitCheckout(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!detail) return
    const values = new FormData(event.currentTarget)
    if (destinationType === 'location' && !destinationLocationId) {
      setCheckoutFeedback('Select an active destination from the filtered list.')
      return
    }
    setCheckoutFeedback(null)
    void runAction(async () => {
      await api(`/api/tools/${detail.tool.id}/checkout`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          destinationType,
          locationId: destinationType === 'location' ? destinationLocationId : null,
          vendor: String(values.get('vendor') ?? ''),
          person: String(values.get('person') ?? ''),
          purpose: String(values.get('purpose') ?? ''),
          inspectionConfirmed: values.get('inspectionConfirmed') === 'on',
          inspectionNotes: String(values.get('inspectionNotes') ?? ''),
        })
      })
    }, destinationType === 'vendor' ? 'Tool released to outside processing.' : 'Tool checked out.')
  }

  function openCheckout() {
    setDestinationType('location')
    setDestinationLocationId(null)
    setCheckoutFeedback(null)
    setDialog('checkout')
  }

  function updateArchiveStatus(isArchived: boolean) {
    if (!detail) return
    void runAction(async () => {
      await api(`/api/tools/${detail.tool.id}/archive`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isArchived, version: detail.version }),
      })
    }, isArchived ? 'Tool archived. Checkout and release actions are disabled.' : 'Tool restored to active service.')
  }

  function submitCheckin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!detail) return
    const values = new FormData(event.currentTarget)
    void runAction(async () => {
      await api(`/api/tools/${detail.tool.id}/checkin`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          locationId: Number(values.get('locationId')),
          person: String(values.get('person') ?? ''),
          purpose: String(values.get('purpose') ?? ''),
        })
      })
    }, 'Tool checked into storage.')
  }

  function submitDocument(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!detail) return
    const body = new FormData(event.currentTarget)
    void runAction(async () => {
      await api(`/api/tools/${detail.tool.id}/documents`, { method: 'POST', body })
    }, 'Document added to the permanent tool record.')
  }

  function submitLocation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = event.currentTarget
    const values = new FormData(event.currentTarget)
    setBusy(true)
    setLocationFeedback(null)
    void api('/api/tool-locations', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: values.get('code'), description: values.get('description') }) })
      .then(async () => {
        form.reset()
        await loadLocations()
        setLocationFeedback({ kind: 'ok', text: 'Location created and available for tool assignments.' })
      })
      .catch(cause => setLocationFeedback({ kind: 'error', text: cause instanceof Error ? cause.message : 'Unable to create the location.' }))
      .finally(() => setBusy(false))
  }

  function toggleLocationStatus(location: ToolLocation) {
    setBusy(true)
    setLocationFeedback(null)
    void api(`/api/tool-locations/${location.id}/status`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ isActive: !location.isActive })
    }).then(async () => {
      await loadLocations()
      setLocationFeedback({ kind: 'ok', text: `${location.code} ${location.isActive ? 'disabled' : 'reactivated'}.` })
    }).catch(cause => setLocationFeedback({ kind: 'error', text: cause instanceof Error ? cause.message : 'Unable to update the location.' }))
      .finally(() => setBusy(false))
  }

  function openCatalogImport() {
    setCatalogReview(null)
    setConfirmCatalogErrors(false)
    setError(null)
    setDialog('import')
  }

  function submitImport(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const body = new FormData(event.currentTarget)
    setBusy(true)
    setError(null)
    setNotice(null)
    void api<ToolCatalogReview>('/api/tools/catalog-import/validate', { method: 'POST', body })
      .then(review => setCatalogReview(review))
      .catch(cause => setError(cause instanceof Error ? cause.message : 'The tool catalogue could not be validated.'))
      .finally(() => setBusy(false))
  }

  function applyCatalog(continueWithErrors: boolean) {
    if (!catalogReview) return
    setBusy(true)
    setError(null)
    void api<ToolCatalogApplyResult>(`/api/tools/catalog-import/${catalogReview.reviewId}/apply`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ continueWithErrors }),
    }).then(async result => {
      setConfirmCatalogErrors(false)
      setDialog(null)
      setCatalogReview(null)
      setNotice(`${result.updated} tool${result.updated === 1 ? '' : 's'} updated, ${result.added} added${result.skipped ? `, and ${result.skipped} invalid row${result.skipped === 1 ? '' : 's'} skipped` : ''}.`)
      await refreshDashboard()
      await loadLocations()
    }).catch(cause => setError(cause instanceof Error ? cause.message : 'The reviewed catalogue changes could not be applied.'))
      .finally(() => setBusy(false))
  }

  if (toolId) {
    if (loading && !detail) return <section className="panel tool-state"><LoaderCircle className="spin"/><span>Loading tool record...</span></section>
    if (!detail) return <section className="panel state-error"><AlertTriangle/><div><strong>Tool record unavailable</strong><p>{error}</p><button className="button" onClick={onBack}>Return to tooling</button></div></section>
    const tool = detail.tool
    return <div className="tooling-workspace tooling-record-view">
      <section className="panel tool-record-banner">
        <button className="tool-back" type="button" onClick={onBack}><ArrowLeft size={15}/><span>Tool register</span></button>
        <div className="tool-record-identity">
          <span className="eyebrow">Controlled tool record</span>
          <div><h2 className="technical-id">{tool.toolNumber}</h2><span className={`tool-status ${statusClass(tool.custodyStatus)}`}>{displayStatus(tool.custodyStatus)}</span></div>
          <p>{tool.name}</p>
        </div>
        <div className="tool-record-actions">
          {can(engineeringPermissionKeys.toolingCustodyManage) && !tool.isArchived && (tool.custodyStatus === 'InStorage'
            ? <button className="button" onClick={openCheckout}><ArrowUpRight size={14}/> Pull tool out</button>
            : <button className="button" onClick={() => setDialog('checkin')}><PackageCheck size={14}/> Check in</button>)}
        </div>
      </section>

      {notice && <div className="tool-notice" role="status"><CheckCircle2 size={16}/>{notice}</div>}
      {error && <div className="tool-error" role="alert"><AlertTriangle size={16}/>{error}</div>}
      {tool.isArchived && <div className="tool-archived-notice" role="status"><Archive size={19}/><div><strong>Archived tool</strong><p>This record is retained for history, but the tool cannot be checked out or released until a permitted manager restores it.</p></div></div>}

      <section className="tool-record-grid">
        <ToolOverview tool={tool} description={detail.description} destination={destination(tool)} shortDate={shortDate} longDate={longDate}/>

        <article className="panel tool-documents-card">
          <header>
            <div><span className="eyebrow">Permanent record</span><h2>Receiving & shipping documents</h2></div>
            {can(engineeringPermissionKeys.toolingDocumentsManage) ? <button type="button" className="tool-card-icon-button" onClick={() => setDialog('document')} aria-label="Upload a receiving or shipping document" title="Upload document"><FileUp size={18}/></button> : <FileUp size={20}/>} 
          </header>
          {detail.documents.length ? <div className="tool-document-list">{detail.documents.map(document => <article key={document.id}>
            <a className="tool-document-open" href={`/api/tool-documents/${document.id}/file`} target="_blank" rel="noreferrer" title={`Open ${document.originalFileName}`}>
              <span className={`tool-doc-kind kind-${document.kind.toLowerCase()}`}>{document.kind === 'Receiving' ? <PackageCheck size={15}/> : <Truck size={15}/>}</span>
              <span><strong>{document.documentNumber || document.originalFileName}</strong><small>{document.originalFileName} · {fileSize(document.fileSize)}</small><small>Document date {shortDate(document.documentDate)} · uploaded {longDate(document.uploadedAt)} by {document.uploadedBy}</small>{document.notes && <small className="tool-document-note">{document.notes}</small>}</span>
            </a>
            <a className="tool-document-download" href={`/api/tool-documents/${document.id}/file?download=true`} aria-label={`Download ${document.originalFileName}`} title={`Download ${document.originalFileName}`}><Download size={15}/></a>
          </article>)}</div> : <div className="tool-empty"><FileUp size={21}/><strong>No documents uploaded</strong><p>Use the upload icon above to attach a dated PDF, Word, or Excel record.</p></div>}
        </article>
      </section>

      <section className="panel tool-history-card">
        <header><div><span className="eyebrow">Append-only custody log</span><h2>Movement & inspection history</h2></div><History size={20}/></header>
        {detail.movements.length ? <div className="tool-history-table"><div className="tool-history-head"><span>Date</span><span>Movement</span><span>Destination</span><span>Responsible / sign-off</span><span>Inspection</span></div>{detail.movements.map(movement => <div className="tool-history-row" key={movement.id}>
          <time dateTime={movement.recordedAt}>{longDate(movement.recordedAt)}</time>
          <strong>{movement.type.replace(/([a-z])([A-Z])/g, '$1 $2')}</strong>
          <span>{movement.vendor ?? movement.locationCode ?? 'Not specified'}{movement.purpose && <small>{movement.purpose}</small>}</span>
          <span>{movement.person ?? 'Not specified'}<small>Signed by {movement.signedOffBy}</small></span>
          <span>{movement.inspectionConfirmed === true ? <em className="inspection-yes"><CheckCircle2 size={13}/> Confirmed</em> : movement.inspectionConfirmed === false ? 'Not confirmed' : 'Not required'}{movement.inspectionNotes && <small>{movement.inspectionNotes}</small>}</span>
        </div>)}</div> : <div className="tool-empty">No movement history.</div>}
      </section>

      {auditOpen && <div className="tool-audit-backdrop" onMouseDown={event => {
        if (event.target === event.currentTarget) setAuditOpen(false)
      }}>
        <aside className="tool-audit-drawer" role="dialog" aria-modal="true" aria-labelledby="tool-audit-title">
          <header className="tool-audit-drawer-header">
            <span className="tool-audit-drawer-icon" aria-hidden="true"><History size={19}/></span>
            <div><span className="eyebrow">Tool record audit</span><h2 id="tool-audit-title">Audit trail</h2><span className="technical-id">{tool.toolNumber}</span></div>
            <button type="button" className="tool-dialog-close" aria-label="Close tool audit" onClick={() => setAuditOpen(false)}><X size={18}/></button>
          </header>
          <div className="tool-audit-drawer-body">
            <div className="tool-audit-summary"><span>Permanent audit trail</span><strong>{detail.auditHistory.length}</strong><small>edits and controlled actions</small></div>
            <div className="tool-audit-timeline">{detail.auditHistory.map(entry => <article key={entry.id}>
              <span className="tool-audit-marker"><History size={13}/></span>
              <div><header><strong>{entry.action.replace(/([a-z])([A-Z])/g, '$1 $2')}</strong><time>{longDate(entry.occurredAt)}</time></header><p>{entry.details}</p><small>{entry.actor}</small></div>
            </article>)}</div>
          </div>
        </aside>
      </div>}

      {(dialog === 'edit' || dialog === 'create') && <NativeDialog title="Edit tool record" eyebrow="Tool identity & ownership" onClose={() => setDialog(null)} wide><form onSubmit={submitTool}><ToolFields detail={detail} locations={locations}/><div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button className="button" disabled={busy}>{busy ? 'Saving...' : 'Save tool'}</button></div></form></NativeDialog>}
      {dialog === 'archive' && <NativeDialog title="Archive this tool?" eyebrow="Manager-controlled action" onClose={() => setDialog(null)}>
        <div className="tool-archive-confirmation is-archive"><AlertTriangle size={25}/><div><strong>Active custody actions will be disabled</strong><p>Archiving removes {tool.toolNumber} from the active tool register and prevents checkout or release until it is restored.</p><p>Existing documents, movement history, and audit records will remain available. A user with archive permission can restore the tool later.</p></div></div>
        <div className="tool-dialog-actions tool-confirm-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Keep active</button><button type="button" className="button danger" disabled={busy} onClick={() => updateArchiveStatus(true)}><Archive size={14}/>{busy ? 'Archiving...' : 'Archive tool'}</button></div>
      </NativeDialog>}
      {dialog === 'restore' && <NativeDialog title="Restore this tool?" eyebrow="Return to active service" onClose={() => setDialog(null)}>
        <div className="tool-archive-confirmation is-restore"><RotateCcw size={25}/><div><strong>Return {tool.toolNumber} to the active register</strong><p>Authorized users will be able to check out and release this tool again after it is restored.</p></div></div>
        <div className="tool-dialog-actions tool-confirm-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button type="button" className="button" disabled={busy} onClick={() => updateArchiveStatus(false)}><RotateCcw size={14}/>{busy ? 'Restoring...' : 'Restore tool'}</button></div>
      </NativeDialog>}
      {dialog === 'checkout' && <NativeDialog title="Pull tool out" eyebrow="Inspection & custody sign-off" onClose={() => setDialog(null)}>
        <form onSubmit={submitCheckout}>
          <div className="inspection-warning"><ClipboardCheck size={22}/><div><strong>Inspection reminder</strong><p>Inspect the tool before it leaves storage. This sign-off becomes part of the permanent custody history.</p></div></div>
          <div className="tool-destination-toggle"><button type="button" className={destinationType === 'location' ? 'active' : ''} onClick={() => { setDestinationType('location'); setCheckoutFeedback(null) }}><MapPin size={15}/> Internal location</button><button type="button" className={destinationType === 'vendor' ? 'active' : ''} onClick={() => { setDestinationType('vendor'); setCheckoutFeedback(null) }}><Building2 size={15}/> Outside processing</button></div>
          <div className="tool-dialog-fields">
            {destinationType === 'location'
              ? <SearchableToolLocation locations={locations} selectedId={destinationLocationId} onSelect={id => { setDestinationLocationId(id); setCheckoutFeedback(null) }}/>
              : <label>Outside processing vendor<input name="vendor" required placeholder="Search or enter vendor company name"/></label>}
            <label>Responsible person<input name="person" required defaultValue={actorName} readOnly={!can(engineeringPermissionKeys.toolingCustodyAssigneeManage)} aria-describedby="responsible-person-help"/><small id="responsible-person-help" className="tool-field-help">{can(engineeringPermissionKeys.toolingCustodyAssigneeManage) ? 'You have permission to assign custody to another person.' : 'Defaults to your Engineering display name. Additional permission is required to change it.'}</small></label>
            <label>Purpose<input name="purpose" placeholder="Work order, operation, or reason"/></label>
            <label>Inspection notes<textarea name="inspectionNotes" rows={3} placeholder="Condition observed before release"/></label>
          </div>
          {checkoutFeedback && <div className="tool-inline-error" role="alert"><AlertTriangle size={15}/>{checkoutFeedback}</div>}
          <label className="inspection-confirm"><input type="checkbox" name="inspectionConfirmed" required/><span><strong>I inspected this tool before release</strong><small>Digital sign-off: {actorName}</small></span></label>
          <div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button className="button" disabled={busy}><UserRoundCheck size={14}/>{busy ? 'Recording...' : 'Sign off & release'}</button></div>
        </form>
      </NativeDialog>}
      {dialog === 'checkin' && <NativeDialog title="Check tool into storage" eyebrow="Return custody" onClose={() => setDialog(null)}><form onSubmit={submitCheckin}><div className="tool-dialog-fields"><label>Storage location<select name="locationId" required defaultValue={tool.homeLocationId ?? ''}><option value="" disabled>Select storage location</option>{locations.filter(x => x.isActive).map(location => <option value={location.id} key={location.id}>{location.code} · {location.description ?? 'No description'}{location.id === tool.homeLocationId ? ' (default)' : ''}</option>)}</select><small className="tool-field-help">Defaults to this tool's normal bin. Select another active location for this check-in only.</small></label><label>Returned by<input name="person" defaultValue={tool.currentHolder ?? actorName}/></label><label>Return notes<textarea name="purpose" rows={3} placeholder="Condition, receiving reference, or notes"/></label></div><div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button className="button" disabled={busy}>{busy ? 'Recording...' : 'Check into storage'}</button></div></form></NativeDialog>}
      {dialog === 'document' && <NativeDialog title="Attach tool document" eyebrow="Permanent receiving / shipping history" onClose={() => setDialog(null)}><form onSubmit={submitDocument}><div className="tool-dialog-fields"><label>Document type<select name="kind" required defaultValue="Receiving"><option>Receiving</option><option>Shipping</option></select></label><label>Document number<input name="documentNumber" placeholder="Packing slip, PO, or shipment number"/></label><label>Document date<input type="date" name="documentDate" required max={todayForDateInput()} defaultValue={todayForDateInput()}/><small className="tool-field-help">Required. Today or an earlier date only.</small></label><label>File<input type="file" name="document" accept=".pdf,.doc,.docx,.xls,.xlsx,application/pdf,application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,application/vnd.ms-excel,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" required/></label><label>Notes<textarea name="notes" rows={3} placeholder="Searchable context for this document"/></label></div><div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button className="button" disabled={busy}>{busy ? 'Uploading...' : 'Upload document'}</button></div></form></NativeDialog>}
    </div>
  }

  const visibleTools = (dashboard?.tools ?? []).filter(tool => {
    if (toolFilter === 'checkedOut') return tool.custodyStatus === 'CheckedOut'
    if (toolFilter === 'outsideProcessing') return tool.custodyStatus === 'OutsideProcessing'
    if (toolFilter === 'auditAttention') return needsAudit(tool)
    return true
  })
  const previewTool = dashboard?.tools.find(tool => tool.id === previewId) ?? null
  const filterLabel = toolFilter === 'checkedOut' ? 'Checked out'
    : toolFilter === 'outsideProcessing' ? 'Outside processing'
      : toolFilter === 'auditAttention' ? 'Audit attention'
        : includeArchived ? 'All tool records' : 'Active tool records'
  const hasDashboardActions = can(engineeringPermissionKeys.toolingAuditImport)
    || can(engineeringPermissionKeys.toolingLocationsManage)
    || can(engineeringPermissionKeys.toolingRecordsManage)
  const visibleLocations = locations.filter(location => {
    if (locationStatus === 'active' && !location.isActive) return false
    if (locationStatus === 'inactive' && location.isActive) return false
    const term = locationQuery.trim().toLowerCase()
    return !term || location.code.toLowerCase().includes(term) || location.description?.toLowerCase().includes(term)
  })

  return <div className="tooling-workspace">
    {hasDashboardActions && <div className="tooling-dashboard-actions" aria-label="Tool register actions">
      {can(engineeringPermissionKeys.toolingAuditImport) && <button className="button ghost" onClick={openCatalogImport}><FileSpreadsheet size={14}/> Catalogue update</button>}
      {can(engineeringPermissionKeys.toolingLocationsManage) && <button className="button ghost" onClick={() => setDialog('locations')}><Warehouse size={14}/> Locations</button>}
      {can(engineeringPermissionKeys.toolingRecordsManage) && <button className="button" onClick={() => setDialog('create')}><Plus size={14}/> Add tool</button>}
    </div>}

    {notice && <div className="tool-notice" role="status"><CheckCircle2 size={16}/>{notice}</div>}
    {error && <div className="tool-error" role="alert"><AlertTriangle size={16}/>{error}</div>}

    <section className="tool-kpis">
      <button type="button" className="panel" aria-pressed={toolFilter === 'active'} onClick={() => selectFilter('active')}><span><Wrench size={16}/> Tool records</span><strong>{dashboard?.total ?? 0}</strong><small>Show all active tools</small></button>
      <button type="button" className="panel is-out" aria-pressed={toolFilter === 'checkedOut'} onClick={() => selectFilter('checkedOut')}><span><ArrowUpRight size={16}/> Checked out</span><strong>{dashboard?.checkedOut ?? 0}</strong><small>Internal custody</small></button>
      <button type="button" className="panel is-vendor" aria-pressed={toolFilter === 'outsideProcessing'} onClick={() => selectFilter('outsideProcessing')}><span><Truck size={16}/> Outside processing</span><strong>{dashboard?.outsideProcessing ?? 0}</strong><small>At specified vendors</small></button>
      <button type="button" className="panel is-audit" aria-pressed={toolFilter === 'auditAttention'} onClick={() => selectFilter('auditAttention')}><span><ClipboardCheck size={16}/> Audit attention</span><strong>{dashboard?.auditOverdue ?? 0}</strong><small>Missing or older than one year</small></button>
    </section>

    <section className="panel tool-register">
      <header className="tool-register-toolbar">
        <label className="tool-search"><Search size={17}/><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search tool, part number, owner, location, vendor, document, or note" aria-label="Search the tool register"/></label>
        <label className="tool-archive-filter"><input type="checkbox" checked={includeArchived} onChange={event => setIncludeArchived(event.target.checked)}/> Include archived</label>
        <button type="button" className="tool-refresh" onClick={() => void refreshDashboard()} aria-label="Refresh tool register"><RefreshCw size={15}/></button>
      </header>
      <div className="tool-result-summary"><span>{loading ? 'Searching tool records...' : `${visibleTools.length} matching tool${visibleTools.length === 1 ? '' : 's'} · ${filterLabel}`}</span>{query && <small>Matching text is highlighted in every result row.</small>}</div>
      <div className="tool-table-wrap">
        <table className="tool-table"><thead><tr><th>Tool</th><th>Type / owner</th><th>Status</th><th>Physical location / vendor</th><th>Last audit</th><th>Documents</th></tr></thead><tbody>
          {visibleTools.map(tool => <tr key={tool.id} className={`tool-result-row ${tool.isArchived ? 'is-archived' : ''}`.trim()} role="link" tabIndex={0} aria-label={`Open tool ${tool.toolNumber}: ${tool.name}`} onClick={() => onOpenTool(tool.id)} onKeyDown={event => {
            if (event.target === event.currentTarget && event.key === 'Enter') onOpenTool(tool.id)
          }}>
            <td><span className="tool-number-line"><strong className="technical-id"><HighlightedText value={tool.toolNumber} query={query}/></strong><button type="button" className="tool-preview-trigger" aria-label={`Preview tool ${tool.toolNumber}`} aria-expanded={previewId === tool.id} onClick={event => { event.stopPropagation(); setPreviewId(tool.id) }}><Eye size={14}/></button></span><span><HighlightedText value={tool.name} query={query}/></span>{tool.partNumbers.length > 0 && <span className="tool-row-parts">{tool.partNumbers.map(part => <span key={part}><HighlightedText value={part} query={query}/></span>)}</span>}</td>
            <td><strong><HighlightedText value={tool.toolType} query={query}/></strong><span><HighlightedText value={tool.owner} query={query}/></span></td>
            <td><span className={`tool-status ${statusClass(tool.custodyStatus)}`}><HighlightedText value={displayStatus(tool.custodyStatus)} query={query}/></span>{tool.isArchived && <small>Archived</small>}</td>
            <td><strong className="technical-id"><HighlightedText value={destination(tool)} query={query}/></strong><span>Home bin: <HighlightedText value={tool.homeLocation ?? 'Not assigned'} query={query}/></span>{tool.currentHolder && <small><HighlightedText value={tool.currentHolder} query={query}/></small>}</td>
            <td><span>{shortDate(tool.lastAuditDate)}</span>{!tool.lastAuditDate && <small className="audit-missing">Audit needed</small>}</td>
            <td><span>{tool.documentCount}</span><small>Receiving / shipping</small></td>
          </tr>)}
          {!loading && visibleTools.length === 0 && <tr><td colSpan={6}><div className="tool-empty"><Search size={22}/><strong>No tools match this view</strong><p>Try another filter or search by tool number, part number, owner, location, vendor, document number, or note keyword.</p></div></td></tr>}
        </tbody></table>
      </div>
    </section>

    {dialog === 'create' && <NativeDialog title="Create tool record" eyebrow="Tool identity & physical assignment" onClose={() => setDialog(null)} wide><form onSubmit={submitTool}><ToolFields locations={locations}/><div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setDialog(null)}>Cancel</button><button className="button" disabled={busy}>{busy ? 'Creating...' : 'Create tool'}</button></div></form></NativeDialog>}
    {dialog === 'locations' && <NativeDialog title="Physical location registry" eyebrow="Searchable tool-bin administration" onClose={() => setDialog(null)} wide>
      <div className="location-registry">
        <form className="location-create-row" onSubmit={submitLocation}><div><span className="eyebrow">Create location</span><strong>Add a new controlled bin or area</strong></div><label>Location code<input name="code" required placeholder="A001-002"/></label><label>Description<input name="description" placeholder="Aisle, rack, bin, or area"/></label><button className="button" disabled={busy}><Plus size={14}/>{busy ? 'Creating...' : 'Add location'}</button></form>
        {locationFeedback && <div className={`location-feedback is-${locationFeedback.kind}`} role="status">{locationFeedback.kind === 'ok' ? <CheckCircle2 size={15}/> : <AlertTriangle size={15}/>}<span>{locationFeedback.text}</span></div>}
        <section className="location-browser">
          <header className="location-browser-toolbar"><label className="location-search"><Search size={15}/><input value={locationQuery} onChange={event => setLocationQuery(event.target.value)} placeholder="Search location code or description" aria-label="Search tool locations"/></label><label className="location-status-filter"><span>Status</span><select value={locationStatus} onChange={event => setLocationStatus(event.target.value as typeof locationStatus)}><option value="active">Active</option><option value="inactive">Inactive</option><option value="all">All locations</option></select></label></header>
          <div className="location-result-summary"><span>{visibleLocations.length} of {locations.length} locations</span><small>Assigned tools use this as their default check-in bin.</small></div>
          <div className="location-table-wrap"><table className="location-table"><thead><tr><th>Location</th><th>Description</th><th>Assigned tools</th><th>Stored now</th><th>Status</th><th aria-label="Location actions"/></tr></thead><tbody>
            {visibleLocations.map(location => <tr key={location.id}><td><span className="location-code"><MapPin size={13}/><HighlightedText value={location.code} query={locationQuery}/></span></td><td><HighlightedText value={location.description ?? 'No description'} query={locationQuery}/></td><td><strong>{location.assignedToolCount}</strong></td><td><strong>{location.toolCount}</strong></td><td><span className={`location-status is-${location.isActive ? 'active' : 'inactive'}`}>{location.isActive ? 'Active' : 'Inactive'}</span></td><td><button type="button" disabled={busy} onClick={() => toggleLocationStatus(location)}>{location.isActive ? 'Disable' : 'Reactivate'}</button></td></tr>)}
            {visibleLocations.length === 0 && <tr><td colSpan={6}><div className="tool-empty"><MapPin size={20}/><strong>No matching locations</strong><p>Adjust the search or status filter.</p></div></td></tr>}
          </tbody></table></div>
        </section>
      </div>
    </NativeDialog>}
    {dialog === 'import' && <NativeDialog title="Tool catalogue update" eyebrow="Controlled two-step workbook import" onClose={() => { setDialog(null); setCatalogReview(null) }} wide>
      <div className="catalog-import-workflow">
        {error && <div className="tool-error" role="alert"><AlertTriangle size={16}/>{error}</div>}
        <section className="catalog-import-step">
          <span className="catalog-step-number">1</span>
          <div><span className="eyebrow">Start with current data</span><h3>Download the tool catalogue</h3><p>The workbook includes every registered tool, status, assignment, audit date, owner, and part-number tags. Tools omitted from a later upload are not removed.</p></div>
          <a className="button ghost" href="/api/tools/catalog-export" download><Download size={14}/> Download Excel</a>
        </section>
        <section className="catalog-import-step">
          <span className="catalog-step-number">2</span>
          <div><span className="eyebrow">Review before saving</span><h3>Upload the edited workbook</h3><p>The system compares each included row to the live catalogue, reports changes and new tools, and checks every row for errors.</p></div>
          <form className="catalog-upload-form" onSubmit={submitImport}><label>Edited Excel workbook<input type="file" name="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" required onChange={() => setCatalogReview(null)}/></label><button className="button" disabled={busy}><FileUp size={14}/>{busy ? 'Checking workbook...' : 'Validate workbook'}</button></form>
        </section>
        {catalogReview && <section className={`catalog-review ${catalogReview.errorRows ? 'has-errors' : 'is-ready'}`}>
          <header><div><span className="eyebrow">Upload comparison</span><h3>{catalogReview.errorRows ? 'Review needed' : 'Ready to apply'}</h3></div><span className={`catalog-review-status ${catalogReview.errorRows ? 'has-errors' : 'is-ready'}`}>{catalogReview.errorRows ? `${catalogReview.errorRows} error row${catalogReview.errorRows === 1 ? '' : 's'}` : 'No errors'}</span></header>
          <div className="catalog-review-metrics">
            <div><span>New tools</span><strong>{catalogReview.newRecords}</strong></div>
            <div><span>Tool updates</span><strong>{catalogReview.updatedRecords}</strong></div>
            <div><span>Field changes</span><strong>{catalogReview.fieldChanges}</strong></div>
            <div><span>Unchanged</span><strong>{catalogReview.unchangedRecords}</strong></div>
          </div>
          {catalogReview.errors.length > 0 && <div className="catalog-error-list"><strong>Workbook errors</strong>{catalogReview.errors.slice(0, 8).map((issue, index) => <div key={`${issue.row}-${issue.column}-${index}`}><span>Row {issue.row}{issue.column ? ` · ${issue.column}` : ''}</span><p>{issue.message}</p></div>)}{catalogReview.errors.length > 8 && <small>Plus {catalogReview.errors.length - 8} more errors in the annotated workbook.</small>}</div>}
          <div className="catalog-review-actions">
            {catalogReview.errors.length > 0 && <a className="button ghost" href={catalogReview.reviewWorkbookUrl} download><Download size={14}/> Download annotated workbook</a>}
            <button className="button" type="button" disabled={busy || !catalogReview.canApply} onClick={() => catalogReview.errorRows ? setConfirmCatalogErrors(true) : applyCatalog(false)}>{busy ? 'Applying...' : catalogReview.errorRows ? 'Continue with valid rows' : 'Apply reviewed changes'}</button>
          </div>
        </section>}
        {!catalogReview && <div className="catalog-import-note"><ShieldCheck size={18}/><p>No catalogue change is saved until validation finishes and you choose to apply the reviewed results.</p></div>}
        <div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => { setDialog(null); setCatalogReview(null) }}>Close</button></div>
      </div>
    </NativeDialog>}

    {confirmCatalogErrors && catalogReview && <NativeDialog title="Errors remain in this workbook" eyebrow="Confirm controlled partial import" onClose={() => setConfirmCatalogErrors(false)}>
      <div className="catalog-force-warning"><AlertTriangle size={24}/><div><strong>{catalogReview.errorRows} invalid row{catalogReview.errorRows === 1 ? '' : 's'} will be skipped</strong><p>The valid {catalogReview.newRecords + catalogReview.updatedRecords} changed record{catalogReview.newRecords + catalogReview.updatedRecords === 1 ? '' : 's'} will be applied. Invalid rows will not be partially updated, and tools omitted from the workbook will remain unchanged.</p><p>Downloading the annotated workbook and correcting its highlighted cells is recommended.</p></div></div>
      {error && <div className="tool-error catalog-confirm-error" role="alert"><AlertTriangle size={16}/>{error}</div>}
      <div className="tool-dialog-actions"><button type="button" className="button ghost" onClick={() => setConfirmCatalogErrors(false)}>Go back and correct</button><button type="button" className="button danger" disabled={busy} onClick={() => applyCatalog(true)}>{busy ? 'Applying valid rows...' : 'Confirm and continue'}</button></div>
    </NativeDialog>}

    {previewTool && <div className="tool-preview-backdrop" onMouseDown={event => {
      if (event.target === event.currentTarget) setPreviewId(null)
    }}>
      <aside className="tool-preview-drawer" role="dialog" aria-modal="true" aria-labelledby="tool-preview-title">
        <header className="tool-preview-header">
          <span className="tool-preview-icon" aria-hidden="true"><Eye size={19}/></span>
          <div><span className="eyebrow">Tool preview</span><h2 id="tool-preview-title">{previewTool.name}</h2><span className="technical-id">{previewTool.toolNumber}</span></div>
          <button type="button" className="tool-dialog-close" aria-label="Close tool preview" onClick={() => setPreviewId(null)}><X size={18}/></button>
        </header>
        <div className="tool-preview-body">
          <section className="tool-preview-status">
            <div><span>Custody</span><strong className={`tool-status ${statusClass(previewTool.custodyStatus)}`}>{displayStatus(previewTool.custodyStatus)}</strong></div>
            <div><span>Tool type</span><strong>{previewTool.toolType}</strong></div>
            <div><span>Owner</span><strong>{previewTool.owner}</strong></div>
            <div><span>Last physical audit</span><strong>{shortDate(previewTool.lastAuditDate)}</strong></div>
          </section>
          <section className="tool-preview-section">
            <header><span>Current physical assignment</span></header>
            <div><MapPin size={17}/><span><strong>{destination(previewTool)}</strong><small>{previewTool.currentHolder ?? (previewTool.custodyStatus === 'InStorage' ? 'Tool crib custody' : 'Responsible person not specified')}</small></span></div>
          </section>
          <section className="tool-preview-section">
            <header><span>Default check-in location</span></header>
            <div><Warehouse size={17}/><span><strong>{previewTool.homeLocation ?? 'Not assigned'}</strong><small>Normal return bin for this tool</small></span></div>
          </section>
          <section className="tool-preview-section">
            <header><span>Associated part numbers</span><b>{previewTool.partNumbers.length}</b></header>
            <div className="tool-preview-parts">{previewTool.partNumbers.map(part => <span className="tool-part-tag" key={part}>{part}</span>)}</div>
          </section>
          <section className="tool-preview-section">
            <header><span>Attached logistics records</span><b>{previewTool.documentCount}</b></header>
            <p>{previewTool.documentCount ? `${previewTool.documentCount} receiving or shipping document${previewTool.documentCount === 1 ? '' : 's'} attached.` : 'No receiving or shipping documents are attached.'}</p>
          </section>
          {previewTool.notes && <section className="tool-preview-section"><header><span>Record notes</span></header><p>{previewTool.notes}</p></section>}
          <div className="tool-preview-actions"><button className="button" type="button" onClick={() => { const id = previewTool.id; setPreviewId(null); onOpenTool(id) }}><Wrench size={14}/> Open full tool record</button></div>
        </div>
      </aside>
    </div>}
  </div>
}
