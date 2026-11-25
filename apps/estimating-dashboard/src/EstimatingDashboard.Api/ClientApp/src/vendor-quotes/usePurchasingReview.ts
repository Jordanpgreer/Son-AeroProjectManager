import { useCallback, useEffect, useState } from 'react'
import { currentPurchasingReview, startPurchasingReview, type PurchasingReviewState } from './purchasingReviewModel'

export function usePurchasingReview<T extends { quoteHistoryId: number }>(
  quoteHistoryId: number,
  requestRevision: string,
  load: (quoteHistoryId: number, signal: AbortSignal) => Promise<T>,
  failureMessage: string,
  enabled = true,
) {
  const [revision, setRevision] = useState(0)
  const key = JSON.stringify([quoteHistoryId, requestRevision, revision, enabled])
  const [state, setState] = useState<PurchasingReviewState<T>>({ key: '', data: null, loading: enabled, error: null })

  useEffect(() => {
    if (!enabled) {
      setState(current => ({ ...current, key, loading: false, error: null }))
      return
    }
    setState(current => ({ key, data: current.data?.quoteHistoryId === quoteHistoryId ? current.data : null, loading: true, error: null }))
    return startPurchasingReview(quoteHistoryId, load, failureMessage, result => {
      setState(current => current.key !== key ? current : {
        key, data: result.error ? current.data : result.data, error: result.error, loading: false,
      })
    })
  }, [key, quoteHistoryId, enabled, load, failureMessage])

  const current = currentPurchasingReview(state, key, quoteHistoryId, enabled)
  const refresh = useCallback(() => setRevision(value => value + 1), [])
  const setData = (data: T) => setState({ key, data, loading: false, error: null })
  return { ...current, refresh, setData }
}
