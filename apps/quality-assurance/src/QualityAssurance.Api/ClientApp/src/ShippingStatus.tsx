import { startTransition, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertTriangle,
  ArrowRightLeft,
  ChevronRight,
  ClipboardList,
  Clock3,
  FileSpreadsheet,
  FileClock,
  FileUp,
  PackageCheck,
  Pencil,
  Plus,
  Search,
  SlidersHorizontal,
  UserRoundCheck,
  X,
} from 'lucide-react'
import { qualityApi } from './api'
import { ageInDays, formatCurrency, formatDate, formatDateTime } from './format'
import { SHIPPING_COLUMN_METADATA } from './ShippingLayoutEditor'
import type {
  AssignmentOptions,
  AuditEntry,
  FieldAccess,
  QualityAssuranceUser,
  Shipment,
  ShipmentFieldKey,
  ShipmentList,
  ShippingLayout,
  ShippingLayoutColumn,
  ShippingLayoutColumnKey,
} from './types'

const PERMISSIONS = {
  create: 'quality-assurance.shipments.create',
  import: 'quality-assurance.shipments.import',
  assignmentView: 'quality-assurance.assignments.view',
  assignmentGroup: 'quality-assurance.assignments.group',
  assignmentUser: 'quality-assurance.assignments.user',
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
  partNumber: string
  purchaseOrderNumber: string
  customer: string
  taskType: string
  quantity: string
  dollarValue: string
  shipDate: string
  holdReason: string
  sourceRequestedDate: string
  nextAction: string
  comments: string
}

interface ShippingImportResult {
  rowsRead: number
  createdRecords: number
  skippedDuplicates: number
  reconciledAssignments: number
  worksheet: string
}

const FIELD_KEYS: (keyof ShipmentDraft)[] = [
  'status', 'salesOrderNumber', 'qaArrivalDate', 'partNumber', 'purchaseOrderNumber',
  'customer', 'taskType', 'quantity', 'dollarValue', 'shipDate', 'holdReason',
  'sourceRequestedDate', 'nextAction', 'comments',
]

function draftFor(shipment?: Shipment | null): ShipmentDraft {
  return {
    status: shipment?.status ?? 'WIP',
    salesOrderNumber: shipment?.salesOrderNumber ?? '',
    qaArrivalDate: shipment?.qaArrivalDate ?? '',
    partNumber: shipment?.partNumber ?? '',
    purchaseOrderNumber: shipment?.purchaseOrderNumber ?? '',
    customer: shipment?.customer ?? '',
    taskType: shipment?.taskType ?? 'General',
    quantity: shipment?.quantity?.toString() ?? '',
    dollarValue: shipment?.dollarValue?.toString() ?? '',
    shipDate: shipment?.shipDate ?? '',
    holdReason: shipment?.holdReason ?? '',
    sourceRequestedDate: shipment?.sourceRequestedDate ?? '',
    nextAction: shipment?.nextAction ?? '',
    comments: shipment?.comments ?? '',
  }
}

function fieldValue(draft: ShipmentDraft, key: keyof ShipmentDraft) {
  const value = draft[key]
  if (key === 'quantity' || key === 'dollarValue') return value === '' ? null : Number(value)
  return value === '' ? null : value
}

function Highlight({ value, query }: { value: string; query: string }) {
  const search = query.trim()
  if (!search) return value
  const index = value.toLowerCase().indexOf(search.toLowerCase())
  if (index < 0) return value
  return <>{value.slice(0, index)}<mark>{value.slice(index, index + search.length)}</mark>{value.slice(index + search.length)}</>
}

