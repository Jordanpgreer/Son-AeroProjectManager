import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CalendarDays,
  ChevronDown,
  CheckCircle2,
  Factory,
  FileSpreadsheet,
  Pencil,
  Plus,
  Save,
  Search,
  ShieldCheck,
  Trash2,
  UploadCloud,
  X,
} from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import { formatHolidayRange, groupConsecutiveHolidays } from './holidayGroups'
import type {
  DayOfWeekName,
  Holiday,
  ScheduleSettings,
  WorkCenter,
  WorkCenterImportResult,
} from './types'

const DAYS: { value: DayOfWeekName; short: string }[] = [
  { value: 'Monday', short: 'Mon' },
  { value: 'Tuesday', short: 'Tue' },
  { value: 'Wednesday', short: 'Wed' },
  { value: 'Thursday', short: 'Thu' },
  { value: 'Friday', short: 'Fri' },
  { value: 'Saturday', short: 'Sat' },
  { value: 'Sunday', short: 'Sun' },
]

function Notice({ error, message }: { error: string | null; message: string | null }) {
  return (
    <>
      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}
    </>
  )
}

export function WorkCalendarPanel() {
  const [settings, setSettings] = useState<ScheduleSettings | null>(null)
  const [draft, setDraft] = useState<DayOfWeekName[]>([])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    void trackerApi<ScheduleSettings>('/api/settings/work-calendar')
      .then((value) => {
        setSettings(value)
        setDraft(value.workingDays)
      })
      .catch((cause) => setError(toErrorMessage(cause)))
  }, [])

  const changed = settings
    ? [...settings.workingDays].sort().join('|') !== [...draft].sort().join('|')
    : false

  async function save() {
    if (!draft.length || !changed || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const next = await trackerApi<ScheduleSettings>('/api/settings/work-calendar', {
        method: 'PUT',
        body: JSON.stringify({ workingDays: draft }),
      })
      setSettings(next)
      setDraft(next.workingDays)
      setMessage('Work calendar saved and active schedules recalculated.')
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="calendar-heading">
      <header className="admin-surface-head">
        <div><span className="kicker">Company schedule</span><h2 id="calendar-heading">Standard work week</h2><p>These days drive active project dates, Gantt projections, progress, and capacity calculations.</p></div>
        <CalendarDays size={23} aria-hidden="true" />
      </header>
      <Notice error={error} message={message} />
      {!settings ? (
        <div className="admin-loading" role="status">Loading work calendar…</div>
      ) : (
        <>
          <div className="admin-weekdays">
            {DAYS.map((day) => {
              const selected = draft.includes(day.value)
              return (
                <button
                  type="button"
                  key={day.value}
                  className={selected ? 'selected' : ''}
                  aria-pressed={selected}
                  onClick={() => setDraft((current) => selected
                    ? current.filter((value) => value !== day.value)
                    : [...current, day.value])}
                >
                  <strong>{day.short}</strong><span>{selected ? 'Working' : 'Off'}</span>
                </button>
              )
            })}
          </div>
          <div className="admin-inline-save">
            <p>Saving recalculates active schedules. Completed projects remain unchanged.</p>
            <button className="solid-button" type="button" disabled={!changed || !draft.length || saving} onClick={() => void save()}>
              <Save size={15} /> {saving ? 'Recalculating…' : 'Save work week'}
            </button>
          </div>
        </>
      )}
    </section>
  )
}

