import { startTransition, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertTriangle,
  ArrowDown,
  ArrowDownUp,
  ArrowUp,
  ChevronRight,
  ClipboardList,
  Clock3,
  Download,
  ExternalLink,
  FileSpreadsheet,
  FileClock,
  FileUp,
  ListFilter,
  MessageSquare,
  PackageCheck,
  Pencil,
  Plus,
  RotateCcw,
  Search,
  SlidersHorizontal,
  UserRoundCheck,
  X,
} from 'lucide-react'
import { qualityApi } from './api'
import CustomerFilterCombobox from './CustomerFilterCombobox'
import { ageInDays, formatCurrency, formatDate, formatDateTime } from './format'
import ShipmentCommentsDrawer from './ShipmentCommentsDrawer'
import { readShipmentDeepLink } from './shippingDeepLink'
import { normalizeShippingScope } from './shippingScope'
import type { ShippingScope } from './shippingScope'
import type {
  AssignmentOptions,
  AuditEntry,
  FieldAccess,
  QualityAssuranceUser,
  Shipment,
  ShipmentFieldKey,
  ShipmentList,
} from './types'

const PERMISSIONS = {
  create: 'quality-assurance.shipments.create',
  import: 'quality-assurance.shipments.import',
  assignmentView: 'quality-assurance.assignments.view',
  assignmentGroup: 'quality-assurance.assignments.group',
  assignmentUser: 'quality-assurance.assignments.user',
  managerReview: 'quality-assurance.dashboard.manager-review',
  ship: 'quality-assurance.shipments.mark-shipped',
  audit: 'quality-assurance.audit.view',
  viewAll: 'quality-assurance.shipments.view-all',
  teamView: 'quality-assurance.dashboard.team-view',
} as const

const STATUS_OPTIONS = [
  'WIP',
  'Ready to Ship',
  'Pending Customer Service/Sales',
  'Pending Customer Feedback',
  'Pending Source Inspection',
  'Pending FAI Approval Portal',
  'On Hold',
]

const TASK_TYPES = ['General', 'Source Inspection', 'FAI Approval', 'Customer Feedback', 'Documentation', 'Final Review']

interface ShipmentDraft {
  status: string
  salesOrderNumber: string
  qaArrivalDate: string
  purchaseOrderNumber: string
  customer: string
  taskType: string
  shipDate: string
  holdReason: string
  sourceRequestedDate: string
  comments: string
  parts: ShipmentPartDraft[]
}

interface ShipmentPartDraft {
  partNumber: string
  quantity: string
  unitPrice: string
}

interface ShippingImportResult {
  rowsRead: number
  createdRecords: number
  skippedDuplicates: number
  reconciledAssignments: number
  worksheet: string
}

type ScalarDraftKey = Exclude<keyof ShipmentDraft, 'parts'>

const FIELD_KEYS: ScalarDraftKey[] = [
  'status', 'salesOrderNumber', 'qaArrivalDate', 'purchaseOrderNumber',
  'customer', 'taskType', 'shipDate', 'holdReason',
  'sourceRequestedDate', 'comments',
]

function draftFor(shipment?: Shipment | null): ShipmentDraft {
  return {
    status: shipment?.status ?? 'WIP',
    salesOrderNumber: shipment?.salesOrderNumber ?? '',
    qaArrivalDate: shipment?.qaArrivalDate ?? '',
    purchaseOrderNumber: shipment?.purchaseOrderNumber ?? '',
    customer: shipment?.customer ?? '',
    taskType: shipment?.taskType ?? 'General',
    shipDate: shipment?.shipDate ?? '',
    holdReason: shipment?.holdReason ?? '',
    sourceRequestedDate: shipment?.sourceRequestedDate ?? '',
    comments: shipment?.comments ?? '',
    parts: shipment?.parts?.length
      ? shipment.parts.map((part) => ({
        partNumber: part.partNumber,
        quantity: part.quantity?.toString() ?? '',
        unitPrice: part.unitPrice?.toFixed(2) ?? '',
      }))
      : [{
        partNumber: shipment?.partNumber ?? '',
        quantity: shipment?.quantity?.toString() ?? '',
        unitPrice: shipment?.quantity && shipment.dollarValue != null
          ? (shipment.dollarValue / shipment.quantity).toFixed(2)
          : '',
      }],
  }
}

function fieldValue(draft: ShipmentDraft, key: ScalarDraftKey) {
  const value = draft[key]
  return value === '' ? null : value
}

function partValues(draft: ShipmentDraft) {
  return draft.parts.map((part) => ({
    partNumber: part.partNumber.trim(),
    quantity: part.quantity === '' ? null : Number(part.quantity),
    unitPrice: part.unitPrice === '' ? null : Number(part.unitPrice),
  }))
}

function Highlight({ value, query }: { value: string; query: string }) {
  const search = query.trim()
  if (!search) return value
  const index = value.toLowerCase().indexOf(search.toLowerCase())
  if (index < 0) return value
  return <>{value.slice(0, index)}<mark>{value.slice(index, index + search.length)}</mark>{value.slice(index + search.length)}</>
}

type WorklistColumnKey =
  | 'status'
  | 'salesOrderNumber'
  | 'qaArrivalDate'
  | 'partNumber'
  | 'purchaseOrderNumber'
  | 'customer'
  | 'quantity'
  | 'dollarValue'
  | 'shipDate'
  | 'holdReason'
  | 'sourceRequestedDate'
  | 'action'
  | 'lastWorkedAt'
  | 'comments'
  | 'queueAge'

interface WorklistColumn {
  key: WorklistColumnKey
  label: string
  width: number
}

const SORT_PARAMETERS: Record<WorklistColumnKey, string> = {
  status: 'status',
  salesOrderNumber: 'sales-order',
  qaArrivalDate: 'qa-arrival',
  partNumber: 'part-number',
  purchaseOrderNumber: 'purchase-order',
  customer: 'customer',
  quantity: 'quantity',
  dollarValue: 'dollar-value',
  shipDate: 'ship-date',
  holdReason: 'hold-reason',
  sourceRequestedDate: 'source-scheduled',
  action: 'action',
  lastWorkedAt: 'last-worked',
  comments: 'comments',
  queueAge: 'queue-age',
}

