import { useEffect, useMemo, useState } from 'react'
import { Eye, Search, UserRoundSearch, UsersRound } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import {
  accessPreviewTargetBadge,
  accessPreviewTargetSummary,
  filterAccessPreviewTargets,
  isFirstSignInPreview,
} from './accessPreviewTarget'
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
  const filtered = useMemo(
    () => filterAccessPreviewTargets(targets, query),
    [query, targets],
  )
  const selected = targets.find((target) => target.key === selectedKey) ?? null

  return (
    <section className="admin-access-preview" aria-labelledby="access-preview-heading">
      <header>
        <span className="admin-placeholder-icon"><Eye size={21} /></span>
        <div>
          <span className="kicker">Verify before handing off access</span>
          <h3 id="access-preview-heading">Preview Arda as a person or group</h3>
          <p>Confirm which application cards are visible, then open an available module in the same read-only preview.</p>
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
          <div className="admin-preview-targets" role="group" aria-label="Preview target">
            {filtered.map((target) => (
              <button
                type="button"
                aria-pressed={selectedKey === target.key}
                className={selectedKey === target.key ? 'selected' : ''}
                key={target.key}
                onClick={() => setSelectedKey(target.key)}
              >
                <span>{target.kind === 'user' ? <UserRoundSearch size={17} /> : <UsersRound size={17} />}</span>
                <span><strong>{target.title}</strong><small>{target.subtitle}</small></span>
                <em>{accessPreviewTargetBadge(target)}</em>
              </button>
            ))}
            {filtered.length === 0 && <p>No users or groups match that search.</p>}
          </div>
          <footer>
            <p>{selected ? accessPreviewTargetSummary(selected) : 'Select a user or group to preview.'}</p>
            <button className="solid-button" type="button" disabled={!selected} onClick={() => selected && onPreview(selected)}>
              <Eye size={15} /> {selected && isFirstSignInPreview(selected)
                ? 'Preview first-sign-in screen'
                : 'Start read-only preview'}
            </button>
          </footer>
        </>
      )}
    </section>
  )
}
