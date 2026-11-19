export type PlanningSource = 'industrios' | 'fulcrum' | 'mixed' | 'unknown'

export function purchasingReviewVisibility(material: {
  data: { planningSource?: PlanningSource } | null; loading: boolean; error: string | null
}, pendingEdits = false) {
  const industriosOnly = !material.loading && !material.error && material.data?.planningSource === 'industrios'
  return {
    showPurchasing: !industriosOnly || pendingEdits,
    loadOutsideProcessing: !material.loading && !industriosOnly,
    waitingForPlanning: material.loading,
  }
}

export interface PurchasingReviewState<T> {
  key: string; data: T | null; loading: boolean; error: string | null
}

export function currentPurchasingReview<T extends { quoteHistoryId: number }>(
  state: PurchasingReviewState<T>, key: string, quoteHistoryId: number, enabled: boolean,
) {
  return {
    data: enabled && state.data?.quoteHistoryId === quoteHistoryId ? state.data : null,
    loading: enabled && (state.key !== key || state.loading),
    error: enabled && state.key === key ? state.error : null,
  }
}

// An aborted or replaced read must never restore a hidden panel or overwrite a newer result.
export function startPurchasingReview<T>(
  quoteHistoryId: number,
  load: (id: number, signal: AbortSignal) => Promise<T>,
  failureMessage: string,
  receive: (result: { data: T; error: null } | { data: null; error: string }) => void,
) {
  const controller = new AbortController()
  void load(quoteHistoryId, controller.signal).then(data => {
    if (!controller.signal.aborted) receive({ data, error: null })
  }, reason => {
    if (!controller.signal.aborted) receive({ data: null, error: reason instanceof Error ? reason.message : failureMessage })
  })
  return () => controller.abort()
}
