import { useEffect, useState } from 'react'
import {
  ExternalLink,
  FileText,
  Layers3,
  Plus,
  Search,
  X,
} from 'lucide-react'

interface DrawingRecord {
  id: number
  drawingNumber: string
  title: string
  customer: string
  partNumbers: string[]
  approvalStatus: string
  currentRevision: string | null
  currentRevisionDate: string | null
  effectiveDate: string | null
  isObsolete: boolean
  physicalMylarLocation: string | null
  isMylarCheckedOut: boolean
  createdAt: string
  revisionCount: number
  attachmentRevisionId: number | null
  attachmentFileName: string | null
  attachmentStatus: string | null
}

interface DrawingDashboardProps {
  onEditDrawing: (drawingId: number) => void
  onCreateDrawing: () => void
}

async function loadDrawings(query: string, signal: AbortSignal): Promise<DrawingRecord[]> {
  const response = await fetch(`/api/drawings?query=${encodeURIComponent(query)}`, {
    credentials: 'include',
    signal,
  })
  if (!response.ok) throw new Error(`Drawing register responded ${response.status}.`)
  return response.json()
}

function shortDate(value: string | null) {
  return value ? new Date(value).toLocaleDateString() : '—'
}

function recordType(record: DrawingRecord) {
  if (record.attachmentRevisionId) return 'Controlled PDF'
  if (record.revisionCount > 0) return 'Metadata drawing'
  return 'Drawing index'
}

function statusLabel(value: string) {
  if (value === 'Obsolete') return 'Archived'
  return value.replace(/([a-z])([A-Z])/g, '$1 $2')
}

export default function DrawingDashboard({ onEditDrawing, onCreateDrawing }: DrawingDashboardProps) {
  const [drawings, setDrawings] = useState<DrawingRecord[]>([])
  const [query, setQuery] = useState('')
  const [lifecycle, setLifecycle] = useState<'active' | 'archived' | 'all'>('active')
  const [status, setStatus] = useState('all')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    void loadDrawings(query, controller.signal)
      .then(setDrawings)
      .catch(cause => {
        if (!controller.signal.aborted) {
          setError(cause instanceof Error ? cause.message : 'Unable to load drawing records.')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [query])

  const visible = drawings.filter(record => {
    if (lifecycle === 'active' && record.isObsolete) return false
    if (lifecycle === 'archived' && !record.isObsolete) return false
    return status === 'all' || record.approvalStatus === status
  })
  const activeCount = drawings.filter(record => !record.isObsolete).length
  const archivedCount = drawings.filter(record => record.isObsolete).length
  const reviewCount = drawings.filter(record => record.approvalStatus === 'UnderReview').length
  const approvedCount = drawings.filter(record => !record.isObsolete && record.approvalStatus === 'Approved').length

  return <div className="drawing-dashboard">
    {error && <div className="inline-alert" role="alert">{error}<button type="button" onClick={() => setError(null)}><X size={15}/></button></div>}

    <section className="drawing-dashboard-kpis">
      <article className="drawing-stat tone-ink"><span>Active drawings</span><strong>{activeCount}</strong><small>available for engineering work</small></article>
      <article className="drawing-stat tone-steel"><span>Approved drawings</span><strong>{approvedCount}</strong><small>released for controlled use</small></article>
      <article className="drawing-stat tone-gold"><span>Under review</span><strong>{reviewCount}</strong><small>awaiting a disposition</small></article>
      <article className="drawing-stat tone-graphite"><span>Archived</span><strong>{archivedCount}</strong><small>preserved historical drawings</small></article>
    </section>

    <section className="panel drawing-register-panel" aria-busy={loading}>
      <header className="drawing-register-header">
        <div>
          <span className="eyebrow">Drawing control board</span>
          <h2>Drawing register</h2>
          <p>Search controlled drawing records, select a drawing to open it, or view its attached PDF.</p>
        </div>
        <button className="button" type="button" onClick={onCreateDrawing}><Plus size={15}/> New drawing</button>
      </header>

      <div className="drawing-register-filters">
        <label className="topbar-search drawing-register-search">
          <Search size={15}/>
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            aria-label="Filter drawing register"
            placeholder="Drawing number, title, customer, part, specification, work order, or note"
          />
        </label>
        <label>
          <span>Lifecycle</span>
          <select value={lifecycle} onChange={event => setLifecycle(event.target.value as typeof lifecycle)}>
            <option value="active">Active</option>
            <option value="archived">Archived</option>
            <option value="all">All records</option>
          </select>
        </label>
        <label>
          <span>Approval status</span>
          <select value={status} onChange={event => setStatus(event.target.value)}>
            <option value="all">All statuses</option>
            <option value="Draft">Draft</option>
            <option value="UnderReview">Under review</option>
            <option value="Approved">Approved</option>
            <option value="Obsolete">Archived</option>
          </select>
        </label>
      </div>

      <div className="drawing-table-summary">
        <span aria-live="polite">{loading ? 'Updating drawing register…' : `${visible.length} record${visible.length === 1 ? '' : 's'} shown`}</span>
        {(query || lifecycle !== 'active' || status !== 'all') && <button type="button" onClick={() => { setQuery(''); setLifecycle('active'); setStatus('all') }}>Clear filters</button>}
      </div>

      {!loading && visible.length === 0 ? <div className="drawing-dashboard-empty">
        <FileText size={28}/>
        <strong>No matching drawing records</strong>
        <p>Adjust the filters or create a new controlled drawing.</p>
      </div> : <div className="drawing-table-wrap">
        <table className="drawing-data-table">
          <thead>
            <tr>
              <th>Drawing</th>
              <th>Customer / Parts</th>
              <th>Type</th>
              <th>Revision</th>
              <th>Approval</th>
              <th>Attachment</th>
            </tr>
          </thead>
          <tbody>
            {visible.map(record => <tr
              key={record.id}
              className="drawing-result-row"
              role="link"
              tabIndex={0}
              aria-label={`Open drawing ${record.drawingNumber}: ${record.title}`}
              onClick={() => onEditDrawing(record.id)}
              onKeyDown={event => {
                if (event.target === event.currentTarget && event.key === 'Enter') {
                  onEditDrawing(record.id)
                }
              }}
            >
              <td>
                <strong className="technical-id">{record.drawingNumber}</strong>
                <small>{record.title}</small>
              </td>
              <td>
                <span>{record.customer}</span>
                <small>{record.partNumbers.join(', ') || 'No linked parts'}</small>
              </td>
              <td><span className="drawing-type-chip"><Layers3 size={13}/>{recordType(record)}</span></td>
              <td>
                <strong>{record.currentRevision ? `Rev ${record.currentRevision}` : 'No revision'}</strong>
                <small>{shortDate(record.currentRevisionDate)} · {record.revisionCount} total</small>
              </td>
              <td><span className={`status-pill status-${record.approvalStatus.toLowerCase()}`}>{statusLabel(record.approvalStatus)}</span></td>
              <td>
                {record.attachmentRevisionId ? <a
                  className="drawing-pdf-button"
                  href={`/api/drawing-revisions/${record.attachmentRevisionId}/file`}
                  target="_blank"
                  rel="noreferrer"
                  title={record.attachmentFileName ?? 'Open attached PDF'}
                  onClick={event => event.stopPropagation()}
                >
                  <FileText size={14}/>
                  <span>View PDF</span>
                  <ExternalLink size={12}/>
                </a> : <span className="attachment-missing">No PDF</span>}
              </td>
            </tr>)}
          </tbody>
        </table>
      </div>}
    </section>
  </div>
}
