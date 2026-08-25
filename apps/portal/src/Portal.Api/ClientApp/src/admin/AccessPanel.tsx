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
import { GroupCreationWizard, GroupEditor } from './GroupManagement'
import type { NewAccessGroup } from './GroupManagement'
import type {
  AccessGroup,
  AccessOverview,
  RegisteredUser,
} from './types'

interface UserDraft {
  displayName: string
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

export default function AccessPanel({
  currentAccountName,
  canManageUsers,
  canManageGroups,
  context = 'Arda',
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
  const [showGroupWizard, setShowGroupWizard] = useState(false)
  const [creatingGroup, setCreatingGroup] = useState(false)
  const [deletingGroupId, setDeletingGroupId] = useState<number | null>(null)

  async function load(preserveDrafts = false) {
    setLoading(true)
    setError(null)
    try {
      const next = await trackerApi<AccessOverview>('/api/admin/access')
      setOverview(next)
      setUserDrafts((current) => Object.fromEntries(next.users.map((user) => [
        user.id,
        preserveDrafts && current[user.id]
          ? current[user.id]
          : {
              displayName: user.displayName,
              groupIds: [...user.groupIds].sort((a, b) => a - b),
              isActive: user.isActive,
            },
      ])))
      setGroupDrafts((current) => Object.fromEntries(next.groups.map((group) => [
        group.id,
        preserveDrafts && current[group.id]
          ? current[group.id]
          : [...group.permissions].sort((a, b) => a.localeCompare(b)),
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
      draft.displayName.trim() !== user.displayName
      || draft.isActive !== user.isActive
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
      await load(true)
    } catch (cause) {
      setError(toErrorMessage(cause))
    }
  }

  async function createGroup(group: NewAccessGroup) {
    if (!canManageGroups) return
    setCreatingGroup(true)
    setError(null)
    setMessage(null)
    try {
      await trackerApi<AccessGroup>('/api/admin/groups', {
        method: 'POST',
        body: JSON.stringify({
          name: group.name,
          description: group.description || null,
          isSystemGroup: false,
          permissions: group.permissions,
        }),
      })
      setShowGroupWizard(false)
      setMessage(`${group.name} was created with ${group.permissions.length} permissions. Assign people from the Registered users directory.`)
      await load(true)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setCreatingGroup(false)
    }
  }

  async function deleteGroup(group: AccessGroup) {
    if (!canManageGroups || deletingGroupId !== null) return false
    if (Object.values(userDrafts).some((draft) => draft.groupIds.includes(group.id))) {
      setError(`Remove ${group.name} from all pending user assignments and save those changes before deleting the group.`)
      return false
    }
    setDeletingGroupId(group.id)
    setError(null)
    setMessage(null)
    try {
      await trackerApi<void>(`/api/admin/groups/${group.id}`, { method: 'DELETE' })
      setMessage(`${group.name} was deleted.`)
      await load(true)
      return true
    } catch (cause) {
      setError(toErrorMessage(cause))
      return false
    } finally {
      setDeletingGroupId(null)
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
                <label><span>Display name</span><input value={newUser.displayName} maxLength={160} onChange={(event) => setNewUser({ ...newUser, displayName: event.target.value })} placeholder="Optional" /></label>
                <button className="solid-button" type="submit"><Plus size={15} /> Register</button>
              </form>
            ) : (
              <p className="admin-readonly-note">User management requires the Manage Registered Users permission.</p>
            )}
            <div className="admin-user-list">
              {filteredUsers.map((user) => {
                const draft = userDrafts[user.id] ?? {
                  displayName: user.displayName,
                  groupIds: user.groupIds,
                  isActive: user.isActive,
                }
                const displayName = draft.displayName.trim() || user.displayName
                const isCurrent = accountKey(currentAccountName) === accountKey(user.accountName)
                return (
                  <details className="admin-user-card" key={user.id}>
                    <summary>
                      <span className="admin-user-avatar" aria-hidden="true">{initials(displayName)}</span>
                      <div className="admin-user-identity">
                        <strong>{displayName} {isCurrent && <small>You</small>}</strong>
                        <span>{user.accountName}</span>
                        <time dateTime={user.lastSeenAt}>{draft.groupIds.length} {draft.groupIds.length === 1 ? 'group' : 'groups'} · {draft.isActive ? formatLastSeen(user.lastSeenAt) : 'Inactive account'}</time>
                      </div>
                      <ChevronDown size={17} aria-hidden="true" />
                    </summary>
                    <div className="admin-user-card-body">
                      <label className="admin-display-name-field">
                        <span>Application display name</span>
                        <input
                          type="text"
                          value={draft.displayName}
                          maxLength={160}
                          disabled={saving || !canManageUsers}
                          onChange={(event) => updateUser(user.id, (current) => ({
                            ...current,
                            displayName: event.target.value,
                          }))}
                        />
                        <small>This is how the user&apos;s name appears throughout Project Tracker.</small>
                      </label>
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
              showGroupWizard ? (
                <GroupCreationWizard
                  permissions={overview.permissions}
                  creating={creatingGroup}
                  onCreate={createGroup}
                  onCancel={() => setShowGroupWizard(false)}
                />
              ) : (
                <button className="solid-button admin-start-group-button" type="button" onClick={() => setShowGroupWizard(true)}>
                  <Plus size={15} aria-hidden="true" /> Add permission group
                </button>
              )
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
                  deleting={deletingGroupId === group.id}
                  hasPendingUserAssignments={Object.values(userDrafts).some((draft) => draft.groupIds.includes(group.id))}
                  onChange={(permissions) => setGroupDrafts((current) => ({
                    ...current,
                    [group.id]: permissions,
                  }))}
                  onDelete={() => deleteGroup(group)}
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