export function WorkCentersPanel({
  canManage,
  canImport,
}: {
  canManage: boolean
  canImport: boolean
}) {
  const importInputRef = useRef<HTMLInputElement>(null)
  const [items, setItems] = useState<WorkCenter[] | null>(null)
  const [drafts, setDrafts] = useState<Record<number, string>>({})
  const [newName, setNewName] = useState('')
  const [search, setSearch] = useState('')
  const [importFile, setImportFile] = useState<File | null>(null)
  const [importResult, setImportResult] = useState<WorkCenterImportResult | null>(null)
  const [busyId, setBusyId] = useState<number | 'new' | 'import' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function load() {
    try {
      const next = await trackerApi<WorkCenter[]>('/api/work-centers')
      setItems(next)
      setDrafts(Object.fromEntries(next.map((item) => [item.id, item.name])))
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  useEffect(() => { void load() }, [])

  const filtered = useMemo(() => (items ?? []).filter((item) =>
    item.name.toLowerCase().includes(search.trim().toLowerCase())), [items, search])
  const hasUnsavedManualChanges = useMemo(() => (items ?? []).some((item) =>
    (drafts[item.id] ?? item.name).trim() !== item.name), [drafts, items])

  async function add(event: FormEvent) {
    event.preventDefault()
    if (!newName.trim() || busyId !== null) return
    setBusyId('new')
    setError(null)
    try {
      await trackerApi<WorkCenter>('/api/work-centers', {
        method: 'POST',
        body: JSON.stringify({ name: newName.trim() }),
      })
      setNewName('')
      setMessage('Work center added.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusyId(null)
    }
  }

  async function update(item: WorkCenter) {
    const name = drafts[item.id]?.trim()
    if (!name || name === item.name || busyId !== null) return
    setBusyId(item.id)
    setError(null)
    try {
      await trackerApi<WorkCenter>(`/api/work-centers/${item.id}`, {
        method: 'PUT',
        body: JSON.stringify({ name }),
      })
      setMessage('Work center renamed.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusyId(null)
    }
  }

  async function remove(item: WorkCenter) {
    if (busyId !== null) return
    if (!window.confirm(`Delete work center “${item.name}”?`)) return
    setBusyId(item.id)
    setError(null)
    try {
      await trackerApi<void>(`/api/work-centers/${item.id}`, { method: 'DELETE' })
      setMessage('Work center deleted.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusyId(null)
    }
  }

  async function importWorkbook(event: FormEvent) {
    event.preventDefault()
    if (!importFile || busyId !== null) return
    if (hasUnsavedManualChanges) {
      setError('Save or discard work-center name edits before importing a workbook.')
      return
    }
    setBusyId('import')
    setError(null)
    setMessage(null)
    setImportResult(null)
    try {
      const form = new FormData()
      form.append('file', importFile)
      const result = await trackerApi<WorkCenterImportResult>('/api/work-centers/import', {
        method: 'POST',
        headers: { 'X-Requested-With': 'XMLHttpRequest' },
        body: form,
      })
      setImportResult(result)
      setMessage(
        `Import complete: ${result.addedCount} work center${result.addedCount === 1 ? '' : 's'} added and ${result.skippedCount} duplicate${result.skippedCount === 1 ? '' : 's'} skipped.`,
      )
      setImportFile(null)
      if (importInputRef.current) importInputRef.current.value = ''
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusyId(null)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="work-centers-heading">
      <header className="admin-surface-head">
        <div><span className="kicker">Company routing</span><h2 id="work-centers-heading">Work centers and machines</h2><p>Maintain the controlled names used for Project Tracker operation assignments.</p></div>
        <label className="admin-search"><Search size={16} /><span className="sr-only">Search work centers</span><input type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search work centers" /></label>
      </header>
      <Notice error={error} message={message} />
      {canImport && (
        <section className="admin-work-center-import" aria-labelledby="work-center-import-heading">
          <header>
            <span className="admin-work-center-import-icon"><FileSpreadsheet size={21} aria-hidden="true" /></span>
            <div>
              <h3 id="work-center-import-heading">Import from Excel</h3>
              <p>Choose a workbook containing a <strong>Work Center Name</strong> column. Existing names are safely skipped.</p>
            </div>
            <span className="admin-permission-badge"><ShieldCheck size={14} /> Permission controlled</span>
          </header>
          <form className="admin-import-upload" onSubmit={importWorkbook}>
            <label className="admin-file-picker">
              <UploadCloud size={25} aria-hidden="true" />
              <span><strong>{importFile?.name || 'Choose a work-center workbook'}</strong><small>.xlsx only · maximum 5 MB · import adds names and never removes existing work centers</small></span>
              <input
                ref={importInputRef}
                type="file"
                accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                disabled={busyId !== null}
                onChange={(event) => {
                  const file = event.target.files?.[0] ?? null
                  if (file && (!file.name.toLowerCase().endsWith('.xlsx') || file.size > 5 * 1024 * 1024)) {
                    setImportFile(null)
                    setImportResult(null)
                    setMessage(null)
                    setError(file.size > 5 * 1024 * 1024
                      ? 'The workbook is larger than the 5 MB import limit.'
                      : 'Choose a supported .xlsx workbook.')
                    event.target.value = ''
                    return
                  }
                  setImportFile(file)
                  setImportResult(null)
                  setError(null)
                  setMessage(null)
                }}
              />
            </label>
            <button className="solid-button" type="submit" disabled={!importFile || busyId !== null || hasUnsavedManualChanges}>
              <UploadCloud size={16} /> {busyId === 'import' ? 'Importing…' : 'Import work centers'}
            </button>
          </form>
          {hasUnsavedManualChanges && <p className="admin-import-safety">Save or discard edited work-center names before importing.</p>}
          {busyId === 'import' && (
            <div className="admin-work-center-import-progress" role="status">
              <progress aria-label="Uploading and importing work centers" />
              <span>Checking the workbook and adding new names…</span>
            </div>
          )}
          {importResult && (
            <div className="admin-work-center-import-result" role="status">
              <div className="admin-import-stats">
                <span className="is-valid"><strong>{importResult.addedCount}</strong><small>Added</small></span>
                <span><strong>{importResult.skippedCount}</strong><small>Duplicates skipped</small></span>
              </div>
              {importResult.addedNames.length > 0 && (
                <details>
                  <summary>View added work centers</summary>
                  <ul>{importResult.addedNames.map((name) => <li key={name}>{name}</li>)}</ul>
                </details>
              )}
              {importResult.skippedNames.length > 0 && (
                <details>
                  <summary>View skipped duplicate rows</summary>
                  <ul>{importResult.skippedNames.map((name, index) => <li key={`${name}-${index}`}>{name}</li>)}</ul>
                </details>
              )}
            </div>
          )}
        </section>
      )}
      {canManage && (
        <form className="admin-add-row" onSubmit={add}>
          <label><span>New work center</span><input value={newName} disabled={busyId !== null} onChange={(event) => setNewName(event.target.value)} placeholder="CNC Mill" required /></label>
          <button className="solid-button" disabled={busyId !== null}><Plus size={15} /> Add work center</button>
        </form>
      )}
      {!canManage && <p className="admin-import-safety"><ShieldCheck size={15} /> Your access permits workbook imports; manual renaming and deletion remain disabled.</p>}
      {items === null ? <div className="admin-loading">Loading work centers…</div> : (
        <div className="admin-record-list">
          {filtered.map((item) => (
            <div className={`admin-record-row${canManage ? '' : ' read-only'}`} key={item.id}>
              <Factory size={17} aria-hidden="true" />
              {canManage ? (
                <>
                  <label><span className="sr-only">Work center name</span><input value={drafts[item.id] ?? item.name} disabled={busyId !== null} onChange={(event) => setDrafts({ ...drafts, [item.id]: event.target.value })} /></label>
                  <button className="admin-icon-button" type="button" title="Save name" aria-label={`Save ${item.name}`} disabled={busyId !== null || drafts[item.id]?.trim() === item.name} onClick={() => void update(item)}><Pencil size={15} /></button>
                  <button className="admin-icon-button danger" type="button" title="Delete" aria-label={`Delete ${item.name}`} disabled={busyId !== null} onClick={() => void remove(item)}><Trash2 size={15} /></button>
                </>
              ) : <strong className="admin-record-name">{item.name}</strong>}
            </div>
          ))}
          {!filtered.length && <p className="admin-empty">No work centers match this search.</p>}
        </div>
      )}
    </section>
  )
}

function enumerateDates(start: string, end: string) {
  const dates: string[] = []
  const cursor = new Date(`${start}T00:00:00Z`)
  const last = new Date(`${(end || start)}T00:00:00Z`)
  if (!Number.isFinite(cursor.getTime()) || !Number.isFinite(last.getTime()) || cursor > last) return dates
  while (cursor <= last && dates.length < 370) {
    dates.push(cursor.toISOString().slice(0, 10))
    cursor.setUTCDate(cursor.getUTCDate() + 1)
  }
  return dates
}

export function HolidaysPanel() {
  const [items, setItems] = useState<Holiday[] | null>(null)
  const [drafts, setDrafts] = useState<Record<number, { date: string; name: string }>>({})
  const [entry, setEntry] = useState({ start: '', end: '', name: '' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const ranges = useMemo(() => groupConsecutiveHolidays(items ?? []), [items])

  async function load() {
    try {
      const next = await trackerApi<Holiday[]>('/api/holidays')
      setItems(next)
      setDrafts(Object.fromEntries(next.map((item) => [item.id, { date: item.date, name: item.name }])))
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  useEffect(() => { void load() }, [])

  async function add(event: FormEvent) {
    event.preventDefault()
    const dates = enumerateDates(entry.start, entry.end)
    if (!dates.length || !entry.name.trim()) {
      setError('Choose a valid start/end date and holiday name.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const existing = new Set((items ?? []).map((item) => item.date))
      for (const date of dates.filter((value) => !existing.has(value))) {
        await trackerApi<Holiday>('/api/holidays', {
          method: 'POST',
          body: JSON.stringify({ date, name: entry.name.trim() }),
        })
      }
      setEntry({ start: '', end: '', name: '' })
      setMessage(`${dates.length} holiday date${dates.length === 1 ? '' : 's'} processed.`)
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusy(false)
    }
  }

  async function update(item: Holiday) {
    const draft = drafts[item.id]
    if (!draft?.date || !draft.name.trim()) return
    setBusy(true)
    try {
      await trackerApi<Holiday>(`/api/holidays/${item.id}`, {
        method: 'PUT',
        body: JSON.stringify({ date: draft.date, name: draft.name.trim() }),
      })
      setMessage('Holiday updated.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusy(false)
    }
  }

  async function remove(item: Holiday) {
    if (!window.confirm(`Delete “${item.name}” on ${item.date}?`)) return
    setBusy(true)
    try {
      await trackerApi<void>(`/api/holidays/${item.id}`, { method: 'DELETE' })
      setMessage('Holiday deleted.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="holidays-heading">
      <header className="admin-surface-head"><div><span className="kicker">Schedule exceptions</span><h2 id="holidays-heading">Company holidays</h2><p>Add a single date or a consecutive shutdown range.</p></div><CalendarDays size={23} /></header>
      <Notice error={error} message={message} />
      <form className="admin-holiday-form" onSubmit={add}>
        <label><span>Start date</span><input type="date" required value={entry.start} onChange={(event) => setEntry({ ...entry, start: event.target.value })} /></label>
        <label><span>End date</span><input type="date" min={entry.start} value={entry.end} onChange={(event) => setEntry({ ...entry, end: event.target.value })} /></label>
        <label><span>Holiday name</span><input required value={entry.name} onChange={(event) => setEntry({ ...entry, name: event.target.value })} placeholder="Winter shutdown" /></label>
        <button className="solid-button" disabled={busy}><Plus size={15} /> Add date(s)</button>
      </form>
      {items === null ? <div className="admin-loading">Loading holidays…</div> : (
        <div className="admin-holiday-list">
          {ranges.map((range) => (
            <details className="admin-holiday-group" key={range.key}>
              <summary>
                <span className="admin-holiday-group-icon" aria-hidden="true"><CalendarDays size={17} /></span>
                <span className="admin-holiday-group-label">
                  <strong>{range.name}</strong>
                  <time dateTime={range.startDate}>{formatHolidayRange(range.startDate, range.endDate)}</time>
                </span>
                <span className="admin-holiday-group-count">{range.items.length} {range.items.length === 1 ? 'date' : 'consecutive dates'}</span>
                <ChevronDown size={17} aria-hidden="true" />
              </summary>
              <div className="admin-holiday-group-body">
                <p>Edit or remove individual dates in this holiday{range.items.length > 1 ? ' range' : ''}.</p>
                {range.items.map((item) => {
                  const draft = drafts[item.id] ?? { date: item.date, name: item.name }
                  return (
                    <div className="admin-record-row holiday" key={item.id}>
                      <CalendarDays size={17} />
                      <label><span className="sr-only">Date for {item.name}</span><input type="date" value={draft.date} onChange={(event) => setDrafts({ ...drafts, [item.id]: { ...draft, date: event.target.value } })} /></label>
                      <label><span className="sr-only">Holiday name for {item.date}</span><input value={draft.name} onChange={(event) => setDrafts({ ...drafts, [item.id]: { ...draft, name: event.target.value } })} /></label>
                      <button className="admin-icon-button" type="button" aria-label={`Save ${item.name} on ${item.date}`} disabled={busy || (draft.date === item.date && draft.name === item.name)} onClick={() => void update(item)}><Save size={15} /></button>
                      <button className="admin-icon-button danger" type="button" aria-label={`Delete ${item.name} on ${item.date}`} disabled={busy} onClick={() => void remove(item)}><X size={15} /></button>
                    </div>
                  )
                })}
              </div>
            </details>
          ))}
          {!items.length && <p className="admin-empty">No company holidays recorded.</p>}
        </div>
      )}
    </section>
  )
}
