import { useEffect, useMemo, useState } from 'react'
import { CheckCircle2, KeyRound, UploadCloud } from 'lucide-react'
import { toErrorMessage, trackerApi } from './api'
import {
  canGrantEstimatingHistoryImport,
  estimatingImportPermissions,
  hasEstimatingHistoryImport,
} from './estimatingImportAccess'
import type { AccessGroup, AccessOverview } from './types'

export default function EstimatingImportAccessPanel() {
  const [overview, setOverview] = useState<AccessOverview | null>(null)
  const [loading, setLoading] = useState(true)
  const [savingGroupId, setSavingGroupId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    void trackerApi<AccessOverview>('/api/admin/access')
      .then((next) => {
        if (active) setOverview(next)
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

  const permission = useMemo(
    () => overview?.permissions.find((candidate) => candidate.key === estimatingImportPermissions.historyImport),
    [overview],
  )

  async function setImportAccess(group: AccessGroup, enabled: boolean) {
    if (savingGroupId !== null || (enabled && !canGrantEstimatingHistoryImport(group))) return
    setSavingGroupId(group.id)
    setError(null)
    setMessage(null)
    try {
      const updated = await trackerApi<AccessGroup>(`/api/admin/groups/${group.id}/estimating-history-import`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      })
      setOverview((current) => current ? {
        ...current,
        groups: current.groups.map((candidate) => candidate.id === updated.id ? updated : candidate),
      } : current)
      setMessage(`${updated.name} can ${enabled ? 'now' : 'no longer'} import Estimating Logs.`)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSavingGroupId(null)
    }
  }

  return (
    <section className="admin-surface estimating-import-access" aria-labelledby="estimating-import-access-heading">
      <header className="admin-surface-head estimator-settings-head">
        <div>
          <span className="kicker">Estimating Logs access</span>
          <h2 id="estimating-import-access-heading">Workbook import access</h2>
          <p>{permission?.description ?? 'Control which permission groups can validate and import Estimating Logs workbooks.'}</p>
        </div>
        <span className="admin-permission-badge"><KeyRound size={13} aria-hidden="true" /> Manage Groups permission</span>
      </header>

      {loading && <div className="admin-loading" role="status">Loading import access...</div>}
      {error && <p className="admin-notice error" role="alert">{error}</p>}
      {message && <p className="admin-notice success" role="status"><CheckCircle2 size={15} aria-hidden="true" /> {message}</p>}

      {!loading && overview && (
        <div className="estimating-import-access-grid">
          {overview.groups.map((group) => {
            const enabled = hasEstimatingHistoryImport(group)
            const prerequisites = canGrantEstimatingHistoryImport(group)
            const isSaving = savingGroupId === group.id
            return (
              <article className={enabled ? 'is-active' : ''} key={group.id}>
                <span className="estimating-import-access-icon"><UploadCloud size={18} aria-hidden="true" /></span>
                <div>
                  <strong>{group.name}</strong>
                  <small>{prerequisites
                    ? `${group.userCount} ${group.userCount === 1 ? 'person' : 'people'} · ${permission?.label ?? 'Import Estimating Logs'}`
                    : 'Grant View estimating and View Estimating Logs in Arda Access first.'}</small>
                </div>
                <button
                  type="button"
                  className="estimator-status-toggle"
                  role="switch"
                  aria-checked={enabled}
                  aria-label={`${group.name}: ${enabled ? 'can' : 'cannot'} import Estimating Logs`}
                  disabled={savingGroupId !== null || (!prerequisites && !enabled)}
                  onClick={() => void setImportAccess(group, !enabled)}
                >
                  <span aria-hidden="true" />
                  <em>{isSaving ? 'Saving' : enabled ? 'Allowed' : 'Off'}</em>
                </button>
              </article>
            )
          })}
        </div>
      )}
    </section>
  )
}
