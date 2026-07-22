import '../App.css'
import { useState, useEffect, useMemo, useRef } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  ArchiveRestore,
  CalendarDays,
  CalendarPlus,
  CalendarRange,
  CheckCircle2,
  ChevronDown,
  Factory,
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
  AccessOverview,
  AccessGroup,
  PermissionDefinition,
  RegisteredUser,
} from '../types'
import {
  EmptyState,
} from '../components'
import { ArchivedProjectsPanel } from './archived-projects'

export type SettingsTab = 'calendar' | 'workCenters' | 'holidays' | 'roles' | 'archived'

function hasPermission(user: User | null, permission: string) {
  return Boolean(user?.permissions?.includes(permission))
}

export function SettingsView({
  scheduleSettings,
  holidays,
  workCenters,
  currentUser,
  onAccessChanged,
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
  currentUser: User | null
  onAccessChanged: () => Promise<User>
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
  const canManageCalendar = hasPermission(currentUser, 'settings.workCalendar.manage')
  const canManageWorkCenters = hasPermission(currentUser, 'settings.workCenters.manage')
  const canManageHolidays = hasPermission(currentUser, 'settings.holidays.manage')
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

  const canManageAccess = hasPermission(currentUser, 'access.manageUsers') || hasPermission(currentUser, 'access.manageGroups')
  const canRestoreArchived = hasPermission(currentUser, 'archived.restore')
  const visibleTabs: SettingsTab[] = [
    'calendar',
    'workCenters',
    'holidays',
    ...(canManageAccess ? ['roles' as const] : []),
    ...(canRestoreArchived ? ['archived' as const] : []),
  ]

  useEffect(() => {
    if (!visibleTabs.includes(tab)) {
      setTab(visibleTabs[0] ?? 'calendar')
    }
  }, [tab, visibleTabs])

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
        {canManageAccess && <button className={tab === 'roles' ? 'active' : ''} onClick={() => setTab('roles')}><Users size={16} /> Access</button>}
        {canRestoreArchived && <button className={tab === 'archived' ? 'active' : ''} onClick={() => setTab('archived')}><ArchiveRestore size={16} /> Archived Projects</button>}
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
            <button className="button primary" disabled={!canManageCalendar || !changed || draftDays.length === 0} onClick={() => setConfirming(true)}><Save size={15} /> Save Work Week</button>
          </div>
        </section>
      )}

      {tab === 'workCenters' && (
        <WorkCenterView workCenters={workCenters} canEdit={canManageWorkCenters} addWorkCenter={addWorkCenter} updateWorkCenter={updateWorkCenter} deleteWorkCenter={deleteWorkCenter} embedded />
      )}
      {tab === 'holidays' && (
        <HolidayView holidays={holidays} canEdit={canManageHolidays} addHolidayRange={addHolidayRange} updateHoliday={updateHoliday} deleteHoliday={deleteHoliday} embedded />
      )}
      {tab === 'roles' && canManageAccess && <AccessManagementPanel currentUser={currentUser} onAccessChanged={onAccessChanged} />}
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

