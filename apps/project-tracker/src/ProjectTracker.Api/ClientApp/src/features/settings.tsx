import '../App.css'
import { useState, useEffect, useMemo, useRef } from 'react'
import type { ReactNode, FormEvent, DragEvent } from 'react'
import {
  AlertTriangle,
  ArchiveRestore,
  CalendarDays,
  CalendarPlus,
  CalendarRange,
  CheckCircle2,
  Eye,
  Factory,
  GripVertical,
  Pencil,
  Plus,
  Save,
  Search,
  ShieldCheck,
  Trash2,
  UploadCloud,
  X,
  Users,
} from 'lucide-react'
import {
  api,
  isWorkday,
  userInitials,
  formatLastSeen,
  compactDate,
  dateToMs,
} from '../lib'
import type {
  DayOfWeekName,
  User,
  ProjectTask,
  TaskOvertimeDay,
  Holiday,
  WorkCenter,
  ScheduleSettings,
  ApplicationRole,
  AdminUser,
} from '../types'
import {
  EmptyState,
} from '../components'
import { ArchivedProjectsPanel } from './archived-projects'

export type SettingsTab = 'calendar' | 'workCenters' | 'holidays' | 'roles' | 'archived'

export function SettingsView({
  scheduleSettings,
  holidays,
  workCenters,
  canEdit,
  currentUser,
  updateWorkCalendar,
  addWorkCenter,
  updateWorkCenter,
  deleteWorkCenter,
  addHolidayRange,
  updateHoliday,
  deleteHoliday,
}: {
  scheduleSettings: ScheduleSettings
  holidays: Holiday[]
  workCenters: WorkCenter[]
  canEdit: boolean
  currentUser: User | null
  updateWorkCalendar: (days: DayOfWeekName[]) => Promise<void>
  addWorkCenter: (name: string) => Promise<void>
  updateWorkCenter: (id: number, name: string) => Promise<void>
  deleteWorkCenter: (id: number) => Promise<void>
  addHolidayRange: (startDate: string, endDate: string, name: string) => Promise<void>
  updateHoliday: (id: number, date: string, name: string) => Promise<void>
  deleteHoliday: (id: number) => Promise<void>
}) {
  const [tab, setTab] = useState<SettingsTab>('calendar')
  const [draftDays, setDraftDays] = useState<DayOfWeekName[]>(scheduleSettings.workingDays)
  const [confirming, setConfirming] = useState(false)
  const [saving, setSaving] = useState(false)
  const dayOptions: { value: DayOfWeekName; short: string; label: string }[] = [
    { value: 'Monday', short: 'Mon', label: 'Monday' },
    { value: 'Tuesday', short: 'Tue', label: 'Tuesday' },
    { value: 'Wednesday', short: 'Wed', label: 'Wednesday' },
    { value: 'Thursday', short: 'Thu', label: 'Thursday' },
    { value: 'Friday', short: 'Fri', label: 'Friday' },
    { value: 'Saturday', short: 'Sat', label: 'Saturday' },
    { value: 'Sunday', short: 'Sun', label: 'Sunday' },
  ]

  useEffect(() => setDraftDays(scheduleSettings.workingDays), [scheduleSettings.workingDays])

  const changed = [...draftDays].sort().join('|') !== [...scheduleSettings.workingDays].sort().join('|')
  const toggleDay = (day: DayOfWeekName) => {
    setDraftDays((current) => current.includes(day) ? current.filter((item) => item !== day) : [...current, day])
  }
  const saveCalendar = async () => {
    if (draftDays.length === 0 || saving) return
    setSaving(true)
    try {
      await updateWorkCalendar(draftDays)
      setConfirming(false)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="view settings-view">
      <nav className="settings-tabs" aria-label="Settings sections">
        <button className={tab === 'calendar' ? 'active' : ''} onClick={() => setTab('calendar')}><CalendarRange size={16} /> Work Calendar</button>
        <button className={tab === 'workCenters' ? 'active' : ''} onClick={() => setTab('workCenters')}><Factory size={16} /> Work Centers</button>
        <button className={tab === 'holidays' ? 'active' : ''} onClick={() => setTab('holidays')}><CalendarDays size={16} /> Holidays</button>
        <button className={tab === 'roles' ? 'active' : ''} onClick={() => setTab('roles')}><Users size={16} /> User Roles</button>
        <button className={tab === 'archived' ? 'active' : ''} onClick={() => setTab('archived')}><ArchiveRestore size={16} /> Archived Projects</button>
      </nav>

      {tab === 'calendar' && (
        <section className="panel work-calendar-panel">
          <header className="panel-head">
            <div className="panel-head-text">
              <span className="kicker">Company Schedule</span>
              <h2>Standard Work Week</h2>
              <p>These days drive active project dates, Gantt projections, progress, and capacity calculations.</p>
            </div>
            <span className="schedule-count">{draftDays.length} days / week</span>
          </header>
          <div className="weekday-selector">
            {dayOptions.map((day) => (
              <button
                type="button"
                key={day.value}
                className={draftDays.includes(day.value) ? 'selected' : ''}
                onClick={() => toggleDay(day.value)}
                aria-pressed={draftDays.includes(day.value)}
              >
                <span>{day.short}</span>
                <small>{draftDays.includes(day.value) ? 'Working' : 'Off'}</small>
              </button>
            ))}
          </div>
          <div className="settings-save-row">
            <p><CalendarRange size={15} /> Completed projects remain unchanged. Active schedules are recalculated after confirmation.</p>
            <button className="button primary" disabled={!canEdit || !changed || draftDays.length === 0} onClick={() => setConfirming(true)}><Save size={15} /> Save Work Week</button>
          </div>
        </section>
      )}

      {tab === 'workCenters' && (
        <WorkCenterView workCenters={workCenters} canEdit={canEdit} addWorkCenter={addWorkCenter} updateWorkCenter={updateWorkCenter} deleteWorkCenter={deleteWorkCenter} embedded />
      )}
      {tab === 'holidays' && (
        <HolidayView holidays={holidays} canEdit={canEdit} addHolidayRange={addHolidayRange} updateHoliday={updateHoliday} deleteHoliday={deleteHoliday} embedded />
      )}
      {tab === 'roles' && <UserRolesPanel currentUser={currentUser} />}
      {tab === 'archived' && <ArchivedProjectsPanel />}

      {confirming && (
        <div className="modal-backdrop" onClick={() => !saving && setConfirming(false)}>
          <section className="modal confirmation-modal" role="alertdialog" aria-modal="true" aria-labelledby="calendar-confirm-title" onClick={(event) => event.stopPropagation()}>
            <div className="confirmation-icon complete"><CalendarRange size={22} /></div>
            <div className="confirmation-copy">
              <span className="kicker">Schedule Recalculation</span>
              <h2 id="calendar-confirm-title">Apply this company work week?</h2>
              <p>All active project schedules, target dates, progress, Gantt projections, and work-center conflicts will be recalculated. Completed projects will not change.</p>
            </div>
            <div className="modal-actions confirmation-actions">
              <button className="button ghost" onClick={() => setConfirming(false)} disabled={saving}>Cancel</button>
              <button className="button complete-solid" onClick={saveCalendar} disabled={saving}>{saving ? 'Recalculating...' : 'Apply & Recalculate'}</button>
            </div>
          </section>
        </div>
      )}
    </section>
  )
}

export const roleDefinitions: { role: ApplicationRole; label: string; description: string; icon: ReactNode }[] = [
  { role: 'Admin', label: 'Admin', description: 'All pages, settings, imports, and editing.', icon: <ShieldCheck size={16} /> },
  { role: 'Editor', label: 'Edit', description: 'Project pages with full project editing.', icon: <Pencil size={15} /> },
  { role: 'Viewer', label: 'View Only', description: 'Project pages without edit controls.', icon: <Eye size={16} /> },
]

export function UserRolesPanel({ currentUser }: { currentUser: User | null }) {
  const [users, setUsers] = useState<AdminUser[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [draggedUserId, setDraggedUserId] = useState<number | null>(null)
  const [dragOverRole, setDragOverRole] = useState<ApplicationRole | null>(null)
  const [movingUserId, setMovingUserId] = useState<number | null>(null)

  async function loadUsers() {
    setLoading(true)
    setError(null)
    try {
      setUsers(await api<AdminUser[]>('/api/admin/users'))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load user roles.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadUsers()
  }, [])

  async function moveUser(userId: number, role: ApplicationRole) {
    const existing = users.find((user) => user.id === userId)
    if (!existing || existing.role === role || movingUserId !== null) return

    setMovingUserId(userId)
    setError(null)
    try {
      const updated = await api<AdminUser>(`/api/admin/users/${userId}/role`, {
        method: 'PUT',
        body: JSON.stringify({ role }),
      })
      setUsers((current) => current.map((user) => user.id === userId ? updated : user))
      if (currentUser?.accountName.toLowerCase() === updated.accountName.toLowerCase()) {
        window.location.reload()
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to update this user role.')
    } finally {
      setMovingUserId(null)
      setDraggedUserId(null)
      setDragOverRole(null)
    }
  }

  function startDrag(event: DragEvent<HTMLElement>, userId: number) {
    setDraggedUserId(userId)
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', String(userId))
  }

  function dropOnRole(event: DragEvent<HTMLElement>, role: ApplicationRole) {
    event.preventDefault()
    const transferredId = Number(event.dataTransfer.getData('text/plain'))
    const userId = draggedUserId ?? (Number.isFinite(transferredId) ? transferredId : null)
    setDragOverRole(null)
    if (userId !== null) void moveUser(userId, role)
  }

  const query = search.trim().toLowerCase()
  const visibleUsers = query
    ? users.filter((user) => user.displayName.toLowerCase().includes(query) || user.accountName.toLowerCase().includes(query))
    : users

  return (
    <section className="settings-tab-content">
      <section className="panel role-management-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Access Control</span>
            <h2>User Roles</h2>
            <p>Drag a Windows account between roles or use the selector on its card.</p>
          </div>
          <label className="search-field role-search" aria-label="Search users">
            <Search size={15} />
            <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search name or Windows account" />
          </label>
        </header>

        {error && <p className="inline-note warning role-error"><AlertTriangle size={14} /> {error}</p>}

        {loading ? (
          <div className="role-board">
            {roleDefinitions.map((definition) => (
              <div className="role-lane" key={definition.role}>
                <div className="skeleton-line" style={{ height: 52 }} />
                <div className="skeleton-line" style={{ height: 76 }} />
              </div>
            ))}
          </div>
        ) : (
          <div className="role-board">
            {roleDefinitions.map((definition) => {
              const roleUsers = visibleUsers.filter((user) => user.role === definition.role)
              return (
                <section
                  className={`role-lane ${dragOverRole === definition.role ? 'drag-over' : ''}`}
                  key={definition.role}
                  onDragEnter={() => setDragOverRole(definition.role)}
                  onDragOver={(event) => event.preventDefault()}
                  onDragLeave={(event) => {
                    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDragOverRole(null)
                  }}
                  onDrop={(event) => dropOnRole(event, definition.role)}
                >
                  <header className={`role-lane-head role-${definition.role.toLowerCase()}`}>
                    <span className="role-lane-icon">{definition.icon}</span>
                    <div>
                      <h3>{definition.label}</h3>
                      <p>{definition.description}</p>
                    </div>
                    <strong>{roleUsers.length}</strong>
                  </header>
                  <div className="role-user-list">
                    {roleUsers.length === 0 ? (
                      <div className="role-empty">Drop users here</div>
                    ) : roleUsers.map((roleUser) => (
                      <article
                        className={`role-user-card ${movingUserId === roleUser.id ? 'is-moving' : ''}`}
                        draggable={movingUserId === null}
                        onDragStart={(event) => startDrag(event, roleUser.id)}
                        onDragEnd={() => {
                          setDraggedUserId(null)
                          setDragOverRole(null)
                        }}
                        key={roleUser.id}
                      >
                        <span className="role-drag-handle" title="Drag to another role"><GripVertical size={15} /></span>
                        <span className="role-avatar">{userInitials(roleUser.displayName)}</span>
                        <div className="role-user-copy">
                          <strong>{roleUser.displayName} {currentUser?.accountName.toLowerCase() === roleUser.accountName.toLowerCase() && <small>You</small>}</strong>
                          <span>{roleUser.accountName}</span>
                          <time dateTime={roleUser.lastSeenAt}>{formatLastSeen(roleUser.lastSeenAt)}</time>
                        </div>
                        <select
                          value={roleUser.role}
                          onChange={(event) => void moveUser(roleUser.id, event.target.value as ApplicationRole)}
                          onMouseDown={(event) => event.stopPropagation()}
                          aria-label={`Role for ${roleUser.displayName}`}
                          disabled={movingUserId !== null}
                        >
                          {roleDefinitions.map((option) => <option value={option.role} key={option.role}>{option.label}</option>)}
                        </select>
                      </article>
                    ))}
                  </div>
                </section>
              )
            })}
          </div>
        )}

        {!loading && users.length === 0 && (
          <p className="inline-note"><Users size={14} /> Windows accounts appear here after their first sign-in or when initially configured on the server.</p>
        )}
      </section>
    </section>
  )
}

export function HolidayView({
  holidays,
  canEdit,
  addHolidayRange,
  updateHoliday,
  deleteHoliday,
  embedded = false,
}: {
  holidays: Holiday[]
  canEdit: boolean
  addHolidayRange: (startDate: string, endDate: string, name: string) => Promise<void>
  updateHoliday: (id: number, date: string, name: string) => Promise<void>
  deleteHoliday: (id: number) => Promise<void>
  embedded?: boolean
}) {
  const [dialog, setDialog] = useState<HolidayDialogState | null>(null)
  const [saving, setSaving] = useState(false)
  const groups = useMemo(() => {
    const map = new Map<string, Holiday[]>()
    for (const holiday of holidays) {
      const year = holiday.date.slice(0, 4)
      const list = map.get(year) ?? []
      list.push(holiday)
      map.set(year, list)
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [holidays])

  const openAdd = () => setDialog({ mode: 'add', startDate: '', endDate: '', name: '' })
  const openEdit = (holiday: Holiday) => setDialog({ mode: 'edit', id: holiday.id, startDate: holiday.date, endDate: holiday.date, name: holiday.name })

  const submitDialog = async (event: FormEvent) => {
    event.preventDefault()
    if (!dialog || !dialog.startDate || !dialog.name.trim() || saving) return
    setSaving(true)
    try {
      if (dialog.mode === 'edit' && dialog.id) {
        await updateHoliday(dialog.id, dialog.startDate, dialog.name)
      } else {
        await addHolidayRange(dialog.startDate, dialog.endDate || dialog.startDate, dialog.name)
      }
      setDialog(null)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className={embedded ? 'settings-tab-content' : 'view'}>
      <section className="panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Non-working Dates</span>
            <h2>Holiday Calendar</h2>
            <p>Dates excluded from operation schedule calculations.</p>
          </div>
          {canEdit && (
            <button className="button primary" type="button" onClick={openAdd}><Plus size={15} /> Add Holiday</button>
          )}
        </header>
        {holidays.length === 0 ? (
          <EmptyState title="No holidays recorded" body="Add the company holidays so the scheduler skips them." />
        ) : (
          groups.map(([year, list]) => (
            <div className="holiday-year" key={year}>
              <span className="kicker">{year}</span>
              <div className="holiday-grid">
                {list.map((holiday) => (
                  <div className="holiday-card" key={holiday.id}>
                    <div className="holiday-date">
                      <strong>{new Date(`${holiday.date}T00:00:00`).getDate()}</strong>
                      <span>{new Intl.DateTimeFormat(undefined, { month: 'short' }).format(new Date(`${holiday.date}T00:00:00`))}</span>
                    </div>
                    <div className="holiday-meta">
                      <strong>{holiday.name}</strong>
                      <span>{new Intl.DateTimeFormat(undefined, { weekday: 'long' }).format(new Date(`${holiday.date}T00:00:00`))}</span>
                    </div>
                    {canEdit && (
                      <div className="holiday-actions">
                        <button className="icon-button" onClick={() => openEdit(holiday)} aria-label={`Rename ${holiday.name}`} title="Rename">
                          <Pencil size={14} />
                        </button>
                        <button className="icon-button danger" onClick={() => deleteHoliday(holiday.id)} aria-label={`Delete ${holiday.name}`} title="Delete">
                          <Trash2 size={14} />
                        </button>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
          ))
        )}
      </section>

      {dialog && (
        <div className="modal-backdrop" onClick={() => setDialog(null)}>
          <form className="modal compact-modal" onSubmit={submitDialog} onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <div className="panel-head-text">
                <span className="kicker">Non-working Dates</span>
                <h2>{dialog.mode === 'edit' ? 'Rename Holiday' : 'Add Holiday Range'}</h2>
              </div>
              <button type="button" className="icon-button" onClick={() => setDialog(null)} aria-label="Close"><X size={16} /></button>
            </header>
            <div className="modal-body">
              <section className="form-section">
                <label className="field"><span>Holiday Name</span>
                  <input value={dialog.name} onChange={(event) => setDialog({ ...dialog, name: event.target.value })} placeholder="Company holiday" autoFocus required />
                </label>
                <div className="field-row">
                  <label className="field"><span>{dialog.mode === 'edit' ? 'Date' : 'Start Date'}</span>
                    <input type="date" value={dialog.startDate} onChange={(event) => setDialog({ ...dialog, startDate: event.target.value, endDate: dialog.mode === 'edit' ? event.target.value : dialog.endDate })} required />
                  </label>
                  {dialog.mode === 'add' && (
                    <label className="field"><span>End Date</span>
                      <input type="date" value={dialog.endDate} min={dialog.startDate || undefined} onChange={(event) => setDialog({ ...dialog, endDate: event.target.value })} />
                    </label>
                  )}
                </div>
                {dialog.mode === 'add' && <p className="field-hint">Leave end date blank for a single-day holiday. Every date in the range will be skipped by schedule calculations.</p>}
              </section>
            </div>
            <div className="modal-actions">
              <button className="button ghost" type="button" onClick={() => setDialog(null)}>Cancel</button>
              <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save'}</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

export type HolidayDialogState =
  | { mode: 'add'; startDate: string; endDate: string; name: string }
  | { mode: 'edit'; id: number; startDate: string; endDate: string; name: string }

export function ImportView({ isAdmin, message, onUpload }: { isAdmin: boolean; message: string; onUpload: (file: File) => Promise<void> }) {
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!file || busy) return
    setBusy(true)
    setError(null)
    try {
      await onUpload(file)
      setFile(null)
      if (inputRef.current) inputRef.current.value = ''
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="view">
      <section className="panel import-panel">
        <div className="import-icon"><UploadCloud size={22} /></div>
        <span className="kicker">Add Programs</span>
        <h2>Import a Workbook</h2>
        <p>Upload a <code>.xlsx</code> or <code>.xlsm</code> tracker workbook to <strong>add its programs</strong> to the tracker. Existing programs are kept — nothing is deleted or overwritten.</p>
        <form className="import-form" onSubmit={submit}>
          <input
            ref={inputRef}
            type="file"
            className="file-input"
            accept=".xlsx,.xlsm"
            disabled={!isAdmin || busy}
            onChange={(event) => { setFile(event.target.files?.[0] ?? null); setError(null) }}
          />
          <button className="button primary lg" type="submit" disabled={!isAdmin || !file || busy}>
            <UploadCloud size={16} /> {busy ? 'Importing…' : 'Import Workbook'}
          </button>
        </form>
        {!isAdmin && <p className="inline-note warning"><AlertTriangle size={14} /> Admin role required to run imports.</p>}
        {error && <p className="inline-note warning"><AlertTriangle size={14} /> {error}</p>}
        {message && <p className="inline-note success"><CheckCircle2 size={14} /> {message}</p>}
      </section>
    </section>
  )
}

export function WorkCenterView({
  workCenters,
  canEdit,
  addWorkCenter,
  updateWorkCenter,
  deleteWorkCenter,
  embedded = false,
}: {
  workCenters: WorkCenter[]
  canEdit: boolean
  addWorkCenter: (name: string) => Promise<void>
  updateWorkCenter: (id: number, name: string) => Promise<void>
  deleteWorkCenter: (id: number) => Promise<void>
  embedded?: boolean
}) {
  const [query, setQuery] = useState('')
  const [dialog, setDialog] = useState<WorkCenterDialogState | null>(null)
  const [saving, setSaving] = useState(false)
  const filtered = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return workCenters
    return workCenters.filter((workCenter) => workCenter.name.toLowerCase().includes(value))
  }, [query, workCenters])

  const openAdd = () => setDialog({ mode: 'add', name: '' })
  const openEdit = (workCenter: WorkCenter) => setDialog({ mode: 'edit', id: workCenter.id, name: workCenter.name })

  const submitDialog = async (event: FormEvent) => {
    event.preventDefault()
    if (!dialog || !dialog.name.trim() || saving) return
    setSaving(true)
    try {
      if (dialog.mode === 'edit') {
        await updateWorkCenter(dialog.id, dialog.name)
      } else {
        await addWorkCenter(dialog.name)
      }
      setDialog(null)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className={embedded ? 'settings-tab-content' : 'view'}>
      <section className="panel table-panel workcenter-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Company Routing</span>
            <h2>Work Centers / Machines</h2>
          </div>
          <div className="toolbar-inline">
            <label className="search-field">
              <Search size={15} />
              <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search work centers" />
            </label>
            {canEdit && <button className="button primary" type="button" onClick={openAdd}><Plus size={15} /> Add Work Center</button>}
          </div>
        </header>

        {workCenters.length === 0 ? (
          <EmptyState title="No work centers recorded" body="Add machines or work centers so operations can be assigned consistently." />
        ) : filtered.length === 0 ? (
          <EmptyState title="No matching work centers" body="Try another machine or work center name." />
        ) : (
          <div className="workcenter-list">
            {filtered.map((workCenter) => (
              <div className="workcenter-row" key={workCenter.id}>
                <Factory size={16} />
                <strong>{workCenter.name}</strong>
                {canEdit && (
                  <div className="workcenter-actions">
                    <button className="icon-button" onClick={() => openEdit(workCenter)} aria-label={`Rename ${workCenter.name}`} title="Rename">
                      <Pencil size={14} />
                    </button>
                    <button className="icon-button danger" onClick={() => deleteWorkCenter(workCenter.id)} aria-label={`Delete ${workCenter.name}`} title="Delete">
                      <Trash2 size={14} />
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </section>

      {dialog && (
        <div className="modal-backdrop" onClick={() => setDialog(null)}>
          <form className="modal compact-modal" onSubmit={submitDialog} onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <div className="panel-head-text">
                <span className="kicker">Company Routing</span>
                <h2>{dialog.mode === 'edit' ? 'Rename Work Center' : 'Add Work Center'}</h2>
              </div>
              <button type="button" className="icon-button" onClick={() => setDialog(null)} aria-label="Close"><X size={16} /></button>
            </header>
            <div className="modal-body">
              <section className="form-section">
                <label className="field"><span>Work Center Name</span>
                  <input value={dialog.name} onChange={(event) => setDialog({ ...dialog, name: event.target.value })} placeholder="CNC Mill" autoFocus required />
                </label>
              </section>
            </div>
            <div className="modal-actions">
              <button className="button ghost" type="button" onClick={() => setDialog(null)}>Cancel</button>
              <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save'}</button>
            </div>
          </form>
        </div>
      )}
    </section>
  )
}

export type WorkCenterDialogState =
  | { mode: 'add'; name: string }
  | { mode: 'edit'; id: number; name: string }

/* ---------------------------------------------------------------------- */
/* Calendar                                                               */
/* ---------------------------------------------------------------------- */



export function OvertimeDateEditor({
  days,
  holidaySet,
  workingDaySet,
  onChange,
}: {
  days: TaskOvertimeDay[]
  holidaySet: Set<string>
  workingDaySet: Set<number>
  onChange: (days: TaskOvertimeDay[]) => void
}) {
  const [date, setDate] = useState('')
  const [note, setNote] = useState('')
  const [message, setMessage] = useState<string | null>(null)

  const addDate = () => {
    if (!date) return
    if (days.some((day) => day.date === date)) {
      setMessage('That overtime date is already approved for this operation.')
      return
    }
    if (isWorkday(dateToMs(date), holidaySet, workingDaySet)) {
      setMessage('Choose a normally non-working day or company holiday.')
      return
    }
    onChange([...days, { id: -Date.now(), date, note: note.trim() || null }].sort((a, b) => a.date.localeCompare(b.date)))
    setDate('')
    setNote('')
    setMessage(null)
  }

  return (
    <div className="overtime-editor">
      <div className="section-head-row">
        <div>
          <span className="section-label">Approved Overtime</span>
          <p className="field-hint">Add exact non-working dates approved for this operation only.</p>
        </div>
        {days.length > 0 && <span className="ot-badge">OT +{days.length}</span>}
      </div>
      <div className="overtime-entry">
        <label className="field"><span>Date</span><input type="date" value={date} onChange={(event) => { setDate(event.target.value); setMessage(null) }} /></label>
        <label className="field"><span>Approval Note</span><input value={note} onChange={(event) => setNote(event.target.value)} placeholder="Optional reason or approval" /></label>
        <button className="button ghost" type="button" onClick={addDate} disabled={!date}><Plus size={14} /> Add Date</button>
      </div>
      {message && <p className="inline-note warning"><AlertTriangle size={14} /> {message}</p>}
      {days.length > 0 && (
        <div className="overtime-list">
          {days.map((day) => (
            <div className="overtime-day" key={`${day.id}-${day.date}`}>
              <CalendarPlus size={15} />
              <span><strong>{compactDate(day.date)}</strong><small>{day.note || (holidaySet.has(day.date) ? 'Holiday overtime' : 'Approved overtime')}</small></span>
              <button type="button" className="icon-button danger" onClick={() => onChange(days.filter((item) => item.date !== day.date))} aria-label={`Remove overtime ${day.date}`}><X size={13} /></button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export function OvertimeDialog({
  task,
  holidaySet,
  workingDaySet,
  onClose,
  onSave,
}: {
  task: ProjectTask
  holidaySet: Set<string>
  workingDaySet: Set<number>
  onClose: () => void
  onSave: (days: TaskOvertimeDay[]) => Promise<void>
}) {
  const [days, setDays] = useState(task.overtimeDays)
  const [saving, setSaving] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    try {
      await onSave(days)
    } finally {
      setSaving(false)
    }
  }
  return (
    <div className="modal-backdrop" onClick={() => !saving && onClose()}>
      <form className="modal compact-modal" onSubmit={submit} onClick={(event) => event.stopPropagation()}>
        <header className="modal-head">
          <div className="panel-head-text"><span className="kicker">Operation Schedule</span><h2>Approved Overtime</h2><p>{task.title}</p></div>
          <button type="button" className="icon-button" onClick={onClose} aria-label="Close"><X size={16} /></button>
        </header>
        <div className="modal-body"><OvertimeDateEditor days={days} holidaySet={holidaySet} workingDaySet={workingDaySet} onChange={setDays} /></div>
        <div className="modal-actions">
          <button className="button ghost" type="button" onClick={onClose} disabled={saving}>Cancel</button>
          <button className="button primary" type="submit" disabled={saving}><Save size={15} /> {saving ? 'Saving...' : 'Save Overtime'}</button>
        </div>
      </form>
    </div>
  )
}
