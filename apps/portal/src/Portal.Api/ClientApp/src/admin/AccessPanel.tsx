import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  Plus,
  Save,
  Search,
  ShieldCheck,
  UserRound,
} from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type {
  AccessGroup,
  AccessOverview,
  PermissionDefinition,
  RegisteredUser,
} from './types'

interface UserDraft {
  groupIds: number[]
  isActive: boolean
}

function sameNumbers(left: number[], right: number[]) {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function sameStrings(left: string[], right: string[]) {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

function accountKey(value: string | null) {
  return value?.trim().replaceAll('/', '\\').toLowerCase() ?? ''
}

function initials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (!words.length) return '?'
  return `${words[0][0]}${words.length > 1 ? words.at(-1)?.[0] : words[0][1] ?? ''}`.toUpperCase()
}

function formatLastSeen(value: string) {
  const date = new Date(value)
  if (!Number.isFinite(date.getTime()) || date.getUTCFullYear() <= 1970) return 'Never signed in'
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  }).format(date)
}

function GroupEditor({
  group,
  permissions,
  draft,
  disabled,
  onChange,
}: {
  group: AccessGroup
  permissions: PermissionDefinition[]
  draft: string[]
  disabled: boolean
  onChange: (permissions: string[]) => void
}) {
  const modules = [...new Map(permissions.map((permission) => [permission.moduleKey, {
    key: permission.moduleKey,
    name: permission.moduleName,
  }])).values()]
  return (
    <details className="admin-group-card">
      <summary>
        <span className="admin-group-icon"><ShieldCheck size={17} aria-hidden="true" /></span>
        <span>
          <strong>{group.name}</strong>
          <small>{group.description || 'No description provided'}</small>
        </span>
        <span className="admin-group-counts">
          {group.userCount} {group.userCount === 1 ? 'user' : 'users'} · {draft.length} permissions
        </span>
        <ChevronDown size={17} aria-hidden="true" />
      </summary>
      <div className="admin-module-permission-list">
        {modules.map((module) => {
          const modulePermissions = permissions.filter((permission) => permission.moduleKey === module.key)
          const categories = [...new Set(modulePermissions.map((permission) => permission.category))]
          const isAdministratorsGroup = group.name.toLowerCase() === 'administrators'
          const isAvailable = (permission: PermissionDefinition) => permission.key !== 'import.manage' || isAdministratorsGroup
          const selectedCount = modulePermissions.filter((permission) => isAvailable(permission) && draft.includes(permission.key)).length
          return (
            <section className="admin-module-permission-card" key={module.key}>
              <header>
                <span>{module.name.slice(0, 2).toUpperCase()}</span>
                <div><strong>{module.name}</strong><small>{selectedCount} of {modulePermissions.length} permissions selected</small></div>
              </header>
              <div className="admin-permission-groups">
                {categories.map((category) => (
                  <fieldset key={category}>
                    <legend>{category}</legend>
                    {modulePermissions.filter((permission) => permission.category === category).map((permission) => (
                      <label className={`admin-check-row ${isAvailable(permission) ? '' : 'permission-restricted'}`.trim()} key={permission.key}>
                        <input
                          type="checkbox"
                          checked={isAvailable(permission) && draft.includes(permission.key)}
                          disabled={disabled || !isAvailable(permission)}
                          onChange={() => {
                            const next = draft.includes(permission.key)
                              ? draft.filter((key) => key !== permission.key)
                              : [...draft, permission.key]
                            onChange(next.sort((a, b) => a.localeCompare(b)))
                          }}
                        />
                        <span><strong>{permission.label}</strong><small>{permission.description}</small></span>
                      </label>
                    ))}
                  </fieldset>
                ))}
              </div>
            </section>
          )
        })}
      </div>
    </details>
  )
}

