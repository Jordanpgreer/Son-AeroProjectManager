import { useEffect, useState } from 'react'
import {
  Eye,
  ExternalLink,
  FileText,
  GitPullRequest,
  Layers3,
  MapPin,
  Plus,
  Search,
  X,
} from 'lucide-react'
import HighlightedText from './HighlightedText'
import { engineeringPermissionKeys, hasEngineeringPermission } from './permissions'

interface DrawingRecord {
  id: number
  drawingNumber: string
  title: string
  customer: string
  partNumbers: string[]
  specifications: string[]
  approvalStatus: string
  currentRevision: string | null
  currentRevisionDate: string | null
  effectiveDate: string | null
  isObsolete: boolean
  physicalMylarLocation: string | null
  isMylarCheckedOut: boolean
  mylarCount: number
  checkedOutMylarCount: number
  createdAt: string
  revisionCount: number
  attachmentRevisionId: number | null
  attachmentFileName: string | null
  attachmentStatus: string | null
  pendingRevisionCount: number
  pendingRevisionNumber: string | null
  pendingRevisionStatus: string | null
}

interface DrawingDashboardProps {
  permissions: string[]
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
  if (record.attachmentRevisionId) return 'Controlled file'
  if (record.revisionCount > 0) return 'Metadata drawing'
  return 'Drawing index'
}

function statusLabel(value: string) {
  if (value === 'Obsolete') return 'Archived'
  return value.replace(/([a-z])([A-Z])/g, '$1 $2')
}

