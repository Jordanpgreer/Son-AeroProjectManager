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
import { portalApi, toErrorMessage } from './api'
import type { AccessGroup, EngineeringAccessOverview, PermissionDefinition } from './types'

interface GroupDraft {
  name: string
  description: string
  permissions: string[]
}

const sortedNumbers = (values: number[]) => [...values].sort((left, right) => left - right)
const sortedStrings = (values: string[]) => [...values].sort((left, right) => left.localeCompare(right))
const sameNumbers = (left: number[], right: number[]) => sortedNumbers(left).join(',') === sortedNumbers(right).join(',')
const sameStrings = (left: string[], right: string[]) => sortedStrings(left).join('\u001f') === sortedStrings(right).join('\u001f')
const accountKey = (value: string | null) => value?.trim().replaceAll('/', '\\').toLowerCase() ?? ''

function initials(name: string) {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (!words.length) return '?'
  return `${words[0][0]}${words.length > 1 ? words.at(-1)?.[0] : words[0][1] ?? ''}`.toUpperCase()
}

function formatLastSeen(value: string) {
  const date = new Date(value)
  if (!Number.isFinite(date.getTime()) || date.getUTCFullYear() <= 1970) return 'Never signed in'
  return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' }).format(date)
}

function PermissionGroup({
  group,
  permissions,
  draft,
  disabled,
  onChange,
}: {
  group: AccessGroup
  permissions: PermissionDefinition[]
  draft: GroupDraft
  disabled: boolean
  onChange: (draft: GroupDraft) => void
}) {
  const categories = [...new Set(permissions.map(permission => permission.category))]
  return <details className="admin-group-card">
    <summary>
      <span className="admin-group-icon"><ShieldCheck size={17} aria-hidden="true"/></span>
      <span><strong>{draft.name}</strong><small>{draft.description || 'No description provided'}</small></span>
      <span className="admin-group-counts">{group.userCount} {group.userCount === 1 ? 'user' : 'users'} · {draft.permissions.length} permissions</span>
      <ChevronDown size={17} aria-hidden="true"/>
    </summary>
    <div className="admin-permission-groups">
      <div className="engineering-admin-group-fields">
        <label><span>Group name</span><input value={draft.name} disabled={disabled || group.isSystemGroup} onChange={event => onChange({ ...draft, name: event.target.value })}/></label>
        <label><span>Description</span><input value={draft.description} disabled={disabled || group.isSystemGroup} onChange={event => onChange({ ...draft, description: event.target.value })}/></label>
      </div>
      {categories.map(category => <fieldset key={category}>
        <legend>{category}</legend>
        {permissions.filter(permission => permission.category === category).map(permission => <label className="admin-check-row" key={permission.key}>
          <input
            type="checkbox"
            checked={draft.permissions.includes(permission.key)}
            disabled={disabled}
            onChange={() => onChange({
              ...draft,
              permissions: draft.permissions.includes(permission.key)
                ? draft.permissions.filter(key => key !== permission.key)
                : sortedStrings([...draft.permissions, permission.key]),
            })}
          />
          <span><strong>{permission.label}</strong><small>{permission.description}</small></span>
        </label>)}
      </fieldset>)}
    </div>
  </details>
}