function moveLayoutColumn(
  columns: ShippingLayoutColumn[],
  sourceKey: ShippingLayoutColumnKey,
  targetKey: ShippingLayoutColumnKey,
) {
  const source = columns.findIndex((column) => column.key === sourceKey)
  const target = columns.findIndex((column) => column.key === targetKey)
  if (source < 0 || target < 0 || source === target) return columns
  const next = [...columns]
  const [moved] = next.splice(source, 1)
  next.splice(target, 0, moved)
  return next
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
          <div><span className="eyebrow">Controlled bulk entry</span><h2 id="shipping-import-title">Import Shipping Status</h2><p>Only the <b>Complete List</b> worksheet is read. Existing exact records are skipped.</p></div>
          <button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button>
        </header>
        <div className="shipping-import-body">
          {!result && <label className="shipping-file-picker">
            <FileSpreadsheet size={24} aria-hidden="true" />
            <span><strong>{file?.name ?? 'Choose an Excel workbook'}</strong><small>Accepted format: .xlsx, up to 25 MB</small></span>
            <input type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" onChange={(event) => { setFile(event.currentTarget.files?.[0] ?? null); setError(null) }} />
          </label>}
          {result && <div className="shipping-import-result">
            <FileSpreadsheet size={28} aria-hidden="true" />
            <div><strong>Import complete</strong><p>{result.createdRecords} records created, {result.skippedDuplicates} exact duplicates skipped, and {result.reconciledAssignments} legacy assignments corrected from {result.rowsRead} rows in <b>{result.worksheet}</b>.</p></div>
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

  function update(key: keyof ShipmentDraft, value: string) {
    setDraft((current) => ({ ...current, [key]: value }))
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      let saved: Shipment
      if (creating) {
        const body = Object.fromEntries(FIELD_KEYS
          .filter((key) => editable.get(key as ShipmentFieldKey))
          .map((key) => [key, fieldValue(draft, key)]))
        saved = await qualityApi<Shipment>('/api/shipments', { method: 'POST', body: JSON.stringify(body) })
      } else {
        const original = draftFor(shipment)
        const changes = Object.fromEntries(FIELD_KEYS
          .filter((key) => editable.get(key as ShipmentFieldKey))
          .filter((key) => fieldValue(draft, key) !== fieldValue(original, key))
          .map((key) => [key, fieldValue(draft, key)]))
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
        <header><div><span className="eyebrow">{creating ? 'New queue item' : `Shipment ${shipment.salesOrderNumber ?? shipment.id}`}</span><h2 id="shipment-form-title">{creating ? 'Add Shipping Status record' : 'Edit shipment details'}</h2></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button></header>
        <form onSubmit={submit}>
          {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
          <div className="form-grid">
            {can('status') && <label><span>Status</span><select value={draft.status} onChange={(event) => update('status', event.target.value)}>{STATUS_OPTIONS.map((status) => <option key={status}>{status}</option>)}</select></label>}
            {can('taskType') && <label><span>Task type</span><input list="qa-task-types" value={draft.taskType} onChange={(event) => update('taskType', event.target.value)} /><datalist id="qa-task-types">{TASK_TYPES.map((type) => <option key={type}>{type}</option>)}</datalist></label>}
            {can('salesOrderNumber') && <label><span>Sales order #</span><input required value={draft.salesOrderNumber} onChange={(event) => update('salesOrderNumber', event.target.value)} /></label>}
            {can('partNumber') && <label><span>Part number</span><input required value={draft.partNumber} onChange={(event) => update('partNumber', event.target.value)} /></label>}
            {can('purchaseOrderNumber') && <label><span>P.O.</span><input value={draft.purchaseOrderNumber} onChange={(event) => update('purchaseOrderNumber', event.target.value)} /></label>}
            {can('customer') && <label><span>Customer</span><input required value={draft.customer} onChange={(event) => update('customer', event.target.value)} /></label>}
            {can('qaArrivalDate') && <label><span>QA arrival date</span><input type="date" value={draft.qaArrivalDate} onChange={(event) => update('qaArrivalDate', event.target.value)} /></label>}
            {can('shipDate') && <label><span>Required ship date</span><input type="date" value={draft.shipDate} onChange={(event) => update('shipDate', event.target.value)} /></label>}
            {can('quantity') && <label><span>Quantity</span><input type="number" min="0" step="0.001" value={draft.quantity} onChange={(event) => update('quantity', event.target.value)} /></label>}
            {can('dollarValue') && <label><span>Dollar value</span><input type="number" min="0" step="0.01" value={draft.dollarValue} onChange={(event) => update('dollarValue', event.target.value)} /></label>}
            {can('sourceRequestedDate') && <label><span>Source requested</span><input type="date" value={draft.sourceRequestedDate} onChange={(event) => update('sourceRequestedDate', event.target.value)} /></label>}
            {can('holdReason') && <label className="span-2"><span>Hold reason</span><textarea rows={3} value={draft.holdReason} onChange={(event) => update('holdReason', event.target.value)} /></label>}
            {can('nextAction') && <label className="span-2"><span>Action</span><textarea rows={3} value={draft.nextAction} onChange={(event) => update('nextAction', event.target.value)} /></label>}
            {can('comments') && <label className="span-2"><span>Comments</span><textarea rows={4} value={draft.comments} onChange={(event) => update('comments', event.target.value)} /></label>}
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
        <header><div><span className="eyebrow">Queue routing</span><h2 id="assignment-title">Assign {shipment.salesOrderNumber ?? `shipment ${shipment.id}`}</h2></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={18} /></button></header>
        <form onSubmit={submit}>
          {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
          {!options ? <div className="loading-panel compact">Loading shared groups and users...</div> : <div className="assignment-fields">
            <label><span>Responsible group</span><select disabled={!canMoveGroup} value={groupId} onChange={(event) => { setGroupId(event.target.value); setUserId('') }}><option value="">Unassigned - manager review</option>{options.groups.map((group) => <option value={group.id} key={group.id}>{group.name} ({group.activeUserCount})</option>)}</select><small>Moving work between departments requires Assign Groups permission.</small></label>
            <label><span>Individual owner</span><select disabled={!canAssignUser || !groupId} value={userId} onChange={(event) => setUserId(event.target.value)}><option value="">Group queue / unassigned</option>{users.map((candidate) => <option value={candidate.id} key={candidate.id}>{candidate.displayName}</option>)}</select><small>Group leads can assign active members of their permitted group.</small></label>
          </div>}
          <footer><button className="button ghost" type="button" onClick={onClose}>Cancel</button><button className="button primary" disabled={!options || saving} type="submit">{saving ? 'Assigning...' : 'Save assignment'}</button></footer>
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
  onUpdated,
}: {
  shipment: Shipment
  user: QualityAssuranceUser
  fields: FieldAccess[]
  onClose: () => void
  onEdit: () => void
  onAssign: () => void
  onUpdated: (shipment: Shipment) => void
}) {
  const [audit, setAudit] = useState<AuditEntry[] | null>(null)
  const [auditError, setAuditError] = useState<string | null>(null)
  const [confirmShip, setConfirmShip] = useState(false)
  const [shipping, setShipping] = useState(false)
  const canEdit = fields.some((field) => field.canEdit)
  const canAssign = user.permissions.includes(PERMISSIONS.assignmentView)
    && (user.permissions.includes(PERMISSIONS.assignmentGroup) || user.permissions.includes(PERMISSIONS.assignmentUser))
  const canAudit = user.permissions.includes(PERMISSIONS.audit)
  const canShip = user.permissions.includes(PERMISSIONS.ship) && !shipment.isShipped

  async function loadAudit() {
    if (audit) { setAudit(null); return }
    setAuditError(null)
    try { setAudit(await qualityApi<AuditEntry[]>(`/api/shipments/${shipment.id}/audit`)) }
    catch (cause) { setAuditError(cause instanceof Error ? cause.message : 'Audit history unavailable.') }
  }

  async function markShipped() {
    setShipping(true)
    try {
      const updated = await qualityApi<Shipment>(`/api/shipments/${shipment.id}/shipped`, {
        method: 'POST', body: JSON.stringify({ version: shipment.version }),
      })
      onUpdated(updated)
    } finally { setShipping(false); setConfirmShip(false) }
  }

  const visible = (key: ShipmentFieldKey) => fields.some((field) => field.key === key && field.canView)
  return (
    <div className="drawer-layer" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <aside className="detail-drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title">
        <header className="drawer-head"><div><span className="eyebrow">Shipping record</span><h2 id="drawer-title">{shipment.salesOrderNumber ?? `Shipment ${shipment.id}`}</h2><p>{shipment.customer ?? 'Customer hidden'} · {shipment.partNumber ?? 'Part number hidden'}</p></div><button className="icon-button" type="button" onClick={onClose} aria-label="Close"><X size={19} /></button></header>
        <div className="drawer-actions">
          {canEdit && <button className="button ghost" type="button" onClick={onEdit}><Pencil size={14} /> Edit</button>}
          {canAssign && <button className="button ghost" type="button" onClick={onAssign}><UserRoundCheck size={14} /> Assign</button>}
          {canAudit && <button className="button ghost" type="button" onClick={() => void loadAudit()}><FileClock size={14} /> {audit ? 'Hide audit' : 'Audit trail'}</button>}
          {canShip && <button className="button success" type="button" onClick={() => setConfirmShip(true)}><PackageCheck size={14} /> Mark shipped</button>}
        </div>
        <div className="drawer-scroll">
          <section className="shipment-hero">
            <span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Status hidden'}</span>
            <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span>
            <span className="age-badge"><Clock3 size={13} /> {ageInDays(shipment.createdAt)} days in queue</span>
          </section>
          {user.permissions.includes(PERMISSIONS.assignmentView) && <section className="detail-section"><h3>Ownership</h3><dl><div><dt>Group</dt><dd>{shipment.assignedGroupName ?? 'Unassigned'}</dd></div><div><dt>Owner</dt><dd>{shipment.assignedDisplayName ?? (shipment.assignedGroupName ? 'Group queue' : 'Unassigned')}</dd></div></dl></section>}
          <section className="detail-section"><h3>Shipment details</h3><dl>
            {visible('qaArrivalDate') && <div><dt>QA arrival</dt><dd>{formatDate(shipment.qaArrivalDate)}</dd></div>}
            {visible('shipDate') && <div><dt>Ship date</dt><dd>{formatDate(shipment.shipDate)}</dd></div>}
            {visible('purchaseOrderNumber') && <div><dt>P.O.</dt><dd>{shipment.purchaseOrderNumber || 'Not set'}</dd></div>}
            {visible('taskType') && <div><dt>Task type</dt><dd>{shipment.taskType || 'Not set'}</dd></div>}
            {visible('quantity') && <div><dt>Quantity</dt><dd>{shipment.quantity?.toLocaleString() ?? 'Not set'}</dd></div>}
            {visible('dollarValue') && <div><dt>Dollar value</dt><dd>{formatCurrency(shipment.dollarValue)}</dd></div>}
            {visible('sourceRequestedDate') && <div><dt>Source requested</dt><dd>{formatDate(shipment.sourceRequestedDate)}</dd></div>}
            {visible('lastWorkedAt') && <div><dt>Last worked</dt><dd>{formatDateTime(shipment.lastWorkedAt)}</dd></div>}
          </dl></section>
          {visible('holdReason') && <section className="detail-section narrative"><h3>Hold reason</h3><p>{shipment.holdReason || 'No hold reason recorded.'}</p></section>}
          {visible('nextAction') && <section className="detail-section narrative"><h3>Current action</h3><p>{shipment.nextAction || 'No action recorded.'}</p></section>}
          {visible('comments') && <section className="detail-section narrative"><h3>Comments</h3><p>{shipment.comments || 'No comments recorded.'}</p></section>}
          {auditError && <p className="notice error"><AlertTriangle size={15} />{auditError}</p>}
          {audit && <section className="detail-section audit-section"><h3>Audit trail</h3>{audit.map((entry) => <article className="audit-entry" key={entry.id}><span className="audit-dot" /><div><strong>{entry.eventType.replace(/([A-Z])/g, ' $1').trim()}</strong><p>{entry.fieldName && <><b>{entry.fieldName}</b>: </>}{entry.oldValue && <del>{entry.oldValue}</del>}{entry.oldValue && entry.newValue && ' → '}{entry.newValue && <ins>{entry.newValue}</ins>}</p><small>{entry.displayName} · {formatDateTime(entry.occurredAt)}</small></div></article>)}</section>}
        </div>
        {confirmShip && <div className="drawer-confirm"><PackageCheck size={22} /><div><strong>Move to Past Shipments?</strong><p>This shipment will leave the default Open view. The complete audit history remains available.</p></div><button className="button ghost" type="button" onClick={() => setConfirmShip(false)}>Cancel</button><button className="button success" disabled={shipping} type="button" onClick={() => void markShipped()}>{shipping ? 'Saving...' : 'Confirm shipped'}</button></div>}
      </aside>
    </div>
  )
}

export default function ShippingStatus({ user, reloadKey }: { user: QualityAssuranceUser; reloadKey: number }) {
  const [data, setData] = useState<ShipmentList | null>(null)
  const [status, setStatus] = useState<'open' | 'shipped' | 'all'>('open')
  const [scope, setScope] = useState<'mine' | 'team' | 'all'>('mine')
  const [sort, setSort] = useState<'oldest' | 'ship-date'>('oldest')
  const [search, setSearch] = useState('')
  const deferredSearch = useDeferredValue(search)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<Shipment | null>(null)
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState(false)
  const [assigning, setAssigning] = useState(false)
  const [importOpen, setImportOpen] = useState(false)
  const [layout, setLayout] = useState<ShippingLayout | null>(null)
  const layoutRef = useRef<ShippingLayout | null>(null)
  const layoutVersionRef = useRef(0)
  const layoutSaveQueue = useRef(Promise.resolve())
  const [draggingColumn, setDraggingColumn] = useState<ShippingLayoutColumnKey | null>(null)
  const [refresh, setRefresh] = useState(0)

  useEffect(() => {
    let active = true
    void qualityApi<ShippingLayout>('/api/shipping-layout')
      .then((next) => {
        if (!active) return
        layoutRef.current = next
        layoutVersionRef.current = next.version
        setLayout(next)
      })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Your saved layout is unavailable.') })
    return () => { active = false }
  }, [user.accountName])

  useEffect(() => {
    let active = true
    setLoading(true)
    setError(null)
    const query = new URLSearchParams({ status, scope, sort })
    if (deferredSearch.trim()) query.set('search', deferredSearch.trim())
    void qualityApi<ShipmentList>(`/api/shipments?${query}`)
      .then((next) => {
        if (!active) return
        startTransition(() => {
          setData(next)
          const requested = new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('shipment')
          if (requested && !selected) setSelected(next.items.find((shipment) => shipment.id === Number(requested)) ?? null)
          if (selected) setSelected(next.items.find((shipment) => shipment.id === selected.id) ?? null)
        })
      })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Shipping Status unavailable.') })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [status, scope, sort, deferredSearch, reloadKey, refresh])

  const fields = data?.fields ?? []
  const canTeam = user.permissions.includes(PERMISSIONS.teamView) || user.permissions.includes(PERMISSIONS.viewAll)
  const canAll = user.permissions.includes(PERMISSIONS.viewAll)
  const canCreate = user.permissions.includes(PERMISSIONS.create)
  const canImport = user.permissions.includes(PERMISSIONS.import)
  const canReviewUnassigned = user.permissions.includes(PERMISSIONS.assignmentGroup)
  const availableColumns = useMemo(() => {
    const available = new Set<ShippingLayoutColumnKey>(
      fields.filter((field) => field.canView).map((field) => field.key),
    )
    if (user.permissions.includes(PERMISSIONS.assignmentView)) available.add('assignment')
    available.add('queueAge')
    return available
  }, [fields, user.permissions])
  const visibleColumns = layout?.columns.filter((column) =>
    column.isVisible && availableColumns.has(column.key)) ?? []
  const shippingTableWidth = 88 + visibleColumns.reduce((total, column) => total + column.width, 0)

  function saveLayoutColumns(columns: ShippingLayoutColumn[]) {
    const current = layoutRef.current
    if (!current) return
    const optimistic = { ...current, columns }
    layoutRef.current = optimistic
    setLayout(optimistic)
    layoutSaveQueue.current = layoutSaveQueue.current.then(async () => {
      try {
        const saved = await qualityApi<ShippingLayout>('/api/shipping-layout', {
          method: 'PUT',
          body: JSON.stringify({ columns, version: layoutVersionRef.current }),
        })
        layoutVersionRef.current = saved.version
        const latest = layoutRef.current
        if (latest) {
          layoutRef.current = { ...latest, version: saved.version, updatedAt: saved.updatedAt }
          setLayout(layoutRef.current)
        }
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'The table layout could not be saved.')
        try {
          const recovered = await qualityApi<ShippingLayout>('/api/shipping-layout')
          layoutRef.current = recovered
          layoutVersionRef.current = recovered.version
          setLayout(recovered)
        } catch { /* Keep the original layout error visible. */ }
      }
    })
  }

  function beginColumnResize(event: React.PointerEvent<HTMLSpanElement>, column: ShippingLayoutColumn) {
    event.preventDefault()
    event.stopPropagation()
    const base = layoutRef.current
    if (!base) return
    const startX = event.clientX
    const startWidth = column.width
    const metadata = SHIPPING_COLUMN_METADATA[column.key]
    const resized = (clientX: number) => Math.max(
      metadata.minimumWidth,
      Math.min(metadata.maximumWidth, Math.round(startWidth + clientX - startX)),
    )
    const columnsAt = (clientX: number) => base.columns.map((candidate) =>
      candidate.key === column.key ? { ...candidate, width: resized(clientX) } : candidate)
    const move = (pointerEvent: PointerEvent) => {
      const columns = columnsAt(pointerEvent.clientX)
      setLayout((current) => current ? { ...current, columns } : current)
    }
    const finish = (pointerEvent: PointerEvent) => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', finish)
      window.removeEventListener('pointercancel', finish)
      saveLayoutColumns(columnsAt(pointerEvent.clientX))
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', finish)
    window.addEventListener('pointercancel', finish)
  }

  function dropColumn(sourceKey: ShippingLayoutColumnKey, targetKey: ShippingLayoutColumnKey) {
    const current = layoutRef.current
    if (!current) return
    const columns = moveLayoutColumn(current.columns, sourceKey, targetKey)
    setDraggingColumn(null)
    if (columns !== current.columns) saveLayoutColumns(columns)
  }

  function beginColumnDrag(event: React.PointerEvent<HTMLTableCellElement>, sourceKey: ShippingLayoutColumnKey) {
    if (event.button !== 0) return
    event.preventDefault()
    const startX = event.clientX
    let moved = false
    const cleanup = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', finish)
      window.removeEventListener('pointercancel', cancel)
      setDraggingColumn(null)
    }
    const move = (pointerEvent: PointerEvent) => {
      if (Math.abs(pointerEvent.clientX - startX) < 4) return
      pointerEvent.preventDefault()
      moved = true
      setDraggingColumn(sourceKey)
    }
    const finish = (pointerEvent: PointerEvent) => {
      const target = document.elementFromPoint(pointerEvent.clientX, pointerEvent.clientY)
        ?.closest<HTMLTableCellElement>('th[data-column-key]')
        ?.dataset.columnKey as ShippingLayoutColumnKey | undefined
      cleanup()
      if (moved && target) dropColumn(sourceKey, target)
    }
    const cancel = () => cleanup()
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', finish)
    window.addEventListener('pointercancel', cancel)
  }

  function renderCell(key: ShippingLayoutColumnKey, shipment: Shipment) {
    switch (key) {
      case 'status': return <td key={key}><span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Hidden'}</span></td>
      case 'salesOrderNumber': return <td key={key}><strong><Highlight value={shipment.salesOrderNumber ?? ''} query={deferredSearch} /></strong></td>
      case 'qaArrivalDate': return <td key={key}>{formatDate(shipment.qaArrivalDate)}</td>
      case 'partNumber': return <td key={key}><Highlight value={shipment.partNumber ?? ''} query={deferredSearch} /></td>
      case 'purchaseOrderNumber': return <td key={key}><Highlight value={shipment.purchaseOrderNumber ?? ''} query={deferredSearch} /></td>
      case 'customer': return <td className="customer-cell" key={key}><Highlight value={shipment.customer ?? ''} query={deferredSearch} /></td>
      case 'taskType': return <td key={key}><span className="type-pill"><Highlight value={shipment.taskType ?? ''} query={deferredSearch} /></span></td>
      case 'quantity': return <td className="numeric" key={key}>{shipment.quantity?.toLocaleString() ?? '—'}</td>
      case 'dollarValue': return <td className="numeric" key={key}>{formatCurrency(shipment.dollarValue)}</td>
      case 'shipDate': return <td key={key}><span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{formatDate(shipment.shipDate)}</span></td>
      case 'holdReason': return <td className="long-cell" key={key}><Highlight value={shipment.holdReason ?? ''} query={deferredSearch} /></td>
      case 'sourceRequestedDate': return <td key={key}>{formatDate(shipment.sourceRequestedDate)}</td>
      case 'nextAction': return <td className="long-cell" key={key}><Highlight value={shipment.nextAction ?? ''} query={deferredSearch} /></td>
      case 'lastWorkedAt': return <td key={key}>{formatDate(shipment.lastWorkedAt)}</td>
      case 'comments': return <td className="long-cell" key={key}><Highlight value={shipment.comments ?? ''} query={deferredSearch} /></td>
      case 'assignment': return <td key={key}><strong>{shipment.assignedDisplayName ?? (shipment.assignedGroupName ? 'Group queue' : 'Unassigned')}</strong><small>{shipment.assignedGroupName ?? 'Needs manager assignment'}</small></td>
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

  return (
    <div className="view shipping-view">
      <section className="shipping-toolbar panel">
        <div className="toolbar-top"><div><span className="eyebrow">Controlled register</span><h2>Shipping Status</h2><p>{data ? `${data.total} ${status === 'open' ? 'open' : status === 'shipped' ? 'past' : 'total'} shipments in this view. Drag headers to move columns or drag their right edges to resize.` : 'Loading shipment register...'}</p></div><div className="toolbar-actions-inline">{canImport && <button className="button ghost" type="button" onClick={() => setImportOpen(true)}><FileUp size={15} /> Import Excel</button>}{canCreate && <button className="button primary" type="button" onClick={() => setCreating(true)}><Plus size={15} /> Add shipment</button>}</div></div>
        <div className="filter-row">
          <div className="segmented" aria-label="Shipment status"><button className={status === 'open' ? 'active' : ''} type="button" onClick={() => setStatus('open')}>Open</button><button className={status === 'shipped' ? 'active' : ''} type="button" onClick={() => setStatus('shipped')}>Past shipments</button>{canAll && <button className={status === 'all' ? 'active' : ''} type="button" onClick={() => setStatus('all')}>All</button>}</div>
          <label className="search-box"><Search size={16} /><span className="sr-only">Search shipments</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search order, part, customer, action..." />{search && <button type="button" onClick={() => setSearch('')} aria-label="Clear search"><X size={14} /></button>}</label>
          <label className="compact-select"><SlidersHorizontal size={15} /><span className="sr-only">Queue scope</span><select value={scope} onChange={(event) => setScope(event.target.value as typeof scope)}><option value="mine">{canReviewUnassigned ? 'My queue + unassigned' : 'My queue'}</option>{canTeam && <option value="team">My groups</option>}{canAll && <option value="all">All shipments</option>}</select></label>
          <label className="compact-select"><ArrowRightLeft size={15} /><span className="sr-only">Sort order</span><select value={sort} onChange={(event) => setSort(event.target.value as typeof sort)}><option value="oldest">Oldest first</option><option value="ship-date">Ship date</option></select></label>
        </div>
        {deferredSearch.trim() && <p className="filter-summary"><Search size={13} /> Highlighting matches for <mark>{deferredSearch.trim()}</mark></p>}
      </section>

      {error && <p className="notice error"><AlertTriangle size={16} />{error}</p>}
      <section className="shipping-register panel" aria-busy={loading}>
        {(loading && !data) || !layout ? <div className="loading-panel">Loading Shipping Status...</div> : data?.items.length ? (
          <div className="shipping-table-wrap">
            <table className="shipping-table layout-controlled" style={{ minWidth: shippingTableWidth, width: shippingTableWidth }}>
              <colgroup><col style={{ width: 44 }} />{visibleColumns.map((column) => <col style={{ width: column.width }} key={column.key} />)}<col style={{ width: 44 }} /></colgroup>
              <thead><tr><th className="sticky-col row-number">#</th>{visibleColumns.map((column) => <th className={`draggable-column-header ${draggingColumn === column.key ? 'is-dragging' : ''}`} data-column-key={column.key} key={column.key} onPointerDown={(event) => beginColumnDrag(event, column.key)}><span className="column-header-label">{SHIPPING_COLUMN_METADATA[column.key].label}</span><span className="column-resize-handle" role="separator" aria-label={`Resize ${SHIPPING_COLUMN_METADATA[column.key].label}`} aria-orientation="vertical" onPointerDown={(event) => beginColumnResize(event, column)} /></th>)}<th aria-label="Open record" /></tr></thead>
              <tbody>{data.items.map((shipment, index) => (
                <tr className={`${shipment.dueState === 'Past due' ? 'past-due-row' : ''} ${shipment.isShipped ? 'shipped-row' : ''}`} key={shipment.id} onClick={() => setSelected(shipment)}>
                  <td className="sticky-col row-number">{index + 1}</td>
                  {visibleColumns.map((column) => renderCell(column.key, shipment))}
                  <td><button className="row-open" type="button" aria-label={`Open ${shipment.salesOrderNumber ?? `shipment ${shipment.id}`}`}><ChevronRight size={16} /></button></td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        ) : <div className="empty-state"><ClipboardList size={28} /><h3>{status === 'open' ? 'No open shipments in this queue' : 'No past shipments in this queue'}</h3><p>Adjust the scope or search, or add a shipment if you have permission.</p></div>}
      </section>

      {selected && <DetailDrawer shipment={selected} user={user} fields={fields} onClose={() => setSelected(null)} onEdit={() => setEditing(true)} onAssign={() => setAssigning(true)} onUpdated={accepted} />}
      {creating && <ShipmentForm fields={fields} onClose={() => setCreating(false)} onSaved={accepted} />}
      {editing && selected && <ShipmentForm shipment={selected} fields={fields} onClose={() => setEditing(false)} onSaved={accepted} />}
      {assigning && selected && <AssignmentDialog shipment={selected} user={user} onClose={() => setAssigning(false)} onSaved={accepted} />}
      {importOpen && <ShippingImportDialog onClose={() => setImportOpen(false)} onImported={() => setRefresh((value) => value + 1)} />}
    </div>
  )
}