function actionOwner(shipment: Shipment, canViewAssignment: boolean) {
  if (!canViewAssignment) {
    return {
      primary: shipment.nextAction || 'Assignment Hidden',
      secondary: shipment.nextAction ? 'Current Action Owner' : 'No Owner Available',
      isUnassigned: !shipment.nextAction,
    }
  }
  if (shipment.assignedDisplayName) {
    return {
      primary: shipment.assignedDisplayName,
      secondary: shipment.assignedGroupName || 'Individual Owner',
      isUnassigned: false,
    }
  }
  if (shipment.assignedGroupName) {
    return {
      primary: 'Group Queue',
      secondary: shipment.assignedGroupName,
      isUnassigned: false,
    }
  }
  return {
    primary: shipment.nextAction || 'Unassigned',
    secondary: shipment.nextAction ? 'Not Linked To An Active User' : 'Needs Assignment',
    isUnassigned: true,
  }
}

function ShippingImportDialog({
  onClose,
  onImported,
}: {
  onClose: () => void
  onImported: () => void
}) {
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ShippingImportResult | null>(null)

  async function importWorkbook() {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      const form = new FormData()
      form.append('file', file)
      const next = await qualityApi<ShippingImportResult>('/api/shipments/import', {
        method: 'POST',
        body: form,
      })
      setResult(next)
      onImported()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The Shipping Status workbook could not be imported.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="modal-backdrop" role="presentation">
      <section className="modal shipping-import-modal" role="dialog" aria-modal="true" aria-labelledby="shipping-import-title">
        <header>
          <div><span className="eyebrow">Controlled Bulk Entry</span><h2 id="shipping-import-title">Import Shipping Status</h2><p>Only the <b>Complete List</b> worksheet is read. Existing exact records are skipped.</p></div>
          <button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button>
        </header>
        <div className="shipping-import-body">
          {!result && <label className="shipping-file-picker">
            <FileSpreadsheet size={24} aria-hidden="true" />
            <span><strong>{file?.name ?? 'Choose An Excel Workbook'}</strong><small>Accepted format: .xlsx, up to 25 MB</small></span>
            <input type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" onChange={(event) => { setFile(event.currentTarget.files?.[0] ?? null); setError(null) }} />
          </label>}
          {result && <div className="shipping-import-result">
            <FileSpreadsheet size={28} aria-hidden="true" />
            <div><strong>Import Complete</strong><p>{result.createdRecords} records created, {result.skippedDuplicates} exact duplicates skipped, and {result.reconciledAssignments} legacy assignments corrected from {result.rowsRead} rows in <b>{result.worksheet}</b>.</p></div>
          </div>}
          {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
        </div>
        <footer>
          <button className="button ghost" type="button" onClick={onClose}>{result ? 'Close' : 'Cancel'}</button>
          {!result && <button className="button primary" type="button" disabled={!file || busy} onClick={() => void importWorkbook()}><FileUp size={15} />{busy ? 'Importing...' : 'Import workbook'}</button>}
        </footer>
      </section>
    </div>
  )
}

function ShipmentForm({
  shipment,
  fields,
  onClose,
  onSaved,
}: {
  shipment?: Shipment | null
  fields: FieldAccess[]
  onClose: () => void
  onSaved: (shipment: Shipment) => void
}) {
  const [draft, setDraft] = useState(() => draftFor(shipment))
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const editable = useMemo(() => new Map(fields.map((field) => [field.key, field.canEdit])), [fields])
  const creating = !shipment

  function update(key: ScalarDraftKey, value: string) {
    setDraft((current) => ({ ...current, [key]: value }))
  }

  function updatePart(index: number, key: keyof ShipmentPartDraft, value: string) {
    setDraft((current) => ({
      ...current,
      parts: current.parts.map((part, partIndex) => partIndex === index
        ? { ...part, [key]: value }
        : part),
    }))
  }

  function addPart() {
    setDraft((current) => ({
      ...current,
      parts: [...current.parts, { partNumber: '', quantity: '', unitPrice: '' }],
    }))
  }

  function removePart(index: number) {
    setDraft((current) => ({
      ...current,
      parts: current.parts.filter((_, partIndex) => partIndex !== index),
    }))
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      let saved: Shipment
      const parts = partValues(draft)
      if (creating) {
        const body: Record<string, unknown> = Object.fromEntries(FIELD_KEYS
          .filter((key) => editable.get(key as ShipmentFieldKey))
          .map((key) => [key, fieldValue(draft, key)]))
        const firstPart = parts[0]
        Object.assign(body, {
          partNumber: firstPart?.partNumber ?? null,
          quantity: firstPart?.quantity ?? null,
          dollarValue: firstPart?.quantity != null && firstPart.unitPrice != null
            ? firstPart.quantity * firstPart.unitPrice
            : null,
          parts,
        })
        saved = await qualityApi<Shipment>('/api/shipments', { method: 'POST', body: JSON.stringify(body) })
      } else {
        const original = draftFor(shipment)
        const changes: Record<string, unknown> = Object.fromEntries(FIELD_KEYS
          .filter((key) => editable.get(key as ShipmentFieldKey))
          .filter((key) => fieldValue(draft, key) !== fieldValue(original, key))
          .map((key) => [key, fieldValue(draft, key)]))
        if (JSON.stringify(parts) !== JSON.stringify(partValues(original))) changes.parts = parts
        if (!Object.keys(changes).length) { onClose(); return }
        saved = await qualityApi<Shipment>(`/api/shipments/${shipment.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ version: shipment.version, changes }),
        })
      }
      onSaved(saved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Shipment could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const can = (key: ShipmentFieldKey) => editable.get(key) === true
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <section className="modal shipment-form-modal" role="dialog" aria-modal="true" aria-labelledby="shipment-form-title">
        <header><div><span className="eyebrow">{creating ? 'New Queue Item' : `Shipment ${shipment.salesOrderNumber ?? shipment.id}`}</span><h2 id="shipment-form-title">{creating ? 'Add Shipping Status Record' : 'Edit Shipment Details'}</h2></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button></header>
        <form onSubmit={submit}>
          {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
          <div className="form-grid">
            {can('status') && <label><span>Status</span><select value={draft.status} onChange={(event) => update('status', event.target.value)}>{STATUS_OPTIONS.map((status) => <option key={status}>{status}</option>)}</select></label>}
            {can('taskType') && <label><span>Task Type</span><input list="qa-task-types" value={draft.taskType} onChange={(event) => update('taskType', event.target.value)} /><datalist id="qa-task-types">{TASK_TYPES.map((type) => <option key={type}>{type}</option>)}</datalist></label>}
            {can('salesOrderNumber') && <label><span>Shipper Number</span><input required value={draft.salesOrderNumber} onChange={(event) => update('salesOrderNumber', event.target.value)} /></label>}
            {can('purchaseOrderNumber') && <label><span>P.O.</span><input required value={draft.purchaseOrderNumber} onChange={(event) => update('purchaseOrderNumber', event.target.value)} /></label>}
            {can('customer') && <label><span>Customer</span><input required value={draft.customer} onChange={(event) => update('customer', event.target.value)} /></label>}
            {can('qaArrivalDate') && <label><span>Shipment Arrival Date</span><input required type="date" value={draft.qaArrivalDate} onChange={(event) => update('qaArrivalDate', event.target.value)} /></label>}
            {can('shipDate') && <label><span>Ship By</span><input required type="date" value={draft.shipDate} onChange={(event) => update('shipDate', event.target.value)} /></label>}
            {can('partNumber') && <fieldset className="shipment-parts-editor span-2"><legend>Part Lines</legend>{draft.parts.map((part, index) => {
              const lineTotal = part.quantity !== '' && part.unitPrice !== ''
                ? Number(part.quantity) * Number(part.unitPrice)
                : null
              return <div className="shipment-part-row" key={index}>
                <label><span>Part Number</span><input required value={part.partNumber} onChange={(event) => updatePart(index, 'partNumber', event.target.value)} /></label>
                <label><span>Quantity</span><input required={creating} disabled={!can('quantity')} type="number" min="0" step="1" value={part.quantity} onChange={(event) => updatePart(index, 'quantity', event.target.value)} /></label>
                <label className="currency-field"><span>Unit Price</span><div><b>$</b><input disabled={!can('dollarValue')} type="number" min="0" step="0.01" value={part.unitPrice} onChange={(event) => updatePart(index, 'unitPrice', event.target.value)} /></div></label>
                <span className="part-line-total"><small>Line Total</small><strong>{lineTotal == null || !Number.isFinite(lineTotal) ? 'Not Set' : formatCurrency(lineTotal)}</strong></span>
                {draft.parts.length > 1 && <button className="icon-button part-remove" type="button" onClick={() => removePart(index)} aria-label={`Remove part line ${index + 1}`}><X size={15} /></button>}
              </div>
            })}<button className="button ghost add-part-line" type="button" onClick={addPart}><Plus size={14} /> Add Part Number</button></fieldset>}
            {can('sourceRequestedDate') && <label><span>Source Scheduled</span><input type="date" value={draft.sourceRequestedDate} onChange={(event) => update('sourceRequestedDate', event.target.value)} /></label>}
            {can('holdReason') && <label className="span-2"><span>Hold Reason</span><textarea rows={3} value={draft.holdReason} onChange={(event) => update('holdReason', event.target.value)} /></label>}
          </div>
          <footer><button className="button ghost" type="button" onClick={onClose}>Cancel</button><button className="button primary" disabled={saving} type="submit">{saving ? 'Saving...' : creating ? 'Add to queue' : 'Save changes'}</button></footer>
        </form>
      </section>
    </div>
  )
}

function AssignmentDialog({
  shipment,
  user,
  onClose,
  onSaved,
}: {
  shipment: Shipment
  user: QualityAssuranceUser
  onClose: () => void
  onSaved: (shipment: Shipment) => void
}) {
  const [options, setOptions] = useState<AssignmentOptions | null>(null)
  const [groupId, setGroupId] = useState(shipment.assignedGroupId?.toString() ?? '')
  const [userId, setUserId] = useState(shipment.assignedUserId?.toString() ?? '')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const canMoveGroup = user.permissions.includes(PERMISSIONS.assignmentGroup)
  const canAssignUser = user.permissions.includes(PERMISSIONS.assignmentUser)

  useEffect(() => {
    void qualityApi<AssignmentOptions>('/api/assignment-options').then(setOptions).catch((cause) => setError(cause instanceof Error ? cause.message : 'Assignments unavailable.'))
  }, [])

  const users = options?.users.filter((candidate) => candidate.groupIds.includes(Number(groupId))) ?? []
  const currentGroupUnavailable = Boolean(groupId && options && !options.groups.some((group) => group.id === Number(groupId)))
  const currentUserUnavailable = Boolean(userId && options && !users.some((candidate) => candidate.id === Number(userId)))
  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const saved = await qualityApi<Shipment>(`/api/shipments/${shipment.id}/assignment`, {
        method: 'POST',
        body: JSON.stringify({ version: shipment.version, groupId: groupId ? Number(groupId) : null, userId: userId ? Number(userId) : null }),
      })
      onSaved(saved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Assignment could not be saved.')
    } finally { setSaving(false) }
  }

  return (
    <div className="modal-backdrop" role="presentation">
      <section className="modal assignment-modal" role="dialog" aria-modal="true" aria-labelledby="assignment-title">
        <header><div><span className="eyebrow">Queue Routing</span><h2 id="assignment-title">Assign {shipment.salesOrderNumber ?? `Shipment ${shipment.id}`}</h2></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button></header>
        <form onSubmit={submit}>
          {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
          {!options ? <div className="loading-panel compact">Loading shared groups and users...</div> : <div className="assignment-fields">
            <label><span>Responsible Group</span><select disabled={!canMoveGroup} value={groupId} onChange={(event) => { setGroupId(event.target.value); setUserId('') }}><option value="">Unassigned - Manager Review</option>{currentGroupUnavailable && <option value={groupId} disabled>{shipment.assignedGroupName ?? 'Current Group'} (not enabled)</option>}{options.groups.map((group) => <option value={group.id} key={group.id}>{group.name} ({group.activeUserCount})</option>)}</select><small>Only groups enabled as a Quality Responsible Group in Arda Access appear here.</small></label>
            <label><span>Individual Owner</span><select disabled={!canAssignUser || !groupId || currentGroupUnavailable} value={userId} onChange={(event) => setUserId(event.target.value)}><option value="">Group Queue / Unassigned</option>{currentUserUnavailable && <option value={userId} disabled>{shipment.assignedDisplayName ?? 'Current Owner'} (not eligible)</option>}{users.map((candidate) => <option value={candidate.id} key={candidate.id}>{candidate.displayName}</option>)}</select><small>Only active users granted Receive Quality assignments through a permission group appear here.</small></label>
          </div>}
          <footer><button className="button ghost" type="button" onClick={onClose}>Cancel</button><button className="button primary" disabled={!options || saving || currentGroupUnavailable || currentUserUnavailable} type="submit">{saving ? 'Assigning...' : 'Save assignment'}</button></footer>
        </form>
      </section>
    </div>
  )
}

function DetailDrawer({
  shipment,
  user,
  fields,
  onClose,
  onEdit,
  onAssign,
  onOpenComments,
  onUpdated,
}: {
  shipment: Shipment
  user: QualityAssuranceUser
  fields: FieldAccess[]
  onClose: () => void
  onEdit: () => void
  onAssign: () => void
  onOpenComments: () => void
  onUpdated: (shipment: Shipment) => void
}) {
  const [audit, setAudit] = useState<AuditEntry[] | null>(null)
  const [auditError, setAuditError] = useState<string | null>(null)
  const [completingQa, setCompletingQa] = useState(false)
  const [workflowError, setWorkflowError] = useState<string | null>(null)
  const canEdit = fields.some((field) => field.canEdit)
  const canViewAssignment = user.permissions.includes(PERMISSIONS.assignmentView)
  const canAssign = canViewAssignment
    && (user.permissions.includes(PERMISSIONS.assignmentGroup) || user.permissions.includes(PERMISSIONS.assignmentUser))
  const canAudit = user.permissions.includes(PERMISSIONS.audit)
  const canCompleteQa = user.permissions.includes(PERMISSIONS.ship)
    && !shipment.isShipped
    && shipment.assignedGroupName?.trim().toLowerCase() === 'quality'
  const owner = actionOwner(shipment, canViewAssignment)

  async function loadAudit() {
    if (audit) { setAudit(null); return }
    setAuditError(null)
    try { setAudit(await qualityApi<AuditEntry[]>(`/api/shipments/${shipment.id}/audit`)) }
    catch (cause) { setAuditError(cause instanceof Error ? cause.message : 'Audit history unavailable.') }
  }

  async function markQaComplete() {
    setCompletingQa(true)
    setWorkflowError(null)
    try {
      const updated = await qualityApi<Shipment>(`/api/shipments/${shipment.id}/qa-complete`, {
        method: 'POST', body: JSON.stringify({ version: shipment.version }),
      })
      onUpdated(updated)
    } catch (cause) {
      setWorkflowError(cause instanceof Error ? cause.message : 'QA completion could not be saved.')
    } finally { setCompletingQa(false) }
  }

  const visible = (key: ShipmentFieldKey) => fields.some((field) => field.key === key && field.canView)
  return (
    <div className="drawer-layer" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <aside className="detail-drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title">
        <header className="drawer-head"><div><span className="eyebrow">Shipping Record</span><h2 id="drawer-title">{shipment.externalShipmentUrl && shipment.salesOrderNumber ? <a className="external-record-link" href={shipment.externalShipmentUrl} target="_blank" rel="noreferrer">{shipment.salesOrderNumber}<ExternalLink size={15} /></a> : shipment.salesOrderNumber ?? `Shipment ${shipment.id}`}</h2><p>{shipment.customer ?? 'Customer hidden'} · {shipment.partNumber ?? 'Part number hidden'}</p></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={19} /></button></header>
        <div className="drawer-actions">
          {canEdit && <button className="button ghost" type="button" onClick={onEdit}><Pencil size={14} /> Edit</button>}
          {canAssign && <button className="button ghost" type="button" onClick={onAssign}><UserRoundCheck size={14} /> Assign</button>}
          {canAudit && <button className="button ghost" type="button" onClick={() => void loadAudit()}><FileClock size={14} /> {audit ? 'Hide Audit' : 'Audit Trail'}</button>}
          {canCompleteQa && <button className="button success" disabled={completingQa} type="button" onClick={() => void markQaComplete()}><PackageCheck size={14} /> {completingQa ? 'Saving...' : 'QA Complete'}</button>}
        </div>
        <div className="drawer-scroll">
          <section className="shipment-hero">
            <span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Status hidden'}</span>
            <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span>
            <span className="age-badge"><Clock3 size={13} /> {ageInDays(shipment.createdAt)} days in queue</span>
          </section>
          {(canViewAssignment || visible('nextAction')) && <section className="detail-section"><h3>Action</h3><dl><div><dt>Current Owner</dt><dd>{owner.primary}</dd></div><div><dt>{owner.isUnassigned && shipment.nextAction ? 'Assignment Status' : 'Responsible Group'}</dt><dd>{owner.secondary}</dd></div></dl></section>}
          <section className="detail-section"><h3>Shipment Details</h3><dl>
            {visible('qaArrivalDate') && <div><dt>Shipment Arrival</dt><dd>{formatDate(shipment.qaArrivalDate)}</dd></div>}
            {visible('shipDate') && <div><dt>Ship By</dt><dd>{formatDate(shipment.shipDate)}</dd></div>}
            {visible('purchaseOrderNumber') && <div><dt>P.O.</dt><dd>{shipment.purchaseOrderNumber || 'Not set'}</dd></div>}
            {visible('taskType') && <div><dt>Task Type</dt><dd>{shipment.taskType || 'Not set'}</dd></div>}
            {visible('quantity') && <div><dt>Quantity</dt><dd>{shipment.quantity?.toLocaleString() ?? 'Not set'}</dd></div>}
            {visible('dollarValue') && <div><dt>Dollar Value</dt><dd>{formatCurrency(shipment.dollarValue)}</dd></div>}
            {visible('sourceRequestedDate') && <div><dt>Source Scheduled</dt><dd>{formatDate(shipment.sourceRequestedDate)}</dd></div>}
            {visible('lastWorkedAt') && <div><dt>Last Worked</dt><dd>{formatDateTime(shipment.lastWorkedAt)}</dd></div>}
          </dl></section>
          {visible('partNumber') && <section className="detail-section"><h3>Part Lines</h3><div className="shipment-part-list"><div className="shipment-part-list-head"><span>Part Number</span>{visible('quantity') && <span>Quantity</span>}{visible('dollarValue') && <><span>Unit Price</span><span>Total</span></>}</div>{shipment.parts.map((part) => <div className="shipment-part-list-row" key={part.id || `${part.displayOrder}-${part.partNumber}`}><strong>{part.partNumber}</strong>{visible('quantity') && <span>{part.quantity?.toLocaleString() ?? 'Not Set'}</span>}{visible('dollarValue') && <><span>{formatCurrency(part.unitPrice)}</span><span>{formatCurrency(part.totalValue)}</span></>}</div>)}</div></section>}
          {(shipment.externalSyncProvider || shipment.externalSyncError) && <section className="detail-section"><h3>Fulcrum Sync</h3><dl><div><dt>Provider</dt><dd>{shipment.externalSyncProvider ?? 'Not Set'}</dd></div><div><dt>Fulcrum Status</dt><dd>{shipment.externalShipmentStatus ?? 'No Match'}</dd></div><div><dt>Last Checked</dt><dd>{formatDateTime(shipment.externalSyncedAt)}</dd></div></dl>{shipment.externalSyncError && <p className="sync-warning"><AlertTriangle size={14} /> {shipment.externalSyncError}</p>}</section>}
          {visible('holdReason') && <section className="detail-section narrative"><h3>Hold Reason</h3><p>{shipment.holdReason || 'No hold reason recorded.'}</p></section>}
          {visible('comments') && <section className="detail-section narrative"><h3>Comments</h3><p>{shipment.comments || 'No comments yet.'}</p><button className="button ghost" type="button" onClick={onOpenComments}><MessageSquare size={14} /> Open Conversation</button></section>}
          {auditError && <p className="notice error"><AlertTriangle size={15} />{auditError}</p>}
          {workflowError && <p className="notice error"><AlertTriangle size={15} />{workflowError}</p>}
          {audit && <section className="detail-section audit-section"><h3>Audit Trail</h3>{audit.map((entry) => <article className="audit-entry" key={entry.id}><span className="audit-dot" /><div><strong>{entry.eventType.replace(/([A-Z])/g, ' $1').trim()}</strong><p>{entry.fieldName && <><b>{entry.fieldName}</b>: </>}{entry.oldValue && <del>{entry.oldValue}</del>}{entry.oldValue && entry.newValue && ' → '}{entry.newValue && <ins>{entry.newValue}</ins>}</p><small>{entry.displayName} · {formatDateTime(entry.occurredAt)}</small></div></article>)}</section>}
        </div>
      </aside>
    </div>
  )
}

export default function ShippingStatus({ user, reloadKey }: { user: QualityAssuranceUser; reloadKey: number }) {
  const canTeam = user.permissions.includes(PERMISSIONS.teamView) || user.permissions.includes(PERMISSIONS.viewAll)
  const canAll = user.permissions.includes(PERMISSIONS.viewAll)
  const [data, setData] = useState<ShipmentList | null>(null)
  const [status, setStatus] = useState<'open' | 'shipped' | 'all'>(() => {
    const value = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('status')
    return value === 'shipped' || value === 'all' ? value : 'open'
  })
  const [scope, setScope] = useState<ShippingScope>(() => {
    const value = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('scope')
    return normalizeShippingScope(value, canTeam, canAll)
  })
  const [sortKey, setSortKey] = useState<WorklistColumnKey>('shipDate')
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc'>('asc')
  const [search, setSearch] = useState('')
  const [shipmentStatusFilter, setShipmentStatusFilter] = useState('')
  const [customerFilters, setCustomerFilters] = useState<string[]>([])
  const [customerQuery, setCustomerQuery] = useState('')
  const [customerOptions, setCustomerOptions] = useState<string[]>([])
  const [customerOptionsLoading, setCustomerOptionsLoading] = useState(false)
  const [assigneeFilter, setAssigneeFilter] = useState('')
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [assignmentOptions, setAssignmentOptions] = useState<AssignmentOptions | null>(null)
  const [exporting, setExporting] = useState(false)
  const deferredSearch = useDeferredValue(search)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<Shipment | null>(null)
  const [commentsOpen, setCommentsOpen] = useState(false)
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState(false)
  const [assigning, setAssigning] = useState(false)
  const [assignmentOrigin, setAssignmentOrigin] = useState<'list' | 'detail'>('detail')
  const [importOpen, setImportOpen] = useState(false)
  const [refresh, setRefresh] = useState(0)
  const pendingDeepLink = useRef(readShipmentDeepLink(window.location.hash))

  useEffect(() => {
    const deepLink = pendingDeepLink.current
    if (!deepLink) return
    window.history.replaceState(
      null,
      '',
      `${window.location.pathname}${window.location.search}${deepLink.cleanedHash}`,
    )
  }, [])

  useEffect(() => {
    const requestedScope = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('scope')
    const requestedStatus = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('status')
    if (requestedScope !== null) setScope(normalizeShippingScope(requestedScope, canTeam, canAll))
    if (requestedStatus === 'open' || requestedStatus === 'shipped') setStatus(requestedStatus)
  }, [canAll, canTeam, reloadKey])

  function queryParameters() {
    const query = new URLSearchParams({
      status,
      scope,
      sort: SORT_PARAMETERS[sortKey],
      direction: sortDirection,
    })
    if (deferredSearch.trim()) query.set('search', deferredSearch.trim())
    if (shipmentStatusFilter) query.set('shipmentStatus', shipmentStatusFilter)
    customerFilters.forEach((customer) => query.append('customer', customer))
    if (assigneeFilter) query.set('assignee', assigneeFilter)
    return query
  }

  useEffect(() => {
    const openImport = () => setImportOpen(true)
    window.addEventListener('quality:open-shipping-import', openImport)
    return () => window.removeEventListener('quality:open-shipping-import', openImport)
  }, [])

  useEffect(() => {
    if (!user.permissions.includes(PERMISSIONS.assignmentView)) return
    void qualityApi<AssignmentOptions>('/api/assignment-options')
      .then(setAssignmentOptions)
      .catch(() => setAssignmentOptions(null))
  }, [user.permissions])

  useEffect(() => {
    let active = true
    setCustomerOptionsLoading(true)
    const query = new URLSearchParams({ status, scope })
    void qualityApi<string[]>(`/api/shipments/customer-options?${query}`)
      .then((options) => { if (active) setCustomerOptions(options) })
      .catch(() => { if (active) setCustomerOptions([]) })
      .finally(() => { if (active) setCustomerOptionsLoading(false) })
    return () => { active = false }
  }, [status, scope, reloadKey, refresh])

  useEffect(() => {
    let active = true
    setLoading(true)
    setError(null)
    const query = queryParameters()
    void qualityApi<ShipmentList>(`/api/shipments?${query}`)
      .then((next) => {
        if (!active) return
        startTransition(() => {
          setData(next)
          const deepLink = pendingDeepLink.current
          if (deepLink) {
            const requestedShipment = next.items.find((shipment) => shipment.id === deepLink.shipmentId) ?? null
            if (requestedShipment) {
              setSelected(requestedShipment)
              if (deepLink.openComments) setCommentsOpen(true)
              pendingDeepLink.current = null
            } else {
              void qualityApi<Shipment>(`/api/shipments/${deepLink.shipmentId}`)
                .then((shipment) => {
                  if (!active) return
                  setSelected(shipment)
                  if (deepLink.openComments) setCommentsOpen(true)
                  pendingDeepLink.current = null
                })
                .catch((cause) => {
                  if (active) setError(cause instanceof Error ? cause.message : 'The mentioned shipment is unavailable.')
                })
            }
          } else if (selected) setSelected(next.items.find((shipment) => shipment.id === selected.id) ?? null)
        })
      })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Shipping Status unavailable.') })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [status, scope, sortKey, sortDirection, deferredSearch, shipmentStatusFilter, customerFilters, assigneeFilter, reloadKey, refresh])

  const fields = data?.fields ?? []
  const canCreate = user.permissions.includes(PERMISSIONS.create)
  const canImport = user.permissions.includes(PERMISSIONS.import)
  const canReviewUnassigned = user.permissions.includes(PERMISSIONS.managerReview)
  const visibleFields = useMemo(() => new Set(
    fields.filter((field) => field.canView).map((field) => field.key),
  ), [fields])
  const canViewAssignment = user.permissions.includes(PERMISSIONS.assignmentView)
  const canAssign = canViewAssignment
    && (user.permissions.includes(PERMISSIONS.assignmentGroup) || user.permissions.includes(PERMISSIONS.assignmentUser))
  const worklistColumns = useMemo<WorklistColumn[]>(() => [
    visibleFields.has('status') && { key: 'status', label: 'Status', width: 145 },
    visibleFields.has('salesOrderNumber') && { key: 'salesOrderNumber', label: 'Shipper Number', width: 125 },
    visibleFields.has('qaArrivalDate') && { key: 'qaArrivalDate', label: 'Shipment Arrival', width: 105 },
    visibleFields.has('partNumber') && { key: 'partNumber', label: 'Part Number', width: 140 },
    visibleFields.has('purchaseOrderNumber') && { key: 'purchaseOrderNumber', label: 'P.O.', width: 105 },
    visibleFields.has('customer') && { key: 'customer', label: 'Customer', width: 165 },
    visibleFields.has('quantity') && { key: 'quantity', label: 'Quantity', width: 65 },
    visibleFields.has('dollarValue') && { key: 'dollarValue', label: 'Dollar Value', width: 105 },
    visibleFields.has('shipDate') && { key: 'shipDate', label: 'Ship By', width: 125 },
    visibleFields.has('holdReason') && { key: 'holdReason', label: 'Hold Reason', width: 155 },
    visibleFields.has('sourceRequestedDate') && { key: 'sourceRequestedDate', label: 'Source Scheduled', width: 105 },
    (visibleFields.has('nextAction') || canViewAssignment) && { key: 'action', label: 'Action', width: 180 },
    visibleFields.has('lastWorkedAt') && { key: 'lastWorkedAt', label: 'Last Worked', width: 105 },
    visibleFields.has('comments') && { key: 'comments', label: 'Comments', width: 185 },
    { key: 'queueAge', label: 'Queue Age', width: 65 },
  ].filter(Boolean) as WorklistColumn[], [canViewAssignment, visibleFields])
  const attentionCount = data?.items.filter((shipment) => shipment.dueState === 'Past due' || shipment.dueState === 'Due today').length ?? 0
  const unassignedCount = canViewAssignment
    ? data?.items.filter((shipment) => !shipment.assignedGroupId && !shipment.assignedUserId).length ?? 0
    : 0
  const activeFilterCount = customerFilters.length + [shipmentStatusFilter, assigneeFilter].filter(Boolean).length
  const hasResettableState = Boolean(search.trim() || activeFilterCount > 0 || sortKey !== 'shipDate' || sortDirection !== 'asc')

  function openShipment(shipment: Shipment) {
    setSelected(shipment)
  }

  function closeComments() {
    setCommentsOpen(false)
    const [path, rawQuery = ''] = window.location.hash.split('?')
    const query = new URLSearchParams(rawQuery)
    if (query.has('comments')) {
      query.delete('comments')
      const suffix = query.toString()
      window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${path}${suffix ? `?${suffix}` : ''}`)
    }
  }

  function openAssignment(event: React.MouseEvent, shipment: Shipment) {
    event.stopPropagation()
    setAssignmentOrigin('list')
    setSelected(shipment)
    setAssigning(true)
  }

  function closeAssignment() {
    setAssigning(false)
    if (assignmentOrigin === 'list') setSelected(null)
  }

  function toggleSort(key: WorklistColumnKey) {
    if (sortKey === key) {
      setSortDirection((current) => current === 'asc' ? 'desc' : 'asc')
      return
    }
    setSortKey(key)
    setSortDirection('asc')
  }

  function resetFiltersAndSort() {
    setSearch('')
    setShipmentStatusFilter('')
    setCustomerFilters([])
    setCustomerQuery('')
    setAssigneeFilter('')
    setSortKey('shipDate')
    setSortDirection('asc')
  }

  function addCustomerFilter(customer: string) {
    setCustomerFilters((current) => current.some((value) => value.localeCompare(customer, undefined, { sensitivity: 'accent' }) === 0)
      ? current
      : [...current, customer])
  }

  function removeCustomerFilter(customer: string) {
    setCustomerFilters((current) => current.filter((value) => value !== customer))
  }

  async function downloadFilteredResults() {
    setExporting(true)
    setError(null)
    try {
      const response = await fetch(`/api/shipments/export?${queryParameters()}`, { credentials: 'include' })
      if (!response.ok) {
        const payload = await response.json().catch(() => null) as { message?: string } | null
        throw new Error(payload?.message ?? 'The filtered Shipping Status results could not be exported.')
      }
      const blob = await response.blob()
      const disposition = response.headers.get('content-disposition') ?? ''
      const filename = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disposition)?.[1]
        ? decodeURIComponent(/filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disposition)![1])
        : 'quality-shipping-results.xlsx'
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = filename
      document.body.appendChild(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The filtered Shipping Status results could not be exported.')
    } finally {
      setExporting(false)
    }
  }

  function renderAction(shipment: Shipment, compact = false) {
    const owner = actionOwner(shipment, canViewAssignment)
    const content = <><span className="action-owner-name">{owner.primary}</span><small>{owner.secondary}</small></>
    return canAssign ? (
      <button className={`action-owner ${owner.isUnassigned ? 'is-unassigned' : ''} ${compact ? 'compact' : ''}`} type="button" onClick={(event) => openAssignment(event, shipment)} aria-label={`Assign ${shipment.salesOrderNumber ?? `shipment ${shipment.id}`}. Current owner: ${owner.primary}`}>
        <UserRoundCheck size={15} aria-hidden="true" />
        <span>{content}</span>
      </button>
    ) : <div className={`action-owner readonly ${owner.isUnassigned ? 'is-unassigned' : ''} ${compact ? 'compact' : ''}`}><UserRoundCheck size={15} aria-hidden="true" /><span>{content}</span></div>
  }

  function renderCell(key: WorklistColumnKey, shipment: Shipment) {
    switch (key) {
      case 'status': return <td key={key}><span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Hidden'}</span></td>
      case 'salesOrderNumber': return <td className="sales-order-cell" key={key}><strong>{shipment.externalShipmentUrl && shipment.salesOrderNumber ? <a className="external-record-link" href={shipment.externalShipmentUrl} target="_blank" rel="noreferrer" onClick={(event) => event.stopPropagation()}><Highlight value={shipment.salesOrderNumber} query={deferredSearch} /><ExternalLink size={11} /></a> : <Highlight value={shipment.salesOrderNumber ?? 'Hidden'} query={deferredSearch} />}</strong></td>
      case 'qaArrivalDate': return <td key={key}>{formatDate(shipment.qaArrivalDate)}</td>
      case 'partNumber': return <td key={key}><Highlight value={shipment.partNumber ?? ''} query={deferredSearch} /></td>
      case 'purchaseOrderNumber': return <td key={key}><Highlight value={shipment.purchaseOrderNumber ?? ''} query={deferredSearch} /></td>
      case 'customer': return <td className="customer-cell" key={key}><Highlight value={shipment.customer ?? ''} query={deferredSearch} /></td>
      case 'quantity': return <td className="numeric" key={key}>{shipment.quantity?.toLocaleString() ?? '—'}</td>
      case 'dollarValue': return <td className="numeric" key={key}>{formatCurrency(shipment.dollarValue)}</td>
      case 'shipDate': return <td className="ship-by-cell" key={key}><strong>{formatDate(shipment.shipDate)}</strong><span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span></td>
      case 'holdReason': return <td className="long-cell" key={key}><Highlight value={shipment.holdReason ?? ''} query={deferredSearch} /></td>
      case 'sourceRequestedDate': return <td key={key}>{formatDate(shipment.sourceRequestedDate)}</td>
      case 'action': return <td className="action-cell" key={key}>{renderAction(shipment)}</td>
      case 'lastWorkedAt': return <td key={key}>{formatDate(shipment.lastWorkedAt)}</td>
      case 'comments': return <td className="long-cell" key={key}><Highlight value={shipment.comments ?? ''} query={deferredSearch} /></td>
      case 'queueAge': return <td key={key}><span className="queue-days">{ageInDays(shipment.createdAt)}d</span></td>
    }
  }

  function accepted(saved: Shipment) {
    setSelected(saved)
    setCreating(false)
    setEditing(false)
    setAssigning(false)
    setRefresh((value) => value + 1)
  }

  function assignmentAccepted(saved: Shipment) {
    setSelected(assignmentOrigin === 'detail' ? saved : null)
    setAssigning(false)
    setRefresh((value) => value + 1)
  }

  return (
    <div className="view shipping-view">
      <section className="shipping-toolbar panel">
        <div className="toolbar-top">
          <div className="queue-view-summary">
            <span className="eyebrow">Live Worklist</span>
            <div className="queue-title-line"><h2>{status === 'open' ? 'Open Shipment Queue' : status === 'shipped' ? 'Past Shipments' : 'All Shipments'}</h2>{data && <span className="result-count">{data.total}</span>}</div>
            <p>{data ? status === 'open'
              ? `${attentionCount} need date attention${canViewAssignment ? ` · ${unassignedCount} need an owner` : ''}`
              : 'Searchable shipment history with full record details.'
              : 'Loading the shipment worklist...'}</p>
          </div>
          <div className="toolbar-actions-inline"><button className="button ghost" type="button" disabled={exporting} onClick={() => void downloadFilteredResults()}><Download size={15} /> {exporting ? 'Exporting...' : 'Export Results'}</button>{canCreate && <button className="button primary" type="button" onClick={() => setCreating(true)}><Plus size={15} /> Add Shipment</button>}</div>
        </div>
        <div className="filter-row">
          <label className="search-box"><Search size={16} /><span className="sr-only">Search Shipments</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search shipper, part, customer, action..." />{search && <button type="button" onClick={() => setSearch('')} aria-label="Clear search"><X size={14} /></button>}</label>
          <div className="segmented" aria-label="Shipment status"><button className={status === 'open' ? 'active' : ''} type="button" onClick={() => setStatus('open')}>Open</button><button className={status === 'shipped' ? 'active' : ''} type="button" onClick={() => setStatus('shipped')}>Past</button>{canAll && <button className={status === 'all' ? 'active' : ''} type="button" onClick={() => setStatus('all')}>All</button>}</div>
          <label className="compact-select"><SlidersHorizontal size={15} /><span className="compact-select-label">Queue</span><select value={scope} onChange={(event) => setScope(event.target.value as typeof scope)} aria-label="Queue scope"><option value="mine">{canReviewUnassigned ? 'Mine + Unassigned' : 'My Queue'}</option>{canTeam && <option value="team">My Groups</option>}{canAll && <option value="all">All Shipments</option>}</select></label>
          <button className={`control-button ${filtersOpen || activeFilterCount ? 'active' : ''}`} type="button" onClick={() => setFiltersOpen((current) => !current)} aria-expanded={filtersOpen} aria-controls="shipping-filter-panel"><ListFilter size={15} /><span>Filters</span>{activeFilterCount > 0 && <b>{activeFilterCount}</b>}</button>
          {hasResettableState && <button className="control-button reset-control" type="button" onClick={resetFiltersAndSort}><RotateCcw size={14} /><span>Reset Filters</span></button>}
        </div>
        {filtersOpen && <section className="shipping-filter-panel" id="shipping-filter-panel" aria-label="Shipment filters">
          <div className="filter-panel-heading"><div><span className="eyebrow">Narrow The Worklist</span><strong>Filter Results</strong><p>Combine filters below. Customer selections match any chosen customer.</p></div></div>
          {visibleFields.has('customer') && <CustomerFilterCombobox options={customerOptions} selected={customerFilters} query={customerQuery} loading={customerOptionsLoading} onQueryChange={setCustomerQuery} onAdd={addCustomerFilter} onRemove={removeCustomerFilter} />}
          {visibleFields.has('status') && <label><span>Status</span><select value={shipmentStatusFilter} onChange={(event) => setShipmentStatusFilter(event.target.value)}><option value="">All Statuses</option>{STATUS_OPTIONS.map((option) => <option value={option} key={option}>{option}</option>)}</select></label>}
          {canViewAssignment && <label><span>Assigned To</span><select value={assigneeFilter} onChange={(event) => setAssigneeFilter(event.target.value)}><option value="">Anyone</option><option value="unassigned">Unassigned</option>{assignmentOptions?.groups.map((group) => <option value={`group:${group.id}`} key={`group-${group.id}`}>{group.name} Queue</option>)}{assignmentOptions?.users.map((candidate) => <option value={`user:${candidate.id}`} key={`user-${candidate.id}`}>{candidate.displayName}</option>)}</select></label>}
        </section>}
        {(deferredSearch.trim() || activeFilterCount > 0) && <p className="filter-summary" role="status"><Search size={13} /> Showing {data?.total ?? 0} filtered result{data?.total === 1 ? '' : 's'}{deferredSearch.trim() && <> for <mark>{deferredSearch.trim()}</mark></>}</p>}
      </section>

      {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
      <section className="shipping-register panel" aria-busy={loading}>
        {loading && !data ? <div className="loading-panel" role="status">Loading Shipping Status...</div> : data?.items.length ? (<>
          <div className="shipping-table-wrap">
            <table className="shipping-table" aria-label={`${status === 'open' ? 'Open' : status === 'shipped' ? 'Past' : 'All'} shipments`}>
              <colgroup><col style={{ width: 44 }} />{worklistColumns.map((column) => <col style={{ width: column.width }} key={column.key} />)}<col style={{ width: 44 }} /></colgroup>
              <thead><tr><th className="sticky-col row-number">#</th>{worklistColumns.map((column) => <th aria-sort={sortKey === column.key ? sortDirection === 'asc' ? 'ascending' : 'descending' : 'none'} key={column.key}><button className="column-sort-button" type="button" onClick={() => toggleSort(column.key)}><span>{column.label}</span>{sortKey === column.key ? sortDirection === 'asc' ? <ArrowUp size={12} /> : <ArrowDown size={12} /> : <ArrowDownUp size={11} />}</button></th>)}<th aria-label="Details" /></tr></thead>
              <tbody>{data.items.map((shipment, index) => (
                <tr className={`${shipment.dueState === 'Past due' ? 'past-due-row' : ''} ${shipment.isShipped ? 'shipped-row' : ''}`} key={shipment.id} onClick={() => openShipment(shipment)}>
                  <td className="sticky-col row-number">{index + 1}</td>
                  {worklistColumns.map((column) => renderCell(column.key, shipment))}
                  <td><button className="row-open" type="button" onClick={(event) => { event.stopPropagation(); openShipment(shipment) }} aria-label={`Open details for ${shipment.salesOrderNumber ?? `shipment ${shipment.id}`}`}><ChevronRight size={16} /></button></td>
                </tr>
              ))}</tbody>
            </table>
          </div>
          <div className="shipping-card-list" aria-label={`${status === 'open' ? 'Open' : status === 'shipped' ? 'Past' : 'All'} shipments`}>
            {data.items.map((shipment) => (
              <article className={`shipping-card ${shipment.dueState === 'Past due' ? 'past-due' : ''}`} key={shipment.id}>
                <header><span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Status hidden'}</span><span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span></header>
                <div className="shipping-card-identity"><strong><Highlight value={shipment.salesOrderNumber ?? `Shipment ${shipment.id}`} query={deferredSearch} /></strong><span><Highlight value={shipment.customer ?? 'Customer hidden'} query={deferredSearch} /> · <Highlight value={shipment.partNumber ?? 'Part hidden'} query={deferredSearch} /></span>{shipment.purchaseOrderNumber && <small>P.O. <Highlight value={shipment.purchaseOrderNumber} query={deferredSearch} /></small>}</div>
                <dl><div><dt>Ship By</dt><dd>{formatDate(shipment.shipDate)}</dd></div><div><dt>Queue Age</dt><dd>{ageInDays(shipment.createdAt)} days</dd></div></dl>
                {(visibleFields.has('nextAction') || canViewAssignment) && <div className="shipping-card-action"><span>Action</span>{renderAction(shipment, true)}</div>}
                <button className="shipping-card-details" type="button" onClick={() => openShipment(shipment)}>View Shipment Details <ChevronRight size={15} /></button>
              </article>
            ))}
          </div>
        </>
        ) : <div className="empty-state"><ClipboardList size={28} /><h3>{status === 'open' ? 'No Open Shipments In This Queue' : 'No Past Shipments In This Queue'}</h3><p>Adjust the scope or search, or add a shipment if you have permission.</p></div>}
      </section>

      {selected && !editing && !assigning && !commentsOpen && <DetailDrawer shipment={selected} user={user} fields={fields} onClose={() => setSelected(null)} onEdit={() => setEditing(true)} onAssign={() => { setAssignmentOrigin('detail'); setAssigning(true) }} onOpenComments={() => setCommentsOpen(true)} onUpdated={accepted} />}
      {selected && commentsOpen && <ShipmentCommentsDrawer shipment={selected} currentUser={user} canPost={fields.some((field) => field.key === 'comments' && field.canEdit)} onClose={closeComments} onMessageSent={() => setRefresh((value) => value + 1)} />}
      {creating && <ShipmentForm fields={fields} onClose={() => setCreating(false)} onSaved={accepted} />}
      {editing && selected && <ShipmentForm shipment={selected} fields={fields} onClose={() => setEditing(false)} onSaved={accepted} />}
      {assigning && selected && <AssignmentDialog shipment={selected} user={user} onClose={closeAssignment} onSaved={assignmentAccepted} />}
      {importOpen && canImport && <ShippingImportDialog onClose={() => setImportOpen(false)} onImported={() => setRefresh((value) => value + 1)} />}
    </div>
  )
}
