import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { AlertTriangle, CheckCircle2, Plus, Save, Search, ShieldCheck, UsersRound } from 'lucide-react'
import { engineeringPermissionKeys, hasEngineeringPermission } from './permissions'

interface AccessUser {
  id: number
  accountName: string
  displayName: string
  isActive: boolean
  lastSeenAt: string
  groupIds: number[]
}

interface AccessGroup {
  id: number
  name: string
  description: string | null
  isSystemGroup: boolean
  permissions: string[]
  userCount: number
}

interface PermissionDefinition {
  key: string
  label: string
  description: string
  category: string
}

interface AccessOverview {
  users: AccessUser[]
  groups: AccessGroup[]
  permissions: PermissionDefinition[]
}

interface GroupDraft {
  name: string
  description: string
  permissions: string[]
}

async function accessApi<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    credentials: 'include',
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(body?.message ?? `Engineering access responded ${response.status}.`)
  }
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T
  return response.json() as Promise<T>
}

const sorted = (values: string[]) => [...values].sort((left, right) => left.localeCompare(right))
const sameNumbers = (left: number[], right: number[]) => [...left].sort((a, b) => a - b).join(',') === [...right].sort((a, b) => a - b).join(',')
const sameStrings = (left: string[], right: string[]) => sorted(left).join('\u001f') === sorted(right).join('\u001f')

