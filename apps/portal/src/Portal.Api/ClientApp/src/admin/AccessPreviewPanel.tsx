import { useEffect, useMemo, useState } from 'react'
import { Eye, Search, UserRoundSearch, UsersRound } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import type {
  AdminAccessPreviewOverview,
  AdminAccessPreviewTarget,
} from './types'

export default function AccessPreviewPanel({
  onPreview,
}: {
  onPreview: (target: AdminAccessPreviewTarget) => void
}) {
  const [overview, setOverview] = useState<AdminAccessPreviewOverview | null>(null)
  const [query, setQuery] = useState('')
  const [selectedKey, setSelectedKey] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    void portalApi<AdminAccessPreviewOverview>('/api/admin/access-previews')
      .then((result) => {
        if (active) setOverview(result)
      })
      .catch((cause) => {
        if (active) setError(toErrorMessage(cause))
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
    }
  }, [])

  const targets = useMemo(
    () => [...(overview?.users ?? []), ...(overview?.groups ?? [])],
    [overview],
  )
  const filtered = useMemo(() => {
    const value = query.trim().toLowerCase()
    if (!value) return targets
    return targets.filter((target) =>
      target.title.toLowerCase().includes(value)
      || target.subtitle.toLowerCase().includes(value)
      || target.role.toLowerCase().includes(value))
  }, [query, targets])
  const selected = targets.find((target) => target.key === selectedKey) ?? null

  return (
    <section className="admin-access-preview" aria-labelledby="access-preview-heading">
      <header>
        <span className="admin-placeholder-icon"><Eye size={21} /></span>
        <div>
          <span className="kicker">Read-only access preview</span>
          <h3 id="access-preview-heading">View the Hub as a user or group</h3>
          <p>Confirm which application cards they can see, then open any available module in the same read-only preview.</p>
        </div>
      </header>

      {loading ? (
        <div className="admin-loading" role="status">Loading users and groups...</div>
      ) : error ? (
        <p className="admin-notice error" role="alert">{error}</p>
      ) : (
        <>
          <label className="admin-search admin-preview-search">
            <Search size={16} aria-hidden="true" />
            <span className="sr-only">Search preview users and groups</span>
            <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search users or groups" />
          </label>
          <div className="admin-preview-targets" role="listbox" aria-label="Preview target">
            {filtered.map((target) => (
              <button
                type="button"
                role="option"
                aria-selected={selectedKey === target.key}
                className={selectedKey === target.key ? 'selected' : ''}
                key={target.key}
                onClick={() => setSelectedKey(target.key)}
              >
                <span>{target.kind === 'user' ? <UserRoundSearch size={17} /> : <UsersRound size={17} />}</span>
                <span><strong>{target.title}</strong><small>{target.subtitle}</small></span>
                <em>{target.role}</em>
              </button>
            ))}
            {filtered.length === 0 && <p>No users or groups match that search.</p>}
          </div>
          <footer>
            <p>{selected ? `${selected.applications.length} application${selected.applications.length === 1 ? '' : 's'} visible to ${selected.title}` : 'Select a user or group to preview.'}</p>
            <button className="solid-button" type="button" disabled={!selected} onClick={() => selected && onPreview(selected)}>
              <Eye size={15} /> Start read-only preview
            </button>
          </footer>
        </>
      )}
    </section>
  )
}
