import { useDeferredValue, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, CalendarCheck2, Download, FileWarning, Search, Tags, X } from 'lucide-react'
import { api, queryString } from './api'
import { highlight } from './highlight'
import type { DashboardData } from './types'

interface ComplianceDashboardProps {
  onOpenVendor: (id: number) => void
  onError: (message: string) => void
}

export default function ComplianceDashboard({ onOpenVendor, onError }: ComplianceDashboardProps) {
  const [query, setQuery] = useState('')
  const deferredQuery = useDeferredValue(query)
  const [businessSize, setBusinessSize] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [sortBy, setSortBy] = useState('name')
  const [sortDirection, setSortDirection] = useState('asc')
  const [data, setData] = useState<DashboardData>({ vendors: [], businessSizes: [], lastFulcrumSyncAt: null })
  const [loading, setLoading] = useState(true)

  const values = {
    query: deferredQuery,
    businessSize,
    certificationFrom: from,
    certificationTo: to,
    sortBy,
    sortDirection,
  }

  useEffect(() => {
    let active = true
    const timer = window.setTimeout(() => {
      setLoading(true)
      api<DashboardData>(`/api/dashboard${queryString(values)}`)
        .then((result) => { if (active) setData(result) })
        .catch((error: Error) => { if (active) onError(error.message) })
        .finally(() => { if (active) setLoading(false) })
    }, 180)
    return () => {
      active = false
      window.clearTimeout(timer)
    }
  }, [businessSize, deferredQuery, from, onError, sortBy, sortDirection, to])

  function toggleSort(column: string) {
    if (sortBy === column) setSortDirection((direction) => direction === 'asc' ? 'desc' : 'asc')
    else {
      setSortBy(column)
      setSortDirection('asc')
    }
  }

  const certified = data.vendors.filter((vendor) => vendor.lastCertificationDate).length
  const classified = data.vendors.filter((vendor) => vendor.businessSizes.length).length
  const documents = data.vendors.reduce((sum, vendor) => sum + vendor.documentCount, 0)
  const exportUrl = `/api/dashboard/export${queryString(values)}`

  return (
    <>
      <section className="page-heading">
        <div>
          <p className="eyebrow">Compliance Dashboard</p>
          <h1>Small business certification register</h1>
          <p className="page-intro">Find vendors by name or classification, review certification dates, and export exactly what is shown.</p>
        </div>
        <a className="primary-button" href={exportUrl}><Download size={16} /> Export visible results</a>
      </section>

      <section className="summary-strip">
        <div><span>Matching Vendors</span><strong>{data.vendors.length}</strong><small>{loading ? 'Updating results' : 'Current view'}</small></div>
        <div><span>Classified</span><strong>{classified}</strong><small><Tags size={13} /> Business size assigned</small></div>
        <div><span>Certifications Recorded</span><strong>{certified}</strong><small><CalendarCheck2 size={13} /> Date on file</small></div>
        <div><span>Associated Files</span><strong>{documents}</strong><small><FileWarning size={13} /> Across current results</small></div>
      </section>

      <section className="registry-panel dashboard-panel">
        <div className="dashboard-controls">
          <label className="search-field wide">
            <Search size={17} />
            <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Live search vendor name or business size" aria-label="Search compliance records" />
            {query && <button type="button" onClick={() => setQuery('')} aria-label="Clear search"><X size={15} /></button>}
          </label>
          <label className="compact-field"><span>Business size</span><select value={businessSize} onChange={(event) => setBusinessSize(event.target.value)}><option value="">All classifications</option>{data.businessSizes.map((tag) => <option key={tag.id} value={tag.name}>{tag.name}</option>)}</select></label>
          <label className="compact-field"><span>Certified from</span><input type="date" value={from} onChange={(event) => setFrom(event.target.value)} /></label>
          <label className="compact-field"><span>Certified through</span><input type="date" value={to} onChange={(event) => setTo(event.target.value)} /></label>
        </div>

        <div className="vendor-table-wrap">
          <table className="data-table compliance-table">
            <thead>
              <tr>
                <SortableHeader label="Vendor Name" column="name" active={sortBy} direction={sortDirection} onSort={toggleSort} />
                <th>Vendor Code</th>
                <SortableHeader label="Business Size" column="businessSize" active={sortBy} direction={sortDirection} onSort={toggleSort} />
                <SortableHeader label="Last Certification" column="certification" active={sortBy} direction={sortDirection} onSort={toggleSort} />
                <th>Contact</th>
                <th>Documents</th>
              </tr>
            </thead>
            <tbody>
              {!loading && data.vendors.map((vendor) => (
                <tr key={vendor.id} className="clickable-row" onClick={() => onOpenVendor(vendor.id)} tabIndex={0} onKeyDown={(event) => { if (event.key === 'Enter') onOpenVendor(vendor.id) }}>
                  <td><strong>{highlight(vendor.name, deferredQuery)}</strong><span className={`cell-secondary ${vendor.active ? '' : 'text-warning'}`}>{vendor.active ? 'Active' : 'Inactive'} in Fulcrum</span></td>
                  <td>{vendor.vendorCode ?? <span className="muted">Not set</span>}</td>
                  <td><div className="tag-list">{vendor.businessSizes.length ? vendor.businessSizes.map((tag) => <span className="tag-pill" key={tag.id}>{highlight(tag.name, deferredQuery)}</span>) : <span className="muted">Not classified</span>}</div></td>
                  <td>{formatDate(vendor.lastCertificationDate)}</td>
                  <td>{vendor.contacts[0] ? <><span>{vendor.contacts[0].name}</span><span className="cell-secondary">{vendor.contacts[0].email ?? vendor.contacts[0].phone ?? 'No details'}</span></> : <span className="muted">No contact</span>}</td>
                  <td><span className="document-count">{vendor.documentCount}</span></td>
                </tr>
              ))}
              {loading && <tr><td colSpan={6}><div className="table-state">Updating compliance results...</div></td></tr>}
              {!loading && data.vendors.length === 0 && <tr><td colSpan={6}><div className="table-state">No compliance records match these filters.</div></td></tr>}
            </tbody>
          </table>
        </div>
        <footer className="panel-footer">
          <span>{data.vendors.length.toLocaleString()} vendors in this view</span>
          <span>Last Fulcrum sync: {data.lastFulcrumSyncAt ? new Date(data.lastFulcrumSyncAt).toLocaleString() : 'Not yet synchronized'}</span>
        </footer>
      </section>
    </>
  )
}

function SortableHeader({ label, column, active, direction, onSort }: { label: string; column: string; active: string; direction: string; onSort: (column: string) => void }) {
  return <th><button className="sort-button" type="button" onClick={() => onSort(column)}>{label}{active === column ? direction === 'asc' ? <ArrowUp size={13} /> : <ArrowDown size={13} /> : null}</button></th>
}

function formatDate(value: string | null) {
  if (!value) return <span className="missing-date">Not recorded</span>
  return new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`))
}