export function AccessManagementPanel({ currentUser, onAccessChanged }: { currentUser: User | null; onAccessChanged: () => Promise<User> }) {
  const [overview, setOverview] = useState<AccessOverview | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [userSearch, setUserSearch] = useState('')
  const [newUser, setNewUser] = useState({ accountName: '', displayName: '', isActive: true, groupIds: [] as number[] })
  const [newGroup, setNewGroup] = useState({ name: '', description: '', isSystemGroup: false, permissions: [] as string[] })
  const [userDrafts, setUserDrafts] = useState<Record<number, { groupIds: number[]; isActive: boolean }>>({})
  const [groupDrafts, setGroupDrafts] = useState<Record<number, string[]>>({})
  const [savingAll, setSavingAll] = useState(false)

  async function loadOverview() {
    setLoading(true)
    setError(null)
    try {
      setOverview(await api<AccessOverview>('/api/admin/access'))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to load access management.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadOverview()
  }, [])

  useEffect(() => {
    if (!overview) return
    setUserDrafts(Object.fromEntries(
      overview.users.map((user) => [user.id, { groupIds: user.groupIds, isActive: user.isActive }]),
    ))
    setGroupDrafts(Object.fromEntries(
      overview.groups.map((group) => [group.id, group.permissions]),
    ))
  }, [overview])

  async function createUser(event: FormEvent) {
    event.preventDefault()
    if (!newUser.accountName.trim()) return
    setError(null)
    try {
      await api<RegisteredUser>('/api/admin/users', {
        method: 'POST',
        body: JSON.stringify({
          accountName: newUser.accountName.trim(),
          displayName: newUser.displayName.trim() || null,
          isActive: newUser.isActive,
          groupIds: newUser.groupIds,
        }),
      })
      setNewUser({ accountName: '', displayName: '', isActive: true, groupIds: [] })
      await loadOverview()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to register that user.')
    }
  }

  function updateUserDraft(userId: number, recipe: (draft: { groupIds: number[]; isActive: boolean }) => { groupIds: number[]; isActive: boolean }) {
    setUserDrafts((current) => {
      const existing = current[userId]
      if (!existing) return current
      return { ...current, [userId]: recipe(existing) }
    })
  }

  async function createGroup(event: FormEvent) {
    event.preventDefault()
    if (!newGroup.name.trim()) return
    setError(null)
    try {
      await api<AccessGroup>('/api/admin/groups', {
        method: 'POST',
        body: JSON.stringify({
          name: newGroup.name.trim(),
          description: newGroup.description.trim() || null,
          isSystemGroup: newGroup.isSystemGroup,
          permissions: newGroup.permissions,
        }),
      })
      setNewGroup({ name: '', description: '', isSystemGroup: false, permissions: [] })
      await loadOverview()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to create that group.')
    }
  }

  const groups = overview?.groups ?? []
  const permissions = overview?.permissions ?? []
  const filteredUsers = (overview?.users ?? []).filter((user) => {
    const query = userSearch.trim().toLowerCase()
    if (!query) return true
    return user.displayName.toLowerCase().includes(query) || user.accountName.toLowerCase().includes(query)
  })
  const permissionCategories = [...new Set(permissions.map((permission) => permission.category))]
  const dirtyUserIds = (overview?.users ?? [])
    .filter((user) => {
      const draft = userDrafts[user.id]
      if (!draft) return false
      return draft.isActive !== user.isActive
        || draft.groupIds.length !== user.groupIds.length
        || draft.groupIds.some((groupId, index) => groupId !== user.groupIds[index])
    })
    .map((user) => user.id)
  const dirtyGroupIds = (overview?.groups ?? [])
    .filter((group) => {
      const draft = groupDrafts[group.id]
      if (!draft) return false
      return draft.length !== group.permissions.length
        || draft.some((permission, index) => permission !== group.permissions[index])
    })
    .map((group) => group.id)
  const hasPendingChanges = dirtyUserIds.length > 0 || dirtyGroupIds.length > 0

  function updateGroupDraft(groupId: number, permissions: string[]) {
    setGroupDrafts((current) => ({ ...current, [groupId]: permissions }))
  }

  async function saveAllChanges() {
    if (!overview || !hasPendingChanges || savingAll) return
    setSavingAll(true)
    setError(null)
    try {
      for (const userId of dirtyUserIds) {
        const user = overview.users.find((candidate) => candidate.id === userId)
        const draft = user ? userDrafts[user.id] : null
        if (!user || !draft) continue
        await api<RegisteredUser>(`/api/admin/users/${user.id}`, {
          method: 'PUT',
          body: JSON.stringify({
            accountName: user.accountName,
            displayName: user.displayName,
            isActive: draft.isActive,
            groupIds: draft.groupIds,
          }),
        })
      }

      for (const groupId of dirtyGroupIds) {
        const group = overview.groups.find((candidate) => candidate.id === groupId)
        const draft = group ? groupDrafts[group.id] : null
        if (!group || !draft) continue
        await api<AccessGroup>(`/api/admin/groups/${group.id}`, {
          method: 'PUT',
          body: JSON.stringify({
            name: group.name,
            description: group.description,
            isSystemGroup: group.isSystemGroup,
            permissions: draft,
          }),
        })
      }

      await onAccessChanged().catch(() => currentUser as User | null)
      await loadOverview()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unable to save access changes.')
    } finally {
      setSavingAll(false)
    }
  }

  return (
    <section className="settings-tab-content access-tab-content">
      <div className="access-save-bar">
        <p className="field-hint">Changes stay local on this page until you apply them.</p>
        <button className="button primary" type="button" disabled={!hasPendingChanges || savingAll} onClick={() => void saveAllChanges()}>
          <Save size={15} /> {savingAll ? 'Saving Changes...' : `Save All Changes${hasPendingChanges ? ` (${dirtyUserIds.length + dirtyGroupIds.length})` : ''}`}
        </button>
      </div>
      <section className="panel role-management-panel">
        <header className="panel-head">
          <div className="panel-head-text">
            <span className="kicker">Access Control</span>
            <h2>Registered Users & Groups</h2>
            <p>Register Microsoft/Windows users before sign-in, assign them to groups, and control permissions per group.</p>
          </div>
          <label className="search-field role-search" aria-label="Search users">
            <Search size={15} />
            <input value={userSearch} onChange={(event) => setUserSearch(event.target.value)} placeholder="Search registered users" />
          </label>
        </header>

        {error && <p className="inline-note warning role-error"><AlertTriangle size={14} /> {error}</p>}

        {loading || !overview ? (
          <div className="role-board">
            <div className="skeleton-line" style={{ height: 120 }} />
            <div className="skeleton-line" style={{ height: 280 }} />
          </div>
        ) : (
          <div className="access-layout">
            <section className="access-users">
              <header className="section-head-row">
                <div>
                  <span className="section-label">Registered Users</span>
                  <p className="field-hint">Only registered users with an active toggle can open this module.</p>
                </div>
                <span className="ot-badge">{overview.users.length} total</span>
              </header>
              <form className="compact-form-grid" onSubmit={createUser}>
                <input value={newUser.accountName} onChange={(event) => setNewUser({ ...newUser, accountName: event.target.value })} placeholder="DOMAIN\\user.name" required />
                <input value={newUser.displayName} onChange={(event) => setNewUser({ ...newUser, displayName: event.target.value })} placeholder="Display name" />
                <button className="button primary" type="submit"><Plus size={15} /> Register User</button>
              </form>
              <div className="role-user-list access-user-list">
                {filteredUsers.map((user) => (
                  <UserAccessCard
                    key={user.id}
                    user={user}
                    currentUser={currentUser}
                    groups={groups}
                    draft={userDrafts[user.id] ?? { groupIds: user.groupIds, isActive: user.isActive }}
                    saving={savingAll}
                    onDraftChange={(recipe) => updateUserDraft(user.id, recipe)}
                  />
                ))}
              </div>
            </section>

            <section className="access-groups">
              <header className="section-head-row">
                <div>
                  <span className="section-label">Groups</span>
                  <p className="field-hint">Permissions stack through group membership. Create groups like Engineering, Manager, Sales, or custom teams.</p>
                </div>
                <ShieldCheck size={18} />
              </header>
              <form className="compact-form-grid" onSubmit={createGroup}>
                <input value={newGroup.name} onChange={(event) => setNewGroup({ ...newGroup, name: event.target.value })} placeholder="Group name" required />
                <input value={newGroup.description} onChange={(event) => setNewGroup({ ...newGroup, description: event.target.value })} placeholder="Description" />
                <button className="button primary" type="submit"><Plus size={15} /> Create Group</button>
              </form>
              <div className="access-group-list">
                {groups.map((group) => (
                  <AccessGroupCard
                    key={group.id}
                    group={group}
                    permissions={permissions}
                    categories={permissionCategories}
                    draft={groupDrafts[group.id] ?? group.permissions}
                    saving={savingAll}
                    onDraftChange={(permissions) => updateGroupDraft(group.id, permissions)}
                  />
                ))}
              </div>
            </section>
          </div>
        )}
      </section>
    </section>
  )
}

