import { useEffect, useState } from 'react'

export function usePurchasingReview<T extends { quoteHistoryId: number }>(
  quoteHistoryId: number,
  requestRevision: string,
  load: (quoteHistoryId: number, signal: AbortSignal) => Promise<T>,
  failureMessage: string,
) {
  const [previous, setData] = useState<T | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    void load(quoteHistoryId, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(reason => {
      if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : failureMessage)
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false)
    })
    return () => controller.abort()
  }, [quoteHistoryId, requestRevision, revision, load, failureMessage])

  const data = previous?.quoteHistoryId === quoteHistoryId ? previous : null
  const refresh = () => setRevision(value => value + 1)
  return { data, loading, error, refresh, setData }
}
