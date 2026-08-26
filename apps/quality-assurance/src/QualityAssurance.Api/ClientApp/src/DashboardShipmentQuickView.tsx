import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Clock3, UserRoundCheck, X } from 'lucide-react'
import { qualityApi } from './api'
import { ageInDays, formatCurrency, formatDate, formatDateTime } from './format'
import type { AssignmentOptions, FieldAccess, Shipment, ShipmentFieldKey } from './types'

export default function DashboardShipmentQuickView({
  shipment,
  fields,
  canViewAssignment,
  canAssign,
  canAssignGroup,
  canAssignUser,
  onClose,
  onSaved,
}: {
  shipment: Shipment
  fields: FieldAccess[]
  canViewAssignment: boolean
  canAssign: boolean
  canAssignGroup: boolean
  canAssignUser: boolean
  onClose: () => void
  onSaved: (shipment: Shipment) => void
}) {
  const [options, setOptions] = useState<AssignmentOptions | null>(null)
  const [groupId, setGroupId] = useState(shipment.assignedGroupId?.toString() ?? '')
  const [userId, setUserId] = useState(shipment.assignedUserId?.toString() ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const visibleFields = useMemo(
    () => new Set(fields.filter((field) => field.canView).map((field) => field.key)),
    [fields],
  )
  const visible = (key: ShipmentFieldKey) => visibleFields.has(key)
  const hasShipmentDetails = [
    'shipDate',
    'qaArrivalDate',
    'dollarValue',
    'lastWorkedAt',
    'quantity',
    'sourceRequestedDate',
  ].some((key) => visible(key as ShipmentFieldKey))

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  useEffect(() => {
    setGroupId(shipment.assignedGroupId?.toString() ?? '')
    setUserId(shipment.assignedUserId?.toString() ?? '')
    setError(null)
  }, [shipment])

  useEffect(() => {
    if (!canAssign) return
    let active = true
    void qualityApi<AssignmentOptions>('/api/assignment-options')
      .then((next) => { if (active) setOptions(next) })
      .catch((cause) => { if (active) setError(cause instanceof Error ? cause.message : 'Assignment options are unavailable.') })
    return () => { active = false }
  }, [canAssign])

  const users = useMemo(
    () => options?.users.filter((candidate) => candidate.groupIds.includes(Number(groupId))) ?? [],
    [groupId, options],
  )

  async function saveAssignment(event: React.FormEvent) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const saved = await qualityApi<Shipment>(`/api/shipments/${shipment.id}/assignment`, {
        method: 'POST',
        body: JSON.stringify({
          version: shipment.version,
          groupId: groupId ? Number(groupId) : null,
          userId: userId ? Number(userId) : null,
        }),
      })
      onSaved(saved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Assignment could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const currentOwner = shipment.assignedDisplayName
    ?? (shipment.assignedGroupName ? `${shipment.assignedGroupName} queue` : 'Needs assignment')

  return (
    <div className="drawer-layer" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <aside className="detail-drawer dashboard-quick-view" role="dialog" aria-modal="true" aria-labelledby="dashboard-quick-view-title">
        <header className="drawer-head">
          <div>
            <span className="eyebrow">Dashboard quick view</span>
            <h2 id="dashboard-quick-view-title">{shipment.salesOrderNumber ?? `Shipment ${shipment.id}`}</h2>
            <p>{shipment.customer ?? 'Customer hidden'} · {shipment.partNumber ?? 'Part number hidden'}</p>
          </div>
          <button className="icon-button" type="button" onClick={onClose} aria-label="Close quick view"><X size={19} /></button>
        </header>
        <div className="drawer-scroll">
          <section className="shipment-hero">
            <span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Status hidden'}</span>
            <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span>
            <span className="age-badge"><Clock3 size={13} /> {ageInDays(shipment.createdAt)} days in queue</span>
          </section>

          {canViewAssignment && <section className="detail-section dashboard-assignment-summary">
            <div><span className="metric-icon compact"><UserRoundCheck size={15} /></span><div><h3>Current assignment</h3><p>{currentOwner}</p></div></div>
            {shipment.assignedGroupName && <small>{shipment.assignedGroupName}</small>}
          </section>}

          {canAssign && (
            <section className="detail-section dashboard-assignment-editor">
              <div className="section-heading"><div><span className="eyebrow">Manager action</span><h3>Assign this work</h3></div></div>
              {error && <p className="notice error"><AlertTriangle size={15} />{error}</p>}
              {!options ? <div className="loading-panel compact" role="status">Loading assignment options...</div> : (
                <form onSubmit={saveAssignment}>
                  <div className="assignment-fields">
                    <label><span>Responsible group</span><select disabled={!canAssignGroup} value={groupId} onChange={(event) => { setGroupId(event.target.value); setUserId('') }}><option value="">Unassigned - manager review</option>{options.groups.map((group) => <option value={group.id} key={group.id}>{group.name} ({group.activeUserCount})</option>)}</select></label>
                    <label><span>Individual owner</span><select disabled={!canAssignUser || !groupId} value={userId} onChange={(event) => setUserId(event.target.value)}><option value="">Group queue / unassigned</option>{users.map((candidate) => <option value={candidate.id} key={candidate.id}>{candidate.displayName}</option>)}</select></label>
                  </div>
                  <button className="button primary dashboard-assignment-save" disabled={saving} type="submit">{saving ? 'Saving assignment...' : 'Save assignment'}</button>
                </form>
              )}
            </section>
          )}

          {hasShipmentDetails && <section className="detail-section"><h3>Shipment details</h3><dl>
            {visible('shipDate') && <div><dt>Ship by</dt><dd>{formatDate(shipment.shipDate)}</dd></div>}
            {visible('qaArrivalDate') && <div><dt>QA arrival</dt><dd>{formatDate(shipment.qaArrivalDate)}</dd></div>}
            {visible('dollarValue') && <div><dt>Dollar value</dt><dd>{formatCurrency(shipment.dollarValue)}</dd></div>}
            {visible('lastWorkedAt') && <div><dt>Last worked</dt><dd>{formatDateTime(shipment.lastWorkedAt)}</dd></div>}
            {visible('quantity') && <div><dt>Quantity</dt><dd>{shipment.quantity?.toLocaleString() ?? 'Not set'}</dd></div>}
            {visible('sourceRequestedDate') && <div><dt>Source scheduled</dt><dd>{formatDate(shipment.sourceRequestedDate)}</dd></div>}
          </dl></section>}
          {visible('holdReason') && <section className="detail-section narrative"><h3>Hold reason</h3><p>{shipment.holdReason || 'No hold reason recorded.'}</p></section>}
          {visible('comments') && <section className="detail-section narrative"><h3>Latest comment</h3><p>{shipment.comments || 'No comments recorded.'}</p></section>}
        </div>
      </aside>
    </div>
  )
}