function UserAccessCard({
  user,
  currentUser,
  groups,
  draft,
  saving,
  onDraftChange,
}: {
  user: RegisteredUser
  currentUser: User | null
  groups: AccessGroup[]
  draft: { groupIds: number[]; isActive: boolean }
  saving: boolean
  onDraftChange: (recipe: (draft: { groupIds: number[]; isActive: boolean }) => { groupIds: number[]; isActive: boolean }) => void
}) {
  function toggleGroup(groupId: number, checked: boolean) {
    onDraftChange((current) => ({
      ...current,
      groupIds: (checked ? [...current.groupIds, groupId] : current.groupIds.filter((value) => value !== groupId))
        .filter((value, index, values) => values.indexOf(value) === index)
        .sort((a, b) => a - b),
    }))
  }

  return (
    <article className="role-user-card access-user-card">
      <span className="role-avatar">{userInitials(user.displayName)}</span>
      <div className="role-user-copy">
        <strong>{user.displayName} {currentUser?.accountName.toLowerCase() === user.accountName.toLowerCase() && <small>You</small>}</strong>
        <span>{user.accountName}</span>
        <time dateTime={user.lastSeenAt}>{formatLastSeen(user.lastSeenAt)}</time>
      </div>
      <div className="field user-group-checklist">
        <span>Groups</span>
        <div className="group-checkbox-list">
          {groups.map((group) => (
            <label key={group.id} className="permission-row group-checkbox-row">
              <input
                type="checkbox"
                checked={draft.groupIds.includes(group.id)}
                onChange={(event) => toggleGroup(group.id, event.target.checked)}
                disabled={saving}
              />
              <span>
                <strong>{group.name}</strong>
                <small>{group.description || `${group.userCount} assigned users`}</small>
              </span>
            </label>
          ))}
        </div>
      </div>
      <div className="access-user-actions">
        <label className="field checkbox-row">
          <input
            type="checkbox"
            checked={draft.isActive}
            onChange={(event) => onDraftChange((current) => ({ ...current, isActive: event.target.checked }))}
            disabled={saving}
          />
          <span>Active</span>
        </label>
      </div>
    </article>
  )
}

