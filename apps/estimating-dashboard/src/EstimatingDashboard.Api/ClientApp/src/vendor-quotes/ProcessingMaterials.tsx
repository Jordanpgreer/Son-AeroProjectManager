import { Plus, Unlink } from 'lucide-react'
import type { ReactNode } from 'react'
import { StatusBadge } from './ActivityTimeline'
import { canonicalVendorStatus, dateOnly } from './model'
import type { VendorDetail, VendorMessage } from './types'
import './processing-materials.css'

export default function ProcessingMaterials({
  threads, selectedId, statuses, canEdit, busy, error, buyCount, opCount, buyChecking = false, opChecking = false,
  onSelect, onStatus, onNew, onUntrack, children,
}: {
  threads: VendorDetail[]; selectedId: number | null; statuses: string[]; canEdit: boolean; busy: boolean; error: string | null
  buyCount?: number | null; opCount?: number | null; buyChecking?: boolean; opChecking?: boolean
  onSelect: (id: number) => void; onStatus: (thread: VendorDetail, status: string) => void; onNew: () => void
  onUntrack: (message: VendorMessage) => void; children?: ReactNode
}) {
  const ready = threads.filter(({ request }) => canonicalVendorStatus(request.status) === 'Quote received').length
  const showBuyCount = buyCount !== undefined || buyChecking
  const showOpCount = opCount !== undefined || opChecking

  return <section id="qs-outside-processing-materials" className="qs-rfq-section" tabIndex={-1} aria-labelledby="qs-rfq-heading">
    <div className="qs-request-card">
      <header className="qs-rfq-heading">
        <h3 id="qs-rfq-heading">Outside Processing and Materials</h3>
        {(showBuyCount || showOpCount) && <div className="qs-rfq-counts" role="status">
          {showBuyCount && <span>{buyCount != null
            ? <><strong className={buyCount > 0 ? 'qs-rfq-count-pending' : undefined}>{buyCount.toLocaleString()}</strong> {buyCount === 1 ? 'buy item needs' : 'buy items need'} vendor quotes</>
            : buyChecking ? 'Checking buy items…' : 'Buy item count unavailable'}</span>}
          {showOpCount && <span>{opCount != null
            ? <><strong>{opCount.toLocaleString()}</strong> {opCount === 1 ? 'OP operation' : 'OP operations'}</>
            : opChecking ? 'Checking OP operations…' : 'OP count unavailable'}</span>}
        </div>}
        {canEdit && <button className="vq-button qs-rfq-add" type="button" disabled={busy} onClick={onNew}><Plus size={15} />Add RFQ</button>}
      </header>
      {threads.length > 0 ? <>
        <div className="qs-rfq-table-scroll" role="region" aria-label="Tracked RFQs" tabIndex={0}>
          <table className="qs-rfq-table" aria-label="Tracked RFQs">
            <thead><tr>
              <th scope="col">Part</th>
              <th scope="col">Operation / Material</th>
              <th scope="col">Vendor</th>
              <th scope="col">Status</th>
              <th scope="col">Tracked since</th>
            </tr></thead>
            <tbody>{threads.map(thread => {
              const { request } = thread
              const status = canonicalVendorStatus(request.status)
              const automaticEmail = thread.messages.length === 1 && thread.isAutomaticCorrespondence ? thread.messages[0] : null
              return <tr key={request.id} className={selectedId === request.id ? 'is-selected' : undefined}>
                <td><strong>{request.partNumber || '—'}</strong></td>
                <th scope="row">
                  <button className="qs-text-button qs-rfq-open" type="button" onClick={() => onSelect(request.id)} aria-pressed={selectedId === request.id}>{request.title}</button>
                  {automaticEmail && canEdit && request.canEdit && <button className="qs-text-button qs-rfq-untrack" type="button" disabled={busy}
                    aria-label={`Remove RFQ tracking for ${automaticEmail.subject || 'email'}`} onClick={() => onUntrack(automaticEmail)}>
                    <Unlink size={13} />Remove RFQ tracking
                  </button>}
                </th>
                <td>{request.vendorName || request.vendorEmail || '—'}</td>
                <td>{canEdit && request.canEdit
                  ? <select aria-label={`Status for ${request.title}`} value={status} disabled={busy} onChange={event => onStatus(thread, event.target.value)}>{statuses.map(option => <option key={option}>{option}</option>)}</select>
                  : <StatusBadge status={status} />}</td>
                <td><time dateTime={request.createdAt}>{dateOnly(request.createdAt)}</time></td>
              </tr>
            })}</tbody>
          </table>
        </div>
        <p className="qs-rfq-ready">{ready} of {threads.length} RFQs ready</p>
      </> : <p className="qs-rfq-empty">No RFQs tracked yet.</p>}
      {error && <p className="vq-error" role="alert">{error}</p>}
    </div>
    {children && <div className="qs-rfq-review">{children}</div>}
  </section>
}
