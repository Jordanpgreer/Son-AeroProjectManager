import { useEffect, useMemo, useState } from 'react'
import { Eye, GraduationCap, Search, ShieldCheck, UsersRound } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import type {
  AdminAccessPreviewOverview,
  AdminAccessPreviewTarget,
} from './types'
import {
  filterWalkthroughGroups,
  projectTrackerWalkthroughGroups,
  walkthroughApplication,
} from './walkthroughPreview'

export default function WalkthroughPreviewLauncher({
  onLaunch,
}: {
  onLaunch: (target: AdminAccessPreviewTarget) => Promise<void>
}) {
  const [overview, setOverview] = useState<AdminAccessPreviewOverview | null>(null)
  const [query, setQuery] = useState('')
  const [selectedKey, setSelectedKey] = useState('')
  const [loading, setLoading] = useState(true)
  const [launching, setLaunching] = useState(false)
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

  const groups = useMemo(
    () => projectTrackerWalkthroughGroups(overview),
    [overview],
  )
  const filtered = useMemo(
    () => filterWalkthroughGroups(groups, query),
    [groups, query],
  )
  const selected = groups.find((target) => target.key === selectedKey) ?? null
  const selectedCanLaunch = Boolean(selected && walkthroughApplication(selected))

  async function launch() {
    if (!selected || !selectedCanLaunch || launching) return
    setLaunching(true)
    setError(null)
    try {
      await onLaunch(selected)
    } catch (cause) {
      setError(toErrorMessage(cause))
      setLaunching(false)
    }
  }

  return (
    <section className="admin-walkthrough-preview" aria-labelledby="walkthrough-preview-heading">
      <header>
        <span className="admin-placeholder-icon"><GraduationCap size={21} aria-hidden="true" /></span>
        <div>
          <span className="kicker">Walkthrough preview</span>
          <h3 id="walkthrough-preview-heading">Preview a Project Tracker group</h3>
          <p>Open the fictional training workspace with the selected group's current lesson set.</p>
        </div>
      </header>

      <p className="admin-preview-safety"><ShieldCheck size={15} aria-hidden="true" /> This preview does not change real permissions or project data.</p>
      {error && <p className="admin-notice error" role="alert">{error}</p>}

      {loading ? (
        <div className="admin-loading" role="status">Loading Project Tracker groups…</div>
      ) : (
        <>
          <label className="admin-search admin-preview-search">
            <Search size={16} aria-hidden="true" />
            <span className="sr-only">Search Project Tracker groups</span>
            <input
              type="search"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Search groups or roles"
            />
          </label>

          <div className="admin-walkthrough-targets" role="listbox" aria-label="Project Tracker group to preview">
            {filtered.map((target) => {
              const canLaunch = Boolean(walkthroughApplication(target))
              const selectedTarget = selectedKey === target.key
              return (
                <button
                  type="button"
                  role="option"
                  aria-selected={selectedTarget}
                  disabled={!canLaunch}
                  className={selectedTarget ? 'selected' : ''}
                  key={target.key}
                  onClick={() => setSelectedKey(target.key)}
                >
                  <span className="admin-walkthrough-target-icon"><UsersRound size={17} aria-hidden="true" /></span>
                  <span className="admin-walkthrough-target-copy"><strong>{target.title}</strong><small>{target.subtitle}</small></span>
                  <em className={canLaunch ? 'available' : undefined}>{canLaunch ? 'Ready' : 'No module access'}</em>
                </button>
              )
            })}
            {filtered.length === 0 && <p>No Project Tracker groups match that search.</p>}
          </div>

          <footer>
            <p>
              {selected
                ? selectedCanLaunch
                  ? `Ready to preview lessons available to ${selected.title}.`
                  : `${selected.title} cannot launch a walkthrough until it has Project Tracker access.`
                : 'Select a group or role to preview its walkthrough.'}
            </p>
            <button
              className="solid-button"
              type="button"
              disabled={!selectedCanLaunch || launching}
              onClick={() => void launch()}
            >
              <Eye size={15} aria-hidden="true" /> {launching ? 'Opening…' : 'Launch walkthrough preview'}
            </button>
          </footer>
        </>
      )}
    </section>
  )
}
