import { useEffect, useState } from 'react'
import { CheckCircle2, UserRoundCheck, UserRoundX } from 'lucide-react'
import { portalApi, toErrorMessage } from './api'
import type { EstimatorSetting, EstimatorSettingsOverview } from './types'

export default function EstimatorSettingsPanel() {
  const [estimators, setEstimators] = useState<EstimatorSetting[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [saving, setSaving] = useState<Set<string>>(new Set())

  useEffect(() => {
    let active = true
    void portalApi<EstimatorSettingsOverview>('/api/admin/estimating/estimators')
      .then((overview) => {
        if (active) setEstimators(overview.estimators)
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

  async function setActive(estimator: EstimatorSetting, isActive: boolean) {
    setSaving((current) => new Set(current).add(estimator.estimator))
    setError(null)
    setMessage(null)
    try {
      const updated = await portalApi<EstimatorSetting>('/api/admin/estimating/estimators', {
        method: 'PUT',
        body: JSON.stringify({ estimator: estimator.estimator, isActive }),
      })
      setEstimators((current) => current.map((item) =>
        item.estimator.toLocaleLowerCase('en-US') === updated.estimator.toLocaleLowerCase('en-US')
          ? updated
          : item,
      ))
      setMessage(`${updated.estimator} is now ${updated.isActive ? 'active' : 'inactive'} in Estimator Statistics.`)
    } catch (cause) {
      setError(toErrorMessage(cause))
    } finally {
      setSaving((current) => {
        const next = new Set(current)
        next.delete(estimator.estimator)
        return next
      })
    }
  }

  return (
    <section className="admin-surface estimator-settings" aria-labelledby="estimator-settings-heading">
      <header className="admin-surface-head estimator-settings-head">
        <div>
          <span className="kicker">Statistics roster</span>
          <h2 id="estimator-settings-heading">Active estimators</h2>
          <p>Inactive estimators stay in the quote log but are removed from Estimator Statistics and department totals.</p>
        </div>
        <span className="admin-permission-badge">Estimating settings permission</span>
      </header>

      {loading && <div className="admin-loading" role="status">Loading estimators...</div>}
      {error && <p className="admin-message error" role="alert">{error}</p>}
      {message && <p className="admin-message success" role="status"><CheckCircle2 size={15} aria-hidden="true" /> {message}</p>}

      {!loading && estimators.length === 0 && !error && (
        <div className="estimator-settings-empty">
          <strong>No estimators found</strong>
          <p>Estimator names will appear after an Estimating Logs workbook has been imported.</p>
        </div>
      )}

      {!loading && estimators.length > 0 && (
        <div className="estimator-settings-grid">
          {estimators.map((estimator) => {
            const isSaving = saving.has(estimator.estimator)
            const Icon = estimator.isActive ? UserRoundCheck : UserRoundX
            return (
              <article className={estimator.isActive ? 'is-active' : 'is-inactive'} key={estimator.estimator}>
                <span className="estimator-settings-icon"><Icon size={19} aria-hidden="true" /></span>
                <div>
                  <strong>{estimator.estimator}</strong>
                  <small>{estimator.isActive ? 'Shown in statistics' : 'Hidden from statistics'}</small>
                </div>
                <button
                  type="button"
                  className="estimator-status-toggle"
                  role="switch"
                  aria-checked={estimator.isActive}
                  aria-label={`${estimator.estimator}: ${estimator.isActive ? 'active' : 'inactive'}`}
                  disabled={isSaving}
                  onClick={() => void setActive(estimator, !estimator.isActive)}
                >
                  <span aria-hidden="true" />
                  <em>{isSaving ? 'Saving' : estimator.isActive ? 'Active' : 'Inactive'}</em>
                </button>
              </article>
            )
          })}
        </div>
      )}
    </section>
  )
}
