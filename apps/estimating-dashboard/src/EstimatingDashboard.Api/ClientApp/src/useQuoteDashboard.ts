import { useEffect, useState } from 'react'
import { dashboardQueryString, dashboardSnapshot, loadQuoteDashboard, startDashboardRequest, type DashboardEstimator, type DashboardQuery, type DashboardSnapshot } from './quoteDashboardApi'

export function useQuoteDashboard(owner: string, query: DashboardQuery) {
  const key = `${owner}|${dashboardQueryString(query)}`
  const statsKey = JSON.stringify([owner, query.scope, query.estimator, query.from, query.to])
  const [revision, setRevision] = useState(0)
  const [roster, setRoster] = useState<{ owner: string; estimators: DashboardEstimator[] }>()
  const [snapshot, setSnapshot] = useState<DashboardSnapshot>({ key: '', statsKey: '', data: null, stats: null, loading: true, error: null })
  useEffect(() => {
    let stop: (() => void) | undefined
    const timer = window.setTimeout(() => {
      setSnapshot(current => ({ key, statsKey, data: current.key === key ? current.data : null,
        stats: current.statsKey === statsKey ? current.stats : null, loading: true, error: null }))
      stop = startDashboardRequest(query, loadQuoteDashboard, (data, error) => {
        if (data) setRoster({ owner, estimators: data.estimators ?? [] })
        setSnapshot(current => ({ key, statsKey, data: data ?? (current.key === key ? current.data : null),
          stats: data?.stats ?? (current.statsKey === statsKey ? current.stats : null), loading: false, error }))
      })
    }, 180)
    return () => { window.clearTimeout(timer); stop?.() }
  }, [key, owner, statsKey, revision, query])
  return { ...dashboardSnapshot(snapshot, key, statsKey),
    estimators: roster?.owner === owner ? roster.estimators : [],
    refresh: () => setRevision(value => value + 1) }
}
