import { useCallback, useEffect, useMemo, useState } from 'react'
import { Check, LockKeyhole, Search, ShieldCheck, UserRoundCog } from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import type {
  ModuleAccessCatalogEntry,
  ModuleAccessRole,
  ModuleAccessUser,
  UserModuleAccess,
} from './types'

type ModuleRole = ModuleAccessRole['role']

const ROLE_SUMMARIES: Record<ModuleRole, string> = {
  Viewer: 'Can open the module and view its records.',
  Editor: 'Includes Viewer access and normal create or edit actions.',
  Admin: 'Includes Editor access and administrative or destructive actions.',
}

function assignmentFor(user: ModuleAccessUser, moduleKey: string): UserModuleAccess {
  return user.modules.find((module) => module.moduleKey === moduleKey) ?? {
    moduleKey,
    enabled: false,
    role: null,
    permissions: [],
    updatedAt: null,
  }
}

export default function ModuleAccessPanel({
  moduleKey,
  moduleName,
  currentAccountName,
}: {
  moduleKey: 'engineering' | 'estimating'
  moduleName: string
  currentAccountName: string | null
}) {
  const [catalog, setCatalog] = useState<ModuleAccessCatalogEntry | null>(null)
  const [users, setUsers] = useState<ModuleAccessUser[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)
  const [savingUserId, setSavingUserId] = useState<number | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [catalogResponse, userResponse] = await Promise.all([
        trackerApi<ModuleAccessCatalogEntry[]>('/api/admin/module-access/catalog'),
        trackerApi<ModuleAccessUser[]>('/api/admin/module-access'),
      ])
      const moduleCatalog = catalogResponse.find((module) => module.key === moduleKey)
      if (!moduleCatalog) throw new Error(`${moduleName} is not present in the module access catalog.`)
      setCatalog(moduleCatalog)
      setUsers(userResponse)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setLoading(false)
    }
  }, [moduleKey, moduleName])

  useEffect(() => {
    void load()
  }, [load])

  const filteredUsers = useMemo(() => {
    const query = search.trim().toLowerCase()
    if (!query) return users
    return users.filter((user) =>
      user.displayName.toLowerCase().includes(query)
      || user.accountName.toLowerCase().includes(query))
  }, [search, users])

  async function saveRole(user: ModuleAccessUser, role: ModuleRole | null) {
    setSavingUserId(user.userId)
    setError(null)
    setSuccess(null)
    try {
      const updated = await trackerApi<UserModuleAccess>(
        `/api/admin/users/${user.userId}/module-access/${moduleKey}`,
        {
          method: 'PUT',
          body: JSON.stringify({ enabled: role !== null, role }),
        },
      )
      setUsers((current) => current.map((candidate) =>
        candidate.userId === user.userId
          ? {
              ...candidate,
              modules: [
                ...candidate.modules.filter((module) => module.moduleKey !== moduleKey),
                updated,
              ],
            }
          : candidate))
      setSuccess(
        role
          ? `${user.displayName} now has ${role} access to ${moduleName}.`
          : `${moduleName} access was removed for ${user.displayName}.`,
      )
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSavingUserId(null)
    }
  }

  if (loading) {
    return <div className="admin-loading" role="status">Loading {moduleName} access...</div>
  }

  if (!catalog) {
    return (
      <section className="admin-surface admin-placeholder" role="alert">
        <span className="admin-placeholder-icon"><LockKeyhole size={25} /></span>
        <h2>{moduleName} access is unavailable</h2>
        <p>{error ?? 'The module access catalog could not be loaded.'}</p>
        <button type="button" className="ghost-button" onClick={() => void load()}>Try again</button>
      </section>
    )
  }

  return (
    <section className="admin-surface module-access-surface" aria-labelledby={`${moduleKey}-access-heading`}>
      <header className="admin-surface-head">
        <div>
          <span className="kicker">Module roles</span>
          <h2 id={`${moduleKey}-access-heading`}>{moduleName} access</h2>
          <p>Assign the least-privileged role each registered user needs. Changes take effect on their next request.</p>
        </div>
        <ShieldCheck size={22} aria-hidden="true" />
      </header>

      <div className="module-role-guide" aria-label={`${moduleName} role definitions`}>
        {catalog.roles.map((role) => (
          <article key={role.role}>
            <span className={`module-role-badge role-${role.role.toLowerCase()}`}>{role.role}</span>
            <p>{ROLE_SUMMARIES[role.role]}</p>
            <small>{role.permissions.map((permission) => permission.label).join(' · ')}</small>
          </article>
        ))}
      </div>

      {error && <p className="admin-notice error" role="alert">{error}</p>}
      {success && <p className="admin-notice success" role="status"><Check size={15} />{success}</p>}

      <div className="module-access-toolbar">
        <label className="admin-search">
          <Search size={16} aria-hidden="true" />
          <span className="sr-only">Search registered users</span>
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Search users"
          />
        </label>
        <span>{filteredUsers.length} registered user{filteredUsers.length === 1 ? '' : 's'}</span>
      </div>

      <div className="module-user-list">
        {filteredUsers.map((user) => {
          const assignment = assignmentFor(user, moduleKey)
          const isCurrentUser = currentAccountName?.toLowerCase() === user.accountName.toLowerCase()
          const busy = savingUserId === user.userId
          return (
            <article className={`module-user-row ${user.isActive ? '' : 'is-inactive'}`.trim()} key={user.userId}>
              <span className="module-user-icon" aria-hidden="true"><UserRoundCog size={18} /></span>
              <div className="module-user-copy">
                <strong>{user.displayName}{isCurrentUser && <small>You</small>}</strong>
                <span>{user.accountName}</span>
                {!user.isActive && <em>Inactive Project Tracker account</em>}
              </div>
              <label className="module-role-field">
                <span className="sr-only">{moduleName} role for {user.displayName}</span>
                <select
                  value={assignment.role ?? ''}
                  disabled={!user.isActive || busy}
                  onChange={(event) => void saveRole(
                    user,
                    event.target.value ? event.target.value as ModuleRole : null,
                  )}
                >
                  <option value="">No access</option>
                  {catalog.roles.map((role) => (
                    <option key={role.role} value={role.role}>{role.role}</option>
                  ))}
                </select>
              </label>
              <span className={`module-access-state ${assignment.enabled ? 'enabled' : ''}`}>
                {busy ? 'Saving…' : assignment.enabled ? assignment.role : 'No access'}
              </span>
            </article>
          )
        })}
        {filteredUsers.length === 0 && (
          <p className="admin-empty">No registered users match this search.</p>
        )}
      </div>
    </section>
  )
}