export default function EngineeringAccessSettings({ permissions, onAccessChanged }: { permissions: string[]; onAccessChanged?: () => void }) {
  const canManageUsers = hasEngineeringPermission(permissions, engineeringPermissionKeys.settingsManageUsers)
  const canManageGroups = hasEngineeringPermission(permissions, engineeringPermissionKeys.settingsManageGroups)
  const [overview, setOverview] = useState<AccessOverview | null>(null)
  const [userDrafts, setUserDrafts] = useState<Record<number, number[]>>({})
  const [groupDrafts, setGroupDrafts] = useState<Record<number, GroupDraft>>({})
  const [selectedGroupId, setSelectedGroupId] = useState<number | null>(null)
  const [userSearch, setUserSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [showCreateGroup, setShowCreateGroup] = useState(false)

  async function load() {
    setLoading(true)
    setError(null)
    try {
      const next = await accessApi<AccessOverview>('/api/engineering-access')
      setOverview(next)
      setUserDrafts(Object.fromEntries(next.users.map(user => [user.id, [...user.groupIds]])))
      setGroupDrafts(Object.fromEntries(next.groups.map(group => [group.id, {
        name: group.name,
        description: group.description ?? '',
        permissions: [...group.permissions],
      }])))
      setSelectedGroupId(current => next.groups.some(group => group.id === current) ? current : next.groups[0]?.id ?? null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to load Engineering access settings.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  const dirtyUserIds = overview?.users
    .filter(user => !sameNumbers(user.groupIds, userDrafts[user.id] ?? []))
    .map(user => user.id) ?? []
  const dirtyGroupIds = overview?.groups
    .filter(group => {
      const draft = groupDrafts[group.id]
      return draft && (
        draft.name.trim() !== group.name ||
        draft.description.trim() !== (group.description ?? '') ||
        !sameStrings(group.permissions, draft.permissions))
    })
    .map(group => group.id) ?? []
  const dirtyCount = dirtyUserIds.length + dirtyGroupIds.length
  const selectedGroup = overview?.groups.find(group => group.id === selectedGroupId) ?? null
  const selectedDraft = selectedGroup ? groupDrafts[selectedGroup.id] : null
  const categories = overview ? [...new Set(overview.permissions.map(permission => permission.category))] : []
  const normalizedSearch = userSearch.trim().toLocaleLowerCase()
  const visibleUsers = overview?.users.filter(user =>
    !normalizedSearch || `${user.displayName} ${user.accountName}`.toLocaleLowerCase().includes(normalizedSearch)) ?? []

  function toggleUserGroup(userId: number, groupId: number) {
    if (!canManageUsers) return
    setUserDrafts(current => {
      const memberships = current[userId] ?? []
      return { ...current, [userId]: memberships.includes(groupId) ? memberships.filter(id => id !== groupId) : [...memberships, groupId] }
    })
    setMessage(null)
  }

  function togglePermission(permission: string) {
    if (!canManageGroups || !selectedGroup) return
    setGroupDrafts(current => {
      const draft = current[selectedGroup.id]
      if (!draft) return current
      return {
        ...current,
        [selectedGroup.id]: {
          ...draft,
          permissions: draft.permissions.includes(permission)
            ? draft.permissions.filter(key => key !== permission)
            : [...draft.permissions, permission],
        },
      }
    })
    setMessage(null)
  }

  async function saveAll() {
    if (!overview || !dirtyCount) return
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      for (const userId of dirtyUserIds) {
        await accessApi(`/api/engineering-access/users/${userId}/groups`, {
          method: 'PUT',
          body: JSON.stringify({ groupIds: userDrafts[userId] ?? [] }),
        })
      }
      for (const groupId of dirtyGroupIds) {
        const draft = groupDrafts[groupId]
        await accessApi(`/api/engineering-access/groups/${groupId}`, {
          method: 'PUT',
          body: JSON.stringify(draft),
        })
      }
      setMessage('Engineering access changes saved. Users receive updated permissions on their next request.')
      await load()
      onAccessChanged?.()
    } catch (cause) {
      setError(`${cause instanceof Error ? cause.message : 'Unable to save access settings.'} Unsaved choices remain on this page.`)
    } finally {
      setSaving(false)
    }
  }

  async function createGroup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canManageGroups) return
    const form = new FormData(event.currentTarget)
    setSaving(true)
    setError(null)
    try {
      await accessApi('/api/engineering-access/groups', {
        method: 'POST',
        body: JSON.stringify({
          name: String(form.get('name') ?? '').trim(),
          description: String(form.get('description') ?? '').trim() || null,
          permissions: [],
        }),
      })
      setShowCreateGroup(false)
      setMessage('Engineering group created. Select it to assign permissions.')
      await load()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to create the group.')
    } finally {
      setSaving(false)
    }
  }

  if (loading && !overview) return <section className="panel skeleton-panel"><div className="skeleton-line lg"/><div className="skeleton-line"/><div className="skeleton-line"/></section>

  return <div className="engineering-access-page">
    <div className="engineering-access-savebar">
      <div>
        <span className="eyebrow">Engineering access control</span>
        <strong>{dirtyCount ? `${dirtyCount} unsaved change${dirtyCount === 1 ? '' : 's'}` : 'All changes saved'}</strong>
      </div>
      <button className="button" type="button" disabled={!dirtyCount || saving} onClick={() => void saveAll()}>
        <Save size={15}/>{saving ? 'Saving changes...' : 'Save all changes'}
      </button>
    </div>

    {error && <div className="inline-alert" role="alert"><AlertTriangle size={16}/>{error}</div>}
    {message && <div className="engineering-access-success" role="status"><CheckCircle2 size={16}/>{message}</div>}

    <section className="engineering-access-intro panel">
      <div><ShieldCheck size={24}/><span><strong>Group-based permissions</strong><p>Users are registered centrally, then assigned to Engineering-specific groups here. Permissions from every assigned Engineering group are combined.</p></span></div>
      <small>Project Tracker group memberships are not changed from this page.</small>
    </section>

    <div className="engineering-access-grid">
      <section className="panel engineering-access-users">
        <header><div><span className="eyebrow">Registered accounts</span><h2>Users and groups</h2></div><span>{overview?.users.length ?? 0} users</span></header>
        <label className="topbar-search"><Search size={14}/><input value={userSearch} onChange={event => setUserSearch(event.target.value)} placeholder="Search registered users"/></label>
        <div className="engineering-user-list">
          {visibleUsers.map(user => <article key={user.id} className={!user.isActive ? 'is-inactive' : undefined}>
            <div className="engineering-user-identity"><span><UsersRound size={16}/></span><div><strong>{user.displayName}</strong><small>{user.accountName}</small></div>{!user.isActive && <em>Inactive</em>}</div>
            <div className="engineering-user-groups" aria-label={`Engineering groups for ${user.displayName}`}>
              {overview?.groups.map(group => <label key={group.id}>
                <input type="checkbox" checked={(userDrafts[user.id] ?? []).includes(group.id)} disabled={!canManageUsers || saving || !user.isActive} onChange={() => toggleUserGroup(user.id, group.id)}/>
                <span>{group.name}</span>
              </label>)}
            </div>
          </article>)}
        </div>
      </section>

      <section className="panel engineering-access-groups">
        <header><div><span className="eyebrow">Permission profiles</span><h2>Engineering groups</h2></div>{canManageGroups && <button className="button ghost" type="button" onClick={() => setShowCreateGroup(current => !current)}><Plus size={14}/> New group</button>}</header>
        {showCreateGroup && <form className="engineering-create-group" onSubmit={createGroup}>
          <label>Group name<input name="name" required autoFocus/></label>
          <label>Description<input name="description"/></label>
          <div><button className="button" disabled={saving}>Create group</button><button className="button ghost" type="button" onClick={() => setShowCreateGroup(false)}>Cancel</button></div>
        </form>}
        <div className="engineering-group-selector" role="tablist" aria-label="Engineering groups">
          {overview?.groups.map(group => <button key={group.id} type="button" role="tab" aria-selected={selectedGroupId === group.id} className={selectedGroupId === group.id ? 'is-active' : undefined} onClick={() => setSelectedGroupId(group.id)}><strong>{group.name}</strong><small>{group.userCount} user{group.userCount === 1 ? '' : 's'} · {group.permissions.length} permissions</small></button>)}
        </div>

        {selectedGroup && selectedDraft && <div className="engineering-group-editor">
          <div className="engineering-group-heading">
            <label>Group name<input value={selectedDraft.name} disabled={selectedGroup.isSystemGroup || !canManageGroups} onChange={event => setGroupDrafts(current => ({ ...current, [selectedGroup.id]: { ...selectedDraft, name: event.target.value } }))}/></label>
            <label>Description<input value={selectedDraft.description} disabled={selectedGroup.isSystemGroup || !canManageGroups} onChange={event => setGroupDrafts(current => ({ ...current, [selectedGroup.id]: { ...selectedDraft, description: event.target.value } }))}/></label>
          </div>
          {categories.map((category, categoryIndex) => <details className="engineering-permission-category" key={category} open={categoryIndex < 2}>
            <summary><span>{category}</span><b>{overview?.permissions.filter(permission => permission.category === category && selectedDraft.permissions.includes(permission.key)).length}/{overview?.permissions.filter(permission => permission.category === category).length}</b></summary>
            <div className="engineering-permission-options">
              {overview?.permissions.filter(permission => permission.category === category).map(permission => <label key={permission.key}>
                <input type="checkbox" checked={selectedDraft.permissions.includes(permission.key)} disabled={!canManageGroups || saving} onChange={() => togglePermission(permission.key)}/>
                <span><strong>{permission.label}</strong><small>{permission.description}</small></span>
              </label>)}
            </div>
          </details>)}
        </div>}
      </section>
    </div>
  </div>
}
