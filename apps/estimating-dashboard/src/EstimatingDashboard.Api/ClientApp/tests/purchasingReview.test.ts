import assert from 'node:assert/strict'
import test from 'node:test'
import { currentPurchasingReview, purchasingReviewVisibility, startPurchasingReview, type PlanningSource } from '../src/vendor-quotes/purchasingReviewModel.ts'

test('only confirmed all-Industrios planning hides the whole purchasing review and skips OP', () => {
  assert.deepEqual(purchasingReviewVisibility({ data: { planningSource: 'industrios' }, loading: false, error: null }), {
    showPurchasing: false, loadOutsideProcessing: false, waitingForPlanning: false,
  })
  for (const planningSource of ['fulcrum', 'mixed', 'unknown', undefined] as (PlanningSource | undefined)[]) {
    assert.deepEqual(purchasingReviewVisibility({ data: { planningSource }, loading: false, error: null }), {
      showPurchasing: true, loadOutsideProcessing: true, waitingForPlanning: false,
    })
  }
})

test('initial loading remains visible while the dependent OP request waits', () => {
  assert.deepEqual(purchasingReviewVisibility({ data: null, loading: true, error: null }), {
    showPurchasing: true, loadOutsideProcessing: false, waitingForPlanning: true,
  })
})

test('a failed planning check cannot masquerade as Industrios and does not block OP', () => {
  for (const data of [null, { planningSource: 'industrios' as const }]) {
    assert.deepEqual(purchasingReviewVisibility({ data, loading: false, error: 'Fulcrum unavailable' }), {
      showPurchasing: true, loadOutsideProcessing: true, waitingForPlanning: false,
    })
  }
})

test('refresh rechecks a former Industrios classification and can reveal mixed planning', () => {
  const previous = { quoteHistoryId: 17, planningSource: 'industrios' as const }
  const state = { key: 'first-check', data: previous, loading: false, error: null }
  assert.equal(purchasingReviewVisibility(currentPurchasingReview(state, 'first-check', 17, true)).showPurchasing, false)
  const refreshing = currentPurchasingReview(state, 'refresh', 17, true)
  assert.equal(refreshing.loading, true)
  assert.equal(purchasingReviewVisibility(refreshing).showPurchasing, true)
  assert.equal(purchasingReviewVisibility(refreshing).loadOutsideProcessing, false)
  const mixed = currentPurchasingReview({ key: 'refresh', data: { ...previous, planningSource: 'mixed' as const }, loading: false, error: null }, 'refresh', 17, true)
  assert.equal(purchasingReviewVisibility(mixed).showPurchasing, true)
  assert.equal(purchasingReviewVisibility(mixed).loadOutsideProcessing, true)
})

test('confirming Industrios keeps an active quantity or draft edit mounted until it ends', () => {
  const material = { data: { planningSource: 'industrios' as const }, loading: false, error: null }
  assert.equal(purchasingReviewVisibility(material, true).showPurchasing, true)
  assert.equal(purchasingReviewVisibility(material, true).loadOutsideProcessing, false)
  assert.equal(purchasingReviewVisibility(material, false).showPurchasing, false)
})

test('disabled review immediately masks old results and errors, and reactivation starts loading', () => {
  const previous = { key: 'enabled', data: { quoteHistoryId: 17 }, loading: false, error: 'Old failure' }
  assert.deepEqual(currentPurchasingReview(previous, 'disabled', 17, false), { data: null, loading: false, error: null })
  const disabled = { ...previous, key: 'disabled', loading: false, error: null }
  assert.deepEqual(currentPurchasingReview(disabled, 'enabled', 17, true), { data: previous.data, loading: true, error: null })
})

test('another quote never inherits the previous result or error as its planning state', () => {
  const previous = { key: '17', data: { quoteHistoryId: 17, planningSource: 'industrios' as const }, loading: false, error: 'Old failure' }
  const next = currentPurchasingReview(previous, '18', 18, true)
  assert.deepEqual(next, { data: null, loading: true, error: null })
  assert.equal(purchasingReviewVisibility(next).showPurchasing, true)
})

test('disabled or replaced in-flight reads cannot publish late successes or failures', async () => {
  for (const reject of [false, true]) {
    let finish: (value: { quoteHistoryId: number }) => void = () => {}
    let fail: (reason: Error) => void = () => {}
    let signal: AbortSignal | undefined
    const results: unknown[] = []
    const stop = startPurchasingReview(17, (_, cancellation) => {
      signal = cancellation
      return new Promise<{ quoteHistoryId: number }>((resolve, reject) => { finish = resolve; fail = reject })
    }, 'Review unavailable', result => results.push(result))
    stop()
    assert.equal(signal?.aborted, true)
    if (reject) fail(new Error('Late failure'))
    else finish({ quoteHistoryId: 17 })
    await Promise.resolve()
    assert.deepEqual(results, [])
  }
})

test('successful and failed current reads preserve their distinct outcomes', async () => {
  const results: unknown[] = []
  startPurchasingReview(17, async id => ({ quoteHistoryId: id, planningSource: 'mixed' }), 'Review unavailable', result => results.push(result))
  await Promise.resolve()
  assert.deepEqual(results, [{ data: { quoteHistoryId: 17, planningSource: 'mixed' }, error: null }])
  startPurchasingReview(17, async () => { throw new Error('Verified diagnostic') }, 'Review unavailable', result => results.push(result))
  await Promise.resolve()
  assert.deepEqual(results[1], { data: null, error: 'Verified diagnostic' })
  startPurchasingReview(17, async () => { throw null }, 'Review unavailable', result => results.push(result))
  await Promise.resolve()
  assert.deepEqual(results[2], { data: null, error: 'Review unavailable' })
})