export default function DrawingDashboard({ permissions, onEditDrawing, onCreateDrawing }: DrawingDashboardProps) {
  const can = (permission: string) => hasEngineeringPermission(permissions, permission)
  const canCreate = can(engineeringPermissionKeys.drawingCreate)
  const canViewPending = can(engineeringPermissionKeys.pendingRevisionsView)
  const canViewHistory = can(engineeringPermissionKeys.revisionHistoryView)
  const canViewSpecifications = can(engineeringPermissionKeys.specificationsView)
  const canViewFiles = can(engineeringPermissionKeys.drawingFilesView)
  const canViewMylar = can(engineeringPermissionKeys.mylarView)
  const [drawings, setDrawings] = useState<DrawingRecord[]>([])
  const [query, setQuery] = useState('')
  const [lifecycle, setLifecycle] = useState<'active' | 'archived' | 'all'>('active')
  const [status, setStatus] = useState('all')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [previewId, setPreviewId] = useState<number | null>(null)

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

  useEffect(() => {
    if (previewId === null) return
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setPreviewId(null)
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [previewId])

  const visible = drawings.filter(record => {
    if (lifecycle === 'active' && record.isObsolete) return false
    if (lifecycle === 'archived' && !record.isObsolete) return false
    return status === 'all' || record.approvalStatus === status
  })
  const activeCount = drawings.filter(record => !record.isObsolete).length
  const archivedCount = drawings.filter(record => record.isObsolete).length
  const reviewCount = drawings.filter(record => record.pendingRevisionStatus === 'UnderReview').length
  const approvedCount = drawings.filter(record => !record.isObsolete && record.approvalStatus === 'Approved').length
  const normalizedQuery = query.trim()
  const previewDrawing = drawings.find(record => record.id === previewId) ?? null

  return <div className="drawing-dashboard">
    {error && <div className="inline-alert" role="alert">{error}<button type="button" onClick={() => setError(null)}><X size={15}/></button></div>}

    <section className="drawing-dashboard-kpis">
      <article className="drawing-stat tone-ink"><span>Active drawings</span><strong>{activeCount}</strong><small>available for engineering work</small></article>
      <article className="drawing-stat tone-steel"><span>Approved drawings</span><strong>{approvedCount}</strong><small>released for controlled use</small></article>
      {canViewPending && <article className="drawing-stat tone-gold"><span>Under review</span><strong>{reviewCount}</strong><small>awaiting a disposition</small></article>}
      <article className="drawing-stat tone-graphite"><span>Archived</span><strong>{archivedCount}</strong><small>preserved historical drawings</small></article>
    </section>

    <section className="panel drawing-register-panel" aria-busy={loading}>
      <header className="drawing-register-header">
        <div>
          <span className="eyebrow">Drawing control board</span>
          <h2>Drawing register</h2>
          <p>Search controlled drawing records, select a drawing to open it, or view its attached drawing file.</p>
        </div>
        {canCreate && <button className="button" type="button" onClick={onCreateDrawing}><Plus size={15}/> New drawing</button>}
      </header>

      <div className="drawing-register-filters">
        <label className="topbar-search drawing-register-search">
          <Search size={15}/>
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            aria-label="Filter drawing register"
            placeholder={canViewSpecifications ? 'Drawing number, title, design authority, part, specification, or note' : 'Drawing number, title, design authority, part, or note'}
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
            {canViewPending && <option value="Draft">Draft</option>}
            {canViewPending && <option value="UnderReview">Under review</option>}
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
              <th>Design authority / Parts</th>
              <th>Type</th>
              <th>Revision</th>
              <th>Approval</th>
              {canViewFiles && <th>Attachment</th>}
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
                <span className="drawing-number-line">
                  <strong className="technical-id drawing-number-tag"><HighlightedText value={record.drawingNumber} query={normalizedQuery}/></strong>
                  <button
                    className="drawing-preview-trigger"
                    type="button"
                    aria-label={`Preview drawing ${record.drawingNumber}`}
                    aria-expanded={previewId === record.id}
                    onClick={event => {
                      event.stopPropagation()
                      setPreviewId(record.id)
                    }}
                  ><Eye size={14}/></button>
                </span>
                <small><HighlightedText value={record.title} query={normalizedQuery}/></small>
                {canViewPending && record.pendingRevisionNumber && <span className={`drawing-pending-revision is-${record.pendingRevisionStatus?.toLowerCase()}`}>
                  <GitPullRequest size={11}/>
                  Rev {record.pendingRevisionNumber} {record.pendingRevisionStatus === 'UnderReview' ? 'awaiting approval' : 'draft in progress'}
                  {record.pendingRevisionCount > 1 && ` +${record.pendingRevisionCount - 1}`}
                </span>}
              </td>
              <td>
                <span><HighlightedText value={record.customer} query={normalizedQuery}/></span>
                <small>{record.partNumbers.length
                  ? <HighlightedText value={record.partNumbers.join(', ')} query={normalizedQuery}/>
                  : 'No linked parts'}</small>
                {canViewSpecifications && record.specifications.length > 0 && <div className="drawing-spec-tags compact">
                  {record.specifications.map(specification => <span key={specification}><HighlightedText value={specification} query={normalizedQuery}/></span>)}
                </div>}
              </td>
              <td><span className="drawing-type-chip"><Layers3 size={13}/>{recordType(record)}</span></td>
              <td>
                <strong>{record.currentRevision ? `Rev ${record.currentRevision}` : 'No revision'}</strong>
                <small>{shortDate(record.currentRevisionDate)}{(canViewHistory || canViewPending) && ` · ${record.revisionCount} visible`}</small>
              </td>
              <td><span className={`status-pill status-${record.approvalStatus.toLowerCase()}`}>{statusLabel(record.approvalStatus)}</span></td>
              {canViewFiles && <td>
                {record.attachmentRevisionId ? <a
                  className="drawing-pdf-button"
                  href={`/api/drawing-revisions/${record.attachmentRevisionId}/file`}
                  target="_blank"
                  rel="noreferrer"
                  title={record.attachmentFileName ?? 'Open attached drawing file'}
                  onClick={event => event.stopPropagation()}
                >
                  <FileText size={14}/>
                  <span>View file</span>
                  <ExternalLink size={12}/>
                </a> : <span className="attachment-missing">No file</span>}
              </td>}
            </tr>)}
          </tbody>
        </table>
      </div>}
    </section>

    {previewDrawing && <div className="drawing-preview-backdrop" onMouseDown={event => {
      if (event.target === event.currentTarget) setPreviewId(null)
    }}>
      <aside className="drawing-preview-drawer" role="dialog" aria-modal="true" aria-labelledby="drawing-preview-title">
        <header className="drawing-preview-header">
          <span className="drawing-preview-icon" aria-hidden="true"><Eye size={19}/></span>
          <div>
            <span className="eyebrow">Drawing preview</span>
            <h2 id="drawing-preview-title">{previewDrawing.title}</h2>
            <span className="technical-id drawing-number-tag">{previewDrawing.drawingNumber}</span>
          </div>
          <button type="button" className="delete-dialog-close" aria-label="Close drawing preview" onClick={() => setPreviewId(null)}><X size={18}/></button>
        </header>

        <div className="drawing-preview-body">
          <section className="drawing-preview-status">
            <div><span>Approval</span><strong className={`status-pill status-${previewDrawing.approvalStatus.toLowerCase()}`}>{statusLabel(previewDrawing.approvalStatus)}</strong></div>
            <div><span>Current revision</span><strong>{previewDrawing.currentRevision ? `Rev ${previewDrawing.currentRevision}` : 'No revision'}</strong></div>
            <div><span>Effective date</span><strong>{shortDate(previewDrawing.effectiveDate)}</strong></div>
            <div><span>Record type</span><strong>{recordType(previewDrawing)}</strong></div>
          </section>

          <section className="drawing-preview-section">
            <header><span>Design authority and parts</span></header>
            <strong>{previewDrawing.customer}</strong>
            {previewDrawing.partNumbers.length ? <div className="drawing-preview-tags">
              {previewDrawing.partNumbers.map(part => <span key={part}>{part}</span>)}
            </div> : <p>No linked part numbers.</p>}
          </section>

          {canViewPending && previewDrawing.pendingRevisionNumber && <section className={`drawing-preview-pending is-${previewDrawing.pendingRevisionStatus?.toLowerCase()}`}>
            <GitPullRequest size={18}/>
            <span><strong>Revision {previewDrawing.pendingRevisionNumber}</strong><small>{previewDrawing.pendingRevisionStatus === 'UnderReview' ? 'Submitted and awaiting approval' : 'Draft revision in progress'}</small></span>
          </section>}

          {canViewSpecifications && <section className="drawing-preview-section">
            <header><span>Specification tags</span><b>{previewDrawing.specifications.length}</b></header>
            {previewDrawing.specifications.length ? <div className="drawing-preview-tags">
              {previewDrawing.specifications.map(specification => <span key={specification}>{specification}</span>)}
            </div> : <p>No specification tags applied.</p>}
          </section>}

          {canViewMylar && <section className="drawing-preview-section drawing-preview-mylar">
            <header><span>Mylar custody</span></header>
            <div><MapPin size={17}/><span><strong>{previewDrawing.mylarCount ? (previewDrawing.isMylarCheckedOut ? 'Checked out' : 'Checked in') : 'Not registered'}</strong><small>{previewDrawing.physicalMylarLocation || 'No physical location recorded'}</small></span></div>
          </section>}

          {canViewFiles && <section className="drawing-preview-section">
            <header><span>Controlled attachment</span></header>
            {previewDrawing.attachmentRevisionId ? <div className="drawing-preview-file">
              <FileText size={17}/>
              <span><strong>{previewDrawing.attachmentFileName ?? 'Controlled drawing file'}</strong><small>{previewDrawing.attachmentStatus ? statusLabel(previewDrawing.attachmentStatus) : 'Available'}</small></span>
              <a href={`/api/drawing-revisions/${previewDrawing.attachmentRevisionId}/file`} target="_blank" rel="noreferrer" aria-label={`Open file for ${previewDrawing.drawingNumber}`}><ExternalLink size={14}/></a>
            </div> : <p>No controlled file is attached.</p>}
          </section>}

          <div className="drawing-preview-actions">
            <button className="button" type="button" onClick={() => {
              const drawingId = previewDrawing.id
              setPreviewId(null)
              onEditDrawing(drawingId)
            }}><FileText size={14}/> Open drawing record</button>
          </div>
        </div>
      </aside>
    </div>}
  </div>
}
