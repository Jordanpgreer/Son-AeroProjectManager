import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Clock3, RefreshCw, Search, ServerCrash } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import './error-tracker.css'

interface ErrorTrackerItem {
  id: number
  applicationId: string
  applicationName: string
  errorCode: string
  explanation: string
  affectedArea: string
  occurredAtUtc: string
  httpStatusCode: number
  requestMethod: string
  requestPath: string
  traceIdentifier: string
  accountName: string | null
  exceptionType: string | null
}

interface ErrorTrackerOverview {
  generatedAtUtc: string
  totalRecorded: number
  last24Hours: number
  affectedModules: number
  items: ErrorTrackerItem[]
}

function formatTimestamp(value: string) {
  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) return value
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'medium',
  }).format(parsed)
}

export default function ErrorTrackerPanel() {
  const [overview, setOverview] = useState<ErrorTrackerOverview | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [applicationId, setApplicationId] = useState('all')
  const [errorCode, setErrorCode] = useState('all')

  async function load(signal?: AbortSignal) {
    setLoading(true)
    setError(null)
    try {
      const result = await portalApi<ErrorTrackerOverview>('/api/admin/errors?limit=500', { signal })
      setOverview(result)
    } catch (cause) {
      if (signal?.aborted) return
      setError(toErrorMessage(cause))
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [])

  const applications = useMemo(() => Array.from(
    new Map((overview?.items ?? []).map((item) => [item.applicationId, item.applicationName])),
  ).sort((left, right) => left[1].localeCompare(right[1])), [overview])

  const errorCodes = useMemo(() => Array.from(
    new Set((overview?.items ?? []).map((item) => item.errorCode)),
  ).sort(), [overview])

  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase()
    return (overview?.items ?? []).filter((item) => {
      if (applicationId !== 'all' && item.applicationId !== applicationId) return false
      if (errorCode !== 'all' && item.errorCode !== errorCode) return false
      if (!term) return true
      return [
        item.applicationName,
        item.errorCode,
        item.explanation,
        item.affectedArea,
        item.requestPath,
        item.accountName ?? '',
        item.traceIdentifier,
      ].some((value) => value.toLocaleLowerCase().includes(term))
    })
  }, [applicationId, errorCode, overview, search])

  return (
    <section className="admin-surface error-tracker" aria-labelledby="error-tracker-heading">
      <header className="error-tracker-head">
        <div>
          <span className="error-tracker-kicker"><ServerCrash size={15} aria-hidden="true" /> System diagnostics</span>
          <h2 id="error-tracker-heading">Recorded errors</h2>
          <p>Server failures across Arda modules. Ordinary validation and access-denied responses are not included.</p>
        </div>
        <button className="ghost-button" type="button" onClick={() => void load()} disabled={loading}>
          <RefreshCw size={15} className={loading ? 'is-spinning' : undefined} aria-hidden="true" />
          {loading ? 'Refreshing...' : 'Refresh'}
        </button>
      </header>

      {error && <div className="error-tracker-alert" role="alert"><AlertTriangle size={18} aria-hidden="true" />{error}</div>}

      <div className="error-tracker-stats" aria-label="Error summary">
        <article><strong>{overview?.last24Hours ?? 0}</strong><span>Last 24 hours</span></article>
        <article><strong>{overview?.affectedModules ?? 0}</strong><span>Modules in the last 24 hours</span></article>
        <article><strong>{overview?.totalRecorded ?? 0}</strong><span>Total recorded</span></article>
        <article><Clock3 size={17} aria-hidden="true" /><span>{overview ? `Updated ${formatTimestamp(overview.generatedAtUtc)}` : 'Waiting for data'}</span></article>
      </div>

      <div className="error-tracker-filters" aria-label="Error filters">
        <label className="error-tracker-search">
          <span>Search errors</span>
          <span><Search size={15} aria-hidden="true" /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Code, affected area, trace ID..." /></span>
        </label>
        <label>
          <span>Module</span>
          <select value={applicationId} onChange={(event) => setApplicationId(event.target.value)}>
            <option value="all">All modules</option>
            {applications.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
          </select>
        </label>
        <label>
          <span>Error code</span>
          <select value={errorCode} onChange={(event) => setErrorCode(event.target.value)}>
            <option value="all">All error codes</option>
            {errorCodes.map((code) => <option key={code} value={code}>{code}</option>)}
          </select>
        </label>
      </div>

      <p className="error-tracker-count" aria-live="polite">
        Showing {filtered.length} of {overview?.items.length ?? 0} most recent errors
      </p>

      {loading && !overview && <div className="admin-loading" role="status">Loading recorded errors...</div>}
      {!loading && !error && overview?.items.length === 0 && (
        <div className="error-tracker-empty">
          <ServerCrash size={28} aria-hidden="true" />
          <strong>No server errors recorded</strong>
          <span>New 5xx failures will appear here automatically.</span>
        </div>
      )}
      {overview && overview.items.length > 0 && filtered.length === 0 && (
        <div className="error-tracker-empty"><Search size={25} aria-hidden="true" /><strong>No errors match these filters</strong></div>
      )}
      {filtered.length > 0 && (
        <div className="error-tracker-table-wrap">
          <table className="error-tracker-table">
            <thead><tr><th>Timestamp</th><th>What it affects</th><th>Error code</th><th>What it means</th><th>Request details</th></tr></thead>
            <tbody>
              {filtered.map((item) => (
                <tr key={item.id}>
                  <td><time dateTime={item.occurredAtUtc}>{formatTimestamp(item.occurredAtUtc)}</time><small>{item.applicationName}</small></td>
                  <td><strong>{item.affectedArea}</strong><small>{item.requestMethod} {item.requestPath}</small></td>
                  <td><code>{item.errorCode}</code><small>HTTP {item.httpStatusCode}</small></td>
                  <td><span>{item.explanation}</span>{item.exceptionType && <small>{item.exceptionType}</small>}</td>
                  <td><span>{item.accountName ?? 'Account unavailable'}</span><small title={item.traceIdentifier}>Trace {item.traceIdentifier}</small></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
