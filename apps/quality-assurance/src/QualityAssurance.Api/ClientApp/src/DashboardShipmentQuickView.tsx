import { useEffect, useMemo } from 'react'
import { Clock3, ExternalLink, UserRoundCheck, X } from 'lucide-react'
import ShipmentAssignmentForm from './ShipmentAssignmentForm'
import { displayShipmentNumber } from './shipmentNumber'
import { ageInDays, formatCompletionDays, formatCurrency, formatDate, formatDateTime } from './format'
import type { FieldAccess, Shipment, ShipmentFieldKey } from './types'

export default function DashboardShipmentQuickView({
  shipment,
  fields,
  canViewAssignment,
  canAssign,
  onClose,
  onOpen,
  onSaved,
}: {
  shipment: Shipment
  fields: FieldAccess[]
  canViewAssignment: boolean
  canAssign: boolean
  onClose: () => void
  onOpen?: () => void
  onSaved: (shipment: Shipment) => void
}) {
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
  ].some((key) => visible(key as ShipmentFieldKey)) || shipment.isShipped

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  const currentOwner = shipment.assignedDisplayName
    ?? (shipment.assignedGroupName ? `${shipment.assignedGroupName} Queue` : 'Needs Assignment')

  return (
    <div className="drawer-layer" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <aside className="detail-drawer dashboard-quick-view" role="dialog" aria-modal="true" aria-labelledby="dashboard-quick-view-title">
        <header className="drawer-head">
          <div>
            <span className="eyebrow">Dashboard Quick View</span>
            <h2 id="dashboard-quick-view-title">{shipment.externalShipmentUrl && shipment.salesOrderNumber ? <a className="external-record-link" href={shipment.externalShipmentUrl} target="_blank" rel="noreferrer">{displayShipmentNumber(shipment.salesOrderNumber)}<ExternalLink size={15} /></a> : displayShipmentNumber(shipment.salesOrderNumber) ?? `Shipment ${shipment.id}`}</h2>
            <p>{shipment.customer ?? 'Customer hidden'} · {shipment.partNumber ?? 'Part number hidden'}</p>
          </div>
          <button className="icon-button" type="button" onClick={onClose} aria-label="Close quick view"><X size={19} /></button>
        </header>
        {onOpen && <div className="drawer-actions"><button className="button primary" type="button" onClick={onOpen}>Open In Shipping Status <ExternalLink size={14} /></button></div>}
        <div className="drawer-scroll">
          <section className="shipment-hero">
            <span className={`status-badge ${shipment.isShipped ? 'shipped' : ''}`}>{shipment.status ?? 'Status hidden'}</span>
            <span className={`due-pill ${shipment.dueState.toLowerCase().replaceAll(' ', '-')}`}><span />{shipment.dueState}</span>
            <span className="age-badge"><Clock3 size={13} /> {ageInDays(shipment.createdAt)} days in queue</span>
          </section>

          {canViewAssignment && <section className="detail-section dashboard-assignment-summary">
            <div><span className="metric-icon compact"><UserRoundCheck size={15} /></span><div><h3>Current Assignment</h3><p>{currentOwner}</p></div></div>
            {shipment.assignedGroupName && <small>{shipment.assignedGroupName}</small>}
          </section>}

          {canAssign && (
            <section className="detail-section dashboard-assignment-editor">
              <div className="section-heading"><div><span className="eyebrow">Manager Action</span><h3>Assign This Work</h3></div></div>
              <ShipmentAssignmentForm
                key={`${shipment.id}:${shipment.assignedGroupId}:${shipment.assignedUserId}`}
                shipment={shipment}
                onSaved={onSaved}
              />
            </section>
          )}

          {hasShipmentDetails && <section className="detail-section"><h3>Shipment Details</h3><dl>
            {visible('shipDate') && <div><dt>Ship By</dt><dd>{formatDate(shipment.shipDate)}</dd></div>}
            {visible('qaArrivalDate') && <div><dt>Shipment Arrival</dt><dd>{formatDate(shipment.qaArrivalDate)}</dd></div>}
            {visible('dollarValue') && <div><dt>Dollar Value</dt><dd>{formatCurrency(shipment.dollarValue)}</dd></div>}
            {visible('lastWorkedAt') && <div><dt>Last Worked</dt><dd>{formatDateTime(shipment.lastWorkedAt)}</dd></div>}
            {visible('quantity') && <div><dt>Quantity</dt><dd>{shipment.quantity?.toLocaleString() ?? 'Not set'}</dd></div>}
            {visible('sourceRequestedDate') && <div><dt>Source Scheduled</dt><dd>{formatDate(shipment.sourceRequestedDate)}</dd></div>}
            {shipment.isShipped && <div><dt>Days To Completion</dt><dd>{formatCompletionDays(shipment.completionDays)}</dd></div>}
          </dl></section>}
          {visible('partNumber') && <section className="detail-section"><h3>Part Lines</h3><div className="shipment-part-list"><div className="shipment-part-list-head"><span>Part Number</span>{visible('quantity') && <span>Quantity</span>}{visible('dollarValue') && <><span>Unit Price</span><span>Total</span></>}</div>{shipment.parts.map((part) => <div className="shipment-part-list-row" key={part.id || `${part.displayOrder}-${part.partNumber}`}><strong>{part.partNumber}</strong>{visible('quantity') && <span>{part.quantity?.toLocaleString() ?? 'Not Set'}</span>}{visible('dollarValue') && <><span>{formatCurrency(part.unitPrice)}</span><span>{formatCurrency(part.totalValue)}</span></>}</div>)}</div></section>}
          {visible('holdReason') && <section className="detail-section narrative"><h3>Hold Reason</h3><p>{shipment.holdReason || 'No hold reason recorded.'}</p></section>}
          {visible('comments') && <section className="detail-section narrative"><h3>Latest Comment</h3><p>{shipment.comments || 'No comments recorded.'}</p></section>}
        </div>
      </aside>
    </div>
  )
}