export default function AccessPanel({
  currentAccountName,
  canManageUsers,
  canManageGroups,
  context = 'SON-AERO',
}: {
  currentAccountName: string | null
  canManageUsers: boolean
  canManageGroups: boolean
  context?: string
}) {
  const [overview, setOverview] = useState<AccessOverview | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [userDrafts, setUserDrafts] = useState<Record<number, UserDraft>>({})
  const [groupDrafts, setGroupDrafts] = useState<Record<number, string[]>>({})
  const [newUser, setNewUser] = useState({ accountName: '', displayName: '' })
  const [newGroup, setNewGroup] = useState({ name: '', description: '' })

  async function load() {
    setLoading(true)
    setError(null)
    try {
      const next = await trackerApi<AccessOverview>('/api/admin/access')
      setOverview(next)
      setUserDrafts(Object.fromEntries(next.users.map((user) => [
        user.id,
        { groupIds: [...user.groupIds].sort((a, b) => a - b), isActive: user.isActive },
      ])))
      setGroupDrafts(Object.fromEntries(next.groups.map((group) => [
        group.id,
        [...group.permissions].sort((a, b) => a.localeCompare(b)),
      ])))
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
  }, [])

  const dirtyUserIds = useMemo(() => (overview?.users ?? []).filter((user) => {
    const draft = userDrafts[user.id]
    return draft && (
      draft.isActive !== user.isActive
      || !sameNumbers(draft.groupIds, [...user.groupIds].sort((a, b) => a - b))
    )
  }).map((user) => user.id), [overview, userDrafts])

  const dirtyGroupIds = useMemo(() => (overview?.groups ?? []).filter((group) => {
    const draft = groupDrafts[group.id]
    return draft && !sameStrings(
      draft,
      [...group.permissions].sort((a, b) => a.localeCompare(b)),
    )
  }).map((group) => group.id), [groupDrafts, overview])

  const pendingCount = dirtyUserIds.length + dirtyGroupIds.length
  const filteredUsers = (overview?.users ?? []).filter((user) => {
    const query = search.trim().toLowerCase()
    return !query
      || user.displayName.toLowerCase().includes(query)
      || user.accountName.toLowerCase().includes(query)
  })

  function updateUser(id: number, update: (draft: UserDraft) => UserDraft) {
    setUserDrafts((current) => {
      const draft = current[id]
      return draft ? { ...current, [id]: update(draft) } : current
    })
  }

  async function createUser(event: FormEvent) {
    event.preventDefault()
    if (!canManageUsers) return
    setError(null)
    setMessage(null)
    try {
      await trackerApi<RegisteredUser>('/api/admin/users', {
        method: 'POST',
        body: JSON.stringify({
          accountName: newUser.accountName.trim(),
          displayName: newUser.displayName.trim() || null,
          isActive: true,
          groupIds: [],
        }),
      })
      setNewUser({ accountName: '', displayName: '' })
      setMessage('User registered. Assign shared access groups below.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  async function createGroup(event: FormEvent) {
    event.preventDefault()
    if (!canManageGroups) return
    setError(null)
    setMessage(null)
    try {
      await trackerApi<AccessGroup>('/api/admin/groups', {
        method: 'POST',
        body: JSON.stringify({
          name: newGroup.name.trim(),
          description: newGroup.description.trim() || null,
          isSystemGroup: false,
          permissions: [],
        }),
      })
      setNewGroup({ name: '', description: '' })
      setMessage('Group created. Expand it to assign permissions.')
      await load()
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  async function saveAll() {
    if (!overview || !pendingCount || saving) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      for (const id of dirtyUserIds) {
        if (!canManageUsers) break
        const user = overview.users.find((candidate) => candidate.id === id)
        const draft = userDrafts[id]
        if (!user || !draft) continue
        await trackerApi<RegisteredUser>(`/api/admin/users/${id}`, {
          method: 'PUT',
          body: JSON.stringify({ ...user, ...draft }),
        })
      }
      for (const id of dirtyGroupIds) {
        if (!canManageGroups) break
        const group = overview.groups.find((candidate) => candidate.id === id)
        const permissions = groupDrafts[id]
        if (!group || !permissions) continue
        await trackerApi<AccessGroup>(`/api/admin/groups/${id}`, {
          method: 'PUT',
          body: JSON.stringify({ ...group, permissions }),
        })
      }
      setMessage('Access changes saved.')
      await load()
    } catch (cause) {
      setError(`${toErrorMessage(cause)} Unsaved changes remain on this page.`)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="admin-surface" aria-labelledby="access-heading" aria-busy={loading}>
      <header className="admin-surface-head">
        <div>
          <span className="kicker">Access control</span>
          <h2 id="access-heading">{context} users and groups</h2>
          <p>Register Windows accounts, assign shared groups, and control each group&apos;s permissions by module.</p>
        </div>
      </header>

      {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16} /> {error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16} /> {message}</p>}

      {loading || !overview ? (
        <div className="admin-loading" role="status">Loading access controls…</div>
      ) : (
        <div className="admin-access-grid">
          <details className="admin-user-directory">
            <summary>
              <span className="admin-directory-icon"><UserRound size={18} aria-hidden="true" /></span>
              <div>
                <h3 id="registered-users-heading">Registered users</h3>
                <p>Search accounts and expand one user to edit assignments.</p>
              </div>
              <span className="admin-directory-count">{overview.users.length} {overview.users.length === 1 ? 'user' : 'users'}</span>
              <ChevronDown size={18} aria-hidden="true" />
            </summary>
            <div className="admin-user-directory-body" aria-labelledby="registered-users-heading">
              <label className="admin-search admin-user-search">
                <Search size={16} aria-hidden="true" />
                <span className="sr-only">Search registered users</span>
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder="Search by name or Windows account"
                />
              </label>
              <p className="admin-directory-results">{filteredUsers.length} of {overview.users.length} users shown · assignments apply across modules</p>
            {canManageUsers ? (
              <form className="admin-create-form" onSubmit={createUser}>
                <label>
                  <span>Windows account</span>
                  <input required value={newUser.accountName} onChange={(event) => setNewUser({ ...newUser, accountName: event.target.value })} placeholder="SON4L\\firstname.lastname" aria-describedby="windows-account-help" />
                  <small id="windows-account-help">Paste the user&apos;s <code>whoami</code> result. Forward slash is also accepted.</small>
                </label>
                <label><span>Display name</span><input value={newUser.displayName} onChange={(event) => setNewUser({ ...newUser, displayName: event.target.value })} placeholder="Optional" /></label>
                <button className="solid-button" type="submit"><Plus size={15} /> Register</button>
              </form>
            ) : (
              <p className="admin-readonly-note">User management requires the Manage Registered Users permission.</p>
            )}
            <div className="admin-user-list">
              {filteredUsers.map((user) => {
                const draft = userDrafts[user.id] ?? { groupIds: user.groupIds, isActive: user.isActive }
                const isCurrent = accountKey(currentAccountName) === accountKey(user.accountName)
                return (
                  <details className="admin-user-card" key={user.id}>
                    <summary>
                      <span className="admin-user-avatar" aria-hidden="true">{initials(user.displayName)}</span>
                      <div className="admin-user-identity">
                        <strong>{user.displayName} {isCurrent && <small>You</small>}</strong>
                        <span>{user.accountName}</span>
                        <time dateTime={user.lastSeenAt}>{draft.groupIds.length} {draft.groupIds.length === 1 ? 'group' : 'groups'} · {draft.isActive ? formatLastSeen(user.lastSeenAt) : 'Inactive account'}</time>
                      </div>
                      <ChevronDown size={17} aria-hidden="true" />
                    </summary>
                    <div className="admin-user-card-body">
                      <fieldset>
                        <legend>Shared groups</legend>
                        {overview.groups.map((group) => (
                          <label className="admin-check-row compact" key={group.id}>
                            <input
                              type="checkbox"
                              checked={draft.groupIds.includes(group.id)}
                              disabled={saving || !canManageUsers}
                              onChange={(event) => updateUser(user.id, (current) => ({
                                ...current,
                                groupIds: (event.target.checked
                                  ? [...current.groupIds, group.id]
                                  : current.groupIds.filter((id) => id !== group.id))
                                  .filter((id, index, values) => values.indexOf(id) === index)
                                  .sort((a, b) => a - b),
                              }))}
                            />
                            <span><strong>{group.name}</strong><small>{group.description}</small></span>
                          </label>
                        ))}
                      </fieldset>
                      <label className="admin-active-toggle">
                        <input
                          type="checkbox"
                          checked={draft.isActive}
                          disabled={saving || !canManageUsers}
                          onChange={(event) => updateUser(user.id, (current) => ({
                            ...current,
                            isActive: event.target.checked,
                          }))}
                        />
                        <span>Account active</span>
                      </label>
                    </div>
                  </details>
                )
              })}
              {!filteredUsers.length && <p className="admin-empty">No registered users match that search.</p>}
            </div>
            </div>
          </details>

          <section aria-labelledby="permission-groups-heading">
            <div className="admin-section-title">
              <div><h3 id="permission-groups-heading">Shared permission groups</h3><p>Permissions stack across assigned groups and are organized by module.</p></div>
              <ShieldCheck size={19} aria-hidden="true" />
            </div>
            {canManageGroups ? (
              <form className="admin-create-form" onSubmit={createGroup}>
                <label><span>Group name</span><input required value={newGroup.name} onChange={(event) => setNewGroup({ ...newGroup, name: event.target.value })} /></label>
                <label><span>Description</span><input value={newGroup.description} onChange={(event) => setNewGroup({ ...newGroup, description: event.target.value })} /></label>
                <button className="solid-button" type="submit"><Plus size={15} /> Create</button>
              </form>
            ) : (
              <p className="admin-readonly-note">Permission editing requires the Manage Groups permission.</p>
            )}
            <div className="admin-group-list">
              {overview.groups.map((group) => (
                <GroupEditor
                  key={group.id}
                  group={group}
                  permissions={overview.permissions}
                  draft={groupDrafts[group.id] ?? group.permissions}
                  disabled={saving || !canManageGroups}
                  onChange={(permissions) => setGroupDrafts((current) => ({
                    ...current,
                    [group.id]: permissions,
                  }))}
                />
              ))}
            </div>
          </section>
        </div>
      )}

      <div className="admin-save-bar">
        <p aria-live="polite">{pendingCount ? `${pendingCount} pending change${pendingCount === 1 ? '' : 's'}` : 'All access changes saved'}</p>
        <button className="solid-button" type="button" disabled={!pendingCount || saving} onClick={() => void saveAll()}>
          <Save size={15} /> {saving ? 'Saving…' : 'Save access changes'}
        </button>
      </div>
    </section>
  )
}
