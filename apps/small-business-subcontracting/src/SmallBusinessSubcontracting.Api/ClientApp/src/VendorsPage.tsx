import { useDeferredValue, useEffect, useState } from 'react'
import {
  ArrowDownToLine,
  Building2,
  CalendarDays,
  FilePlus2,
  Mail,
  Phone,
  RefreshCw,
  Search,
  Tag,
  Upload,
  X,
} from 'lucide-react'
import { api, queryString } from './api'
import { highlight } from './highlight'
import type { AppUser, SyncResult, VendorDetail, VendorSummary } from './types'

const compliancePermission = 'small-business-subcontracting.compliance.manage'
const documentPermission = 'small-business-subcontracting.documents.manage'
const syncPermission = 'small-business-subcontracting.fulcrum.sync'

interface VendorsPageProps {
  user: AppUser
  selectedVendorId: number | null
  onOpenVendor: (id: number) => void
  onCloseVendor: () => void
  onError: (message: string) => void
}

export default function VendorsPage({
  user,
  selectedVendorId,
  onOpenVendor,
  onCloseVendor,
  onError,
}: VendorsPageProps) {
  const [query, setQuery] = useState('')
  const deferredQuery = useDeferredValue(query)
  const [vendors, setVendors] = useState<VendorSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [syncing, setSyncing] = useState(false)
  const [syncResult, setSyncResult] = useState<SyncResult | null>(null)
  const [detail, setDetail] = useState<VendorDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [refreshKey, setRefreshKey] = useState(0)

  useEffect(() => {
    let active = true
    const timer = window.setTimeout(() => {
      setLoading(true)
      api<VendorSummary[]>(`/api/vendors${queryString({ query: deferredQuery })}`)
        .then((result) => { if (active) setVendors(result) })
        .catch((error: Error) => { if (active) onError(error.message) })
        .finally(() => { if (active) setLoading(false) })
    }, 180)
    return () => {
      active = false
      window.clearTimeout(timer)
    }
  }, [deferredQuery, onError, refreshKey])

  useEffect(() => {
    if (selectedVendorId === null) {
      setDetail(null)
      return
    }
    let active = true
    setDetailLoading(true)
    api<VendorDetail>(`/api/vendors/${selectedVendorId}`)
      .then((result) => { if (active) setDetail(result) })
      .catch((error: Error) => { if (active) onError(error.message) })
      .finally(() => { if (active) setDetailLoading(false) })
    return () => { active = false }
  }, [onError, selectedVendorId])

  async function synchronize() {
    setSyncing(true)
    setSyncResult(null)
    try {
      const result = await api<SyncResult>('/api/vendors/sync', { method: 'POST' })
      setSyncResult(result)
      setRefreshKey((value) => value + 1)
    } catch (error) {
      onError(error instanceof Error ? error.message : 'Vendor synchronization failed.')
    } finally {
      setSyncing(false)
    }
  }

  return (
    <>
      <section className="page-heading">
        <div>
          <p className="eyebrow">Vendor Register</p>
          <h1>Fulcrum vendors, local compliance context</h1>
          <p className="page-intro">Identity and contacts refresh from Fulcrum. Business-size records and documents remain controlled in Arda.</p>
        </div>
        {user.permissions.includes(syncPermission) && (
          <button className="primary-button" type="button" onClick={synchronize} disabled={syncing}>
            <RefreshCw size={16} className={syncing ? 'spin' : ''} />
            {syncing ? 'Syncing vendors' : 'Sync Fulcrum'}
          </button>
        )}
      </section>

      {syncResult && (
        <div className="success-banner" role="status">
          Sync complete: {syncResult.added} added, {syncResult.updated} updated, {syncResult.unchanged} unchanged, {syncResult.contactCount} contacts.
        </div>
      )}

      <section className="registry-panel">
        <div className="registry-toolbar">
          <label className="search-field">
            <Search size={17} />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Search vendor, code, contact, email, phone, or business size"
              aria-label="Search vendors"
            />
            {query && <button type="button" onClick={() => setQuery('')} aria-label="Clear search"><X size={15} /></button>}
          </label>
          <span className="record-count">{vendors.length.toLocaleString()} records</span>
        </div>

        <div className="vendor-table-wrap">
          <table className="data-table vendor-table">
            <thead>
              <tr>
                <th>Vendor</th>
                <th>Primary Contact</th>
                <th>Business Size</th>
                <th>Last Certification</th>
                <th>Documents</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {!loading && vendors.map((vendor) => {
                const contact = vendor.contacts[0]
                return (
                  <tr
                    key={vendor.id}
                    className="clickable-row"
                    onClick={() => onOpenVendor(vendor.id)}
                    onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') onOpenVendor(vendor.id) }}
                    tabIndex={0}
                  >
                    <td>
                      <strong>{highlight(vendor.name, deferredQuery)}</strong>
                      <span className="cell-secondary">{vendor.vendorCode ? highlight(vendor.vendorCode, deferredQuery) : 'No vendor code'}</span>
                    </td>
                    <td>
                      {contact ? <>
                        <span>{highlight(contact.name, deferredQuery)}</span>
                        <span className="cell-secondary">{contact.email ? highlight(contact.email, deferredQuery) : contact.phone ?? 'No contact details'}</span>
                      </> : <span className="muted">No Fulcrum contact</span>}
                    </td>
                    <td>
                      <div className="tag-list">
                        {vendor.businessSizes.length
                          ? vendor.businessSizes.map((tag) => <span className="tag-pill" key={tag.id}>{highlight(tag.name, deferredQuery)}</span>)
                          : <span className="muted">Not classified</span>}
                      </div>
                    </td>
                    <td>{formatDate(vendor.lastCertificationDate)}</td>
                    <td><span className="document-count">{vendor.documentCount}</span></td>
                    <td><span className={`status-pill ${vendor.active ? 'active' : 'inactive'}`}>{vendor.active ? 'Active' : 'Inactive'}</span></td>
                  </tr>
                )
              })}
              {loading && <tr><td colSpan={6}><div className="table-state">Loading vendor records...</div></td></tr>}
              {!loading && vendors.length === 0 && <tr><td colSpan={6}><div className="table-state">No vendors match this search.</div></td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      {selectedVendorId !== null && (
        <div className="drawer-scrim" onMouseDown={(event) => { if (event.target === event.currentTarget) onCloseVendor() }}>
          <aside className="record-drawer" aria-label="Vendor record">
            <button className="drawer-close" type="button" onClick={onCloseVendor} aria-label="Close vendor record"><X size={19} /></button>
            {detailLoading && <div className="drawer-state">Loading vendor record...</div>}
            {!detailLoading && detail && (
              <VendorRecord
                detail={detail}
                canManageCompliance={user.permissions.includes(compliancePermission)}
                canManageDocuments={user.permissions.includes(documentPermission)}
                onUpdated={(updated) => {
                  setDetail(updated)
                  setRefreshKey((value) => value + 1)
                }}
                onError={onError}
              />
            )}
          </aside>
        </div>
      )}
    </>
  )
}

function VendorRecord({
  detail,
  canManageCompliance,
  canManageDocuments,
  onUpdated,
  onError,
}: {
  detail: VendorDetail
  canManageCompliance: boolean
  canManageDocuments: boolean
  onUpdated: (detail: VendorDetail) => void
  onError: (message: string) => void
}) {
  const [tagNames, setTagNames] = useState<string[]>(() => detail.vendor.businessSizes.map((tag) => tag.name))
  const [newTag, setNewTag] = useState('')
  const [certificationDate, setCertificationDate] = useState(detail.vendor.lastCertificationDate ?? '')
  const [saving, setSaving] = useState(false)
  const [uploading, setUploading] = useState(false)

  useEffect(() => {
    setTagNames(detail.vendor.businessSizes.map((tag) => tag.name))
    setCertificationDate(detail.vendor.lastCertificationDate ?? '')
  }, [detail])

  function addTag(name: string) {
    const value = name.trim()
    if (!value || tagNames.some((tag) => tag.toLowerCase() === value.toLowerCase())) return
    setTagNames((current) => [...current, value])
    setNewTag('')
  }

  async function saveCompliance() {
    setSaving(true)
    try {
      const updated = await api<VendorDetail>(`/api/vendors/${detail.vendor.id}/compliance`, {
        method: 'PUT',
        body: JSON.stringify({
          expectedVersion: detail.vendor.version,
          lastCertificationDate: certificationDate || null,
          businessSizes: tagNames,
        }),
      })
      onUpdated(updated)
    } catch (error) {
      onError(error instanceof Error ? error.message : 'The compliance record could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  async function uploadDocument(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = event.currentTarget
    const data = new FormData(form)
    setUploading(true)
    try {
      const updated = await api<VendorDetail>(`/api/vendors/${detail.vendor.id}/documents`, {
        method: 'POST',
        body: data,
      })
      form.reset()
      onUpdated(updated)
    } catch (error) {
      onError(error instanceof Error ? error.message : 'The document could not be uploaded.')
    } finally {
      setUploading(false)
    }
  }

  const primary = detail.vendor.contacts[0]
  return (
    <div className="record-content">
      <header className="record-header">
        <div className="record-icon"><Building2 size={22} /></div>
        <div>
          <p className="eyebrow">Vendor Record</p>
          <h2>{detail.vendor.name}</h2>
          <p>{detail.vendor.vendorCode ?? 'No vendor code'} · <span className={detail.vendor.active ? 'text-active' : 'muted'}>{detail.vendor.active ? 'Active in Fulcrum' : 'Inactive in Fulcrum'}</span></p>
        </div>
      </header>

      <div className="record-metadata">
        {primary ? <>
          <div><span>Primary contact</span><strong>{primary.name}</strong>{primary.position && <small>{primary.position}</small>}</div>
          <div><span>Contact details</span><strong>{primary.email ? <a href={`mailto:${primary.email}`}><Mail size={14} /> {primary.email}</a> : 'No email'}</strong>{primary.phone && <small><Phone size={13} /> {primary.phone}</small>}</div>
        </> : <div><span>Contact</span><strong>No Fulcrum contact on file</strong></div>}
        <div><span>Last Fulcrum sync</span><strong>{formatDateTime(detail.vendor.lastSyncedAt)}</strong></div>
      </div>

      <section className="record-section">
        <div className="section-title"><div><Tag size={17} /><h3>Business Size Certification</h3></div><span>Arda controlled</span></div>
        <label className="form-field">
          <span>Date of last certification</span>
          <input type="date" value={certificationDate} max={today()} onChange={(event) => setCertificationDate(event.target.value)} disabled={!canManageCompliance} />
        </label>
        <div className="form-field">
          <span>Business size tags</span>
          <div className="editable-tags">
            {tagNames.map((tag) => (
              <span className="tag-pill strong" key={tag}>{tag}
                {canManageCompliance && <button type="button" onClick={() => setTagNames((current) => current.filter((item) => item !== tag))} aria-label={`Remove ${tag}`}><X size={12} /></button>}
              </span>
            ))}
            {tagNames.length === 0 && <span className="muted">No classifications assigned</span>}
          </div>
          {canManageCompliance && <div className="tag-entry">
            <input
              value={newTag}
              list="available-business-sizes"
              onChange={(event) => setNewTag(event.target.value)}
              onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); addTag(newTag) } }}
              placeholder="Choose or create a tag"
            />
            <datalist id="available-business-sizes">
              {detail.availableBusinessSizes.map((tag) => <option key={tag.id} value={tag.name} />)}
            </datalist>
            <button className="secondary-button" type="button" onClick={() => addTag(newTag)}>Add tag</button>
          </div>}
        </div>
        {canManageCompliance && <button className="primary-button" type="button" onClick={saveCompliance} disabled={saving}>{saving ? 'Saving' : 'Save compliance'}</button>}
      </section>

      <section className="record-section">
        <div className="section-title"><div><FilePlus2 size={17} /><h3>Associated Documents</h3></div><span>{detail.documents.length} files</span></div>
        {canManageDocuments && (
          <form className="document-upload" onSubmit={uploadDocument}>
            <label className="form-field"><span>Document</span><input type="file" name="file" accept=".pdf,.doc,.docx,.xls,.xlsx,.csv,.txt" required /></label>
            <div className="form-grid">
              <label className="form-field"><span>Document type</span><input name="documentType" placeholder="e.g. Size certification" maxLength={100} required /></label>
              <label className="form-field"><span>Document date</span><input type="date" name="documentDate" max={today()} defaultValue={today()} required /></label>
            </div>
            <label className="form-field"><span>Notes</span><textarea name="notes" rows={2} maxLength={1000} placeholder="Optional context for this file" /></label>
            <button className="secondary-button" type="submit" disabled={uploading}><Upload size={15} /> {uploading ? 'Uploading' : 'Attach document'}</button>
          </form>
        )}
        <div className="document-list">
          {detail.documents.map((document) => (
            <a className="document-row" key={document.id} href={`/api/documents/${document.id}/download`}>
              <div className="file-monogram">{extension(document.fileName)}</div>
              <div><strong>{document.fileName}</strong><span>{document.documentType} · {formatDate(document.documentDate)} · {formatBytes(document.fileSize)}</span>{document.notes && <small>{document.notes}</small>}</div>
              <ArrowDownToLine size={17} />
            </a>
          ))}
          {detail.documents.length === 0 && <div className="empty-inline">No documents have been attached to this vendor.</div>}
        </div>
      </section>

      <section className="record-section audit-section">
        <div className="section-title"><div><CalendarDays size={17} /><h3>Record History</h3></div><span>{detail.auditHistory.length} events</span></div>
        <div className="audit-list">
          {detail.auditHistory.map((audit) => <div className="audit-row" key={audit.id}><i /><div><strong>{audit.kind}</strong><p>{audit.summary}</p><span>{audit.actor} · {formatDateTime(audit.occurredAt)}</span></div></div>)}
        </div>
      </section>
    </div>
  )
}

function formatDate(value: string | null) {
  if (!value) return 'Not recorded'
  return new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`))
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(value))
}

function today() { return new Date().toISOString().slice(0, 10) }
function extension(fileName: string) { return fileName.split('.').pop()?.slice(0, 4).toUpperCase() || 'FILE' }
function formatBytes(value: number) { return value < 1024 * 1024 ? `${Math.max(1, Math.round(value / 1024))} KB` : `${(value / 1024 / 1024).toFixed(1)} MB` }
