import { CheckCircle2, ChevronRight, GitBranch, Globe, Mail, Plus } from 'lucide-react'
import { StatusBadge } from './ActivityTimeline'
import { canonicalVendorStatus } from './model'
import type { VendorDetail } from './types'

export default function ProcessingMaterials({ threads, selectedId, statuses, canEdit, busy, error, onSelect, onStatus, onNew }: {
  threads: VendorDetail[]; selectedId: number | null; statuses: string[]; canEdit: boolean; busy: boolean; error: string | null
  onSelect: (id: number) => void; onStatus: (thread: VendorDetail, status: string) => void; onNew: () => void
}) {
  const ready = threads.filter(({ request }) => canonicalVendorStatus(request.status) === 'Quote received').length
  return <aside className="qs-processing" aria-label="Outside Processing and Materials">
    <section className="qs-request-card">
      <header className="qs-card-heading"><h3><GitBranch size={16} />Outside Processing and Materials</h3><span>{ready}/{threads.length} ready</span></header>
      {threads.length ? <div className="qs-processing-list">{threads.map(thread => {
        const { request } = thread
        const Icon = request.vendorEmail ? Mail : Globe
        return <article key={request.id} className={`qs-processing-item${selectedId === request.id ? ' is-selected' : ''}`}>
          <button className="qs-processing-open" type="button" onClick={() => onSelect(request.id)} aria-pressed={selectedId === request.id}>
            <span className="qs-fact-icon"><Icon size={17} /></span><span><strong>{request.title}</strong><small>{[request.vendorName, request.partNumber].filter(Boolean).join(' · ') || (request.vendorEmail ? 'Vendor correspondence' : 'Website or file reference')}</small></span><ChevronRight size={15} />
          </button>
          <div className="qs-processing-status"><small>{request.vendorEmail ? `${request.messageCount} email${request.messageCount === 1 ? '' : 's'}` : 'Reference pricing'}</small>
            {canEdit && request.canEdit ? <select aria-label={`Status for ${request.title}`} value={canonicalVendorStatus(request.status)} disabled={busy} onChange={event => onStatus(thread, event.target.value)}>{statuses.map(status => <option key={status}>{status}</option>)}</select> : <StatusBadge status={canonicalVendorStatus(request.status)} />}
          </div>
        </article>
      })}</div> : <div className="qs-processing-empty"><CheckCircle2 size={24} /><p>No RFQs tracked yet</p><span>Add outside processing or material pricing, with or without a vendor email.</span></div>}
      {error && <p className="vq-error" role="alert">{error}</p>}
      {canEdit && <button className="vq-button qs-add-rfq" type="button" disabled={busy} onClick={onNew}><Plus size={15} />Add RFQ</button>}
      <p className="qs-processing-hint">Mark a sent email Rates requested to set Waiting on vendor. Confirm pricing with Quote received.</p>
    </section>
  </aside>
}