export default function EngineeringAccessPanel({ currentAccountName }: { currentAccountName: string | null }) {
  const [overview, setOverview] = useState<EngineeringAccessOverview | null>(null)
  const [userDrafts, setUserDrafts] = useState<Record<number, number[]>>({})
  const [groupDrafts, setGroupDrafts] = useState<Record<number, GroupDraft>>({})
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [newGroup, setNewGroup] = useState({ name: '', description: '' })

  async function load() {
    setLoading(true)
    setError(null)
    try {
      const next = await portalApi<EngineeringAccessOverview>('/api/admin/engineering-access')
      setOverview(next)
      setUserDrafts(Object.fromEntries(next.users.map(user => [user.id, sortedNumbers(user.groupIds)])))
      setGroupDrafts(Object.fromEntries(next.groups.map(group => [group.id, {
        name: group.name,
        description: group.description ?? '',
        permissions: sortedStrings(group.permissions),
      }])))
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  const dirtyUserIds = useMemo(() => overview?.users
    .filter(user => !sameNumbers(user.groupIds, userDrafts[user.id] ?? []))
    .map(user => user.id) ?? [], [overview, userDrafts])
  const dirtyGroupIds = useMemo(() => overview?.groups
    .filter(group => {
      const draft = groupDrafts[group.id]
      return draft && (draft.name.trim() !== group.name
        || draft.description.trim() !== (group.description ?? '')
        || !sameStrings(draft.permissions, group.permissions))
    })
    .map(group => group.id) ?? [], [groupDrafts, overview])
  const pendingCount = dirtyUserIds.length + dirtyGroupIds.length
  const filteredUsers = overview?.users.filter(user => {
    const query = search.trim().toLowerCase()
    return !query || `${user.displayName} ${user.accountName}`.toLowerCase().includes(query)
  }) ?? []

  async function createGroup(event: FormEvent) {
    event.preventDefault()
    if (!overview?.canManageGroups) return
    setError(null)
    setMessage(null)
    try {
      await portalApi('/api/admin/engineering-access/groups', {
        method: 'POST',
        body: JSON.stringify({ name: newGroup.name.trim(), description: newGroup.description.trim() || null, permissions: [] }),
      })
      setNewGroup({ name: '', description: '' })
      setMessage('Engineering group created. Expand it to assign permissions.')
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
        await portalApi(`/api/admin/engineering-access/users/${id}/groups`, {
          method: 'PUT',
          body: JSON.stringify({ groupIds: userDrafts[id] ?? [] }),
        })
      }
      for (const id of dirtyGroupIds) {
        await portalApi(`/api/admin/engineering-access/groups/${id}`, {
          method: 'PUT',
          body: JSON.stringify(groupDrafts[id]),
        })
      }
      setMessage('Engineering access changes saved. Updated permissions apply on the next request.')
      await load()
    } catch (cause) {
      setError(`${toErrorMessage(cause)} Unsaved changes remain on this page.`)
    } finally {
      setSaving(false)
    }
  }

  return <section className="admin-surface" aria-labelledby="engineering-access-heading" aria-busy={loading}>
    <header className="admin-surface-head">
      <div><span className="kicker">Engineering access control</span><h2 id="engineering-access-heading">Engineering users and groups</h2><p>Assign registered users to Engineering-specific groups and control detailed view, edit, approval, and administration permissions.</p></div>
      <label className="admin-search"><Search size={16} aria-hidden="true"/><span className="sr-only">Search registered users</span><input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Search users"/></label>
    </header>

    {error && <p className="admin-notice error" role="alert"><AlertTriangle size={16}/> {error}</p>}
    {message && <p className="admin-notice success" role="status"><CheckCircle2 size={16}/> {message}</p>}

    {loading || !overview ? <div className="admin-loading" role="status">Loading Engineering access controls...</div> : <>
      <p className="admin-readonly-note">Accounts are registered and activated from Project Tracker Access. This page controls only their Engineering groups and permissions.</p>
      <div className="admin-access-grid">
        <section aria-labelledby="engineering-users-heading">
          <div className="admin-section-title"><div><h3 id="engineering-users-heading">Registered users</h3><p>{filteredUsers.length} of {overview.users.length} shown</p></div><UserRound size={19} aria-hidden="true"/></div>
          {!overview.canManageUsers && <p className="admin-readonly-note">Editing assignments requires Manage Engineering Users.</p>}
          <div className="admin-user-list">
            {filteredUsers.map(user => <article className="admin-user-card" key={user.id}>
              <span className="admin-user-avatar" aria-hidden="true">{initials(user.displayName)}</span>
              <div className="admin-user-identity"><strong>{user.displayName} {accountKey(currentAccountName) === accountKey(user.accountName) && <small>You</small>}</strong><span>{user.accountName}</span><time dateTime={user.lastSeenAt}>{formatLastSeen(user.lastSeenAt)}</time></div>
              <fieldset><legend>Engineering groups</legend>{overview.groups.map(group => <label className="admin-check-row compact" key={group.id}>
                <input
                  type="checkbox"
                  checked={(userDrafts[user.id] ?? []).includes(group.id)}
                  disabled={saving || !overview.canManageUsers || !user.isActive}
                  onChange={event => setUserDrafts(current => ({ ...current, [user.id]: sortedNumbers(event.target.checked
                    ? [...(current[user.id] ?? []), group.id]
                    : (current[user.id] ?? []).filter(id => id !== group.id)) }))}
                />
                <span><strong>{group.name}</strong><small>{group.description}</small></span>
              </label>)}</fieldset>
            </article>)}
          </div>
        </section>

        <section aria-labelledby="engineering-groups-heading">
          <div className="admin-section-title"><div><h3 id="engineering-groups-heading">Engineering permission groups</h3><p>Permissions stack across every assigned group.</p></div><ShieldCheck size={19} aria-hidden="true"/></div>
          {overview.canManageGroups ? <form className="admin-create-form" onSubmit={createGroup}>
            <label><span>Group name</span><input required value={newGroup.name} onChange={event => setNewGroup({ ...newGroup, name: event.target.value })}/></label>
            <label><span>Description</span><input value={newGroup.description} onChange={event => setNewGroup({ ...newGroup, description: event.target.value })}/></label>
            <button className="solid-button" type="submit"><Plus size={15}/> Create</button>
          </form> : <p className="admin-readonly-note">Permission editing requires Manage Engineering Groups.</p>}
          <div className="admin-group-list">{overview.groups.map(group => <PermissionGroup
            key={group.id}
            group={group}
            permissions={overview.permissions}
            draft={groupDrafts[group.id] ?? { name: group.name, description: group.description ?? '', permissions: group.permissions }}
            disabled={saving || !overview.canManageGroups}
            onChange={draft => setGroupDrafts(current => ({ ...current, [group.id]: draft }))}
          />)}</div>
        </section>
      </div>
    </>}

    <div className="admin-save-bar">
      <p aria-live="polite">{pendingCount ? `${pendingCount} pending change${pendingCount === 1 ? '' : 's'}` : 'All Engineering access changes saved'}</p>
      <button className="solid-button" type="button" disabled={!pendingCount || saving} onClick={() => void saveAll()}><Save size={15}/> {saving ? 'Saving...' : 'Save Engineering access'}</button>
    </div>
  </section>
}