function AccessGroupCard({
  group,
  permissions,
  categories,
  draft,
  saving,
  onDraftChange,
}: {
  group: AccessGroup
  permissions: PermissionDefinition[]
  categories: string[]
  draft: string[]
  saving: boolean
  onDraftChange: (permissions: string[]) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const panelId = `access-group-permissions-${group.id}`

  const togglePermission = (key: string) => {
    const next = draft.includes(key) ? draft.filter((permission) => permission !== key) : [...draft, key]
    onDraftChange(next.sort((a, b) => a.localeCompare(b)))
  }

  return (
    <article className={`panel access-group-card ${expanded ? 'is-expanded' : ''}`}>
      <button
        className="access-group-toggle"
        type="button"
        aria-expanded={expanded}
        aria-controls={panelId}
        onClick={() => setExpanded((current) => !current)}
      >
        <span className="access-group-icon" aria-hidden="true"><ShieldCheck size={17} /></span>
        <span className="access-group-summary">
          <span className="access-group-title-line">
            <strong>{group.name}</strong>
            <small>{group.isSystemGroup ? 'System role' : 'Custom role'}</small>
          </span>
          <span>{group.description || 'No description provided.'}</span>
        </span>
        <span className="access-group-stats">
          <span><strong>{group.userCount}</strong> {group.userCount === 1 ? 'user' : 'users'}</span>
          <span><strong>{draft.length}</strong> of {permissions.length} permissions</span>
        </span>
        <ChevronDown className="access-group-chevron" size={18} aria-hidden="true" />
      </button>
      {expanded && (
        <div className="access-group-body" id={panelId}>
          <div className="permission-grid">
            {categories.map((category) => {
              const categoryPermissions = permissions.filter((permission) => permission.category === category)
              const selectedCount = categoryPermissions.filter((permission) => draft.includes(permission.key)).length
              return (
                <section key={category} className="permission-category">
                  <header className="permission-category-head">
                    <span className="section-label">{category}</span>
                    <span>{selectedCount}/{categoryPermissions.length} enabled</span>
                  </header>
                  <div className="permission-category-list">
                    {categoryPermissions.map((permission) => (
                      <label key={permission.key} className="permission-row">
                        <input
                          type="checkbox"
                          checked={draft.includes(permission.key)}
                          onChange={() => togglePermission(permission.key)}
                          disabled={saving}
                        />
                        <span>
                          <strong>{permission.label}</strong>
                          <small>{permission.description}</small>
                        </span>
                      </label>
                    ))}
                  </div>
                </section>
              )
            })}
          </div>
        </div>
      )}
    </article>
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
        {!isAdmin && <p className="inline-note warning"><AlertTriangle size={14} /> Import permission required to run workbook uploads.</p>}
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
