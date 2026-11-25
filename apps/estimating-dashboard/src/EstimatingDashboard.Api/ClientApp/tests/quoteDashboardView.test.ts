import assert from 'node:assert/strict'
import test from 'node:test'
import { calendarDate, dashboardPeriod, periodButtonLabel, periodDescription, validCustomPeriod } from '../src/quoteDashboardPeriods.ts'
import { dashboardLastPage, dashboardQueryString, dashboardSnapshot, loadQuoteDashboard, startDashboardRequest, type DashboardQuery, type QuoteDashboardResponse } from '../src/quoteDashboardApi.ts'

const query: DashboardQuery = { scope: 'all', from: '2028-02-01', to: '2028-02-29', search: '', status: 'all', page: 1, pageSize: 25 }
const result: QuoteDashboardResponse = {
  records: [], total: 26, page: 1, pageSize: 25,
  stats: { activeCount: 10, overdueCount: 2, completedCount: 16, averageCompletionWorkdays: 3.5, completionSampleSize: 16,
    wonCount: 7, wonTotalValue: 700, lostCount: 1, lostTotalValue: 100, onTimeCount: 12, onTimeSampleSize: 15, onTimePercentage: 80, onTimeMissingCount: 1 },
}

test('default month uses the actual local calendar rather than a fixed preview month or UTC date', () => {
  const now = new Date()
  const period = dashboardPeriod('this-month')
  assert.equal(period.from, calendarDate(new Date(now.getFullYear(), now.getMonth(), 1)))
  assert.equal(period.to, calendarDate(new Date(now.getFullYear(), now.getMonth() + 1, 0)))
  assert.deepEqual(dashboardPeriod('this-month', new Date(2028, 1, 10, 23, 59)), { preset: 'this-month', from: '2028-02-01', to: '2028-02-29' })
})

test('month, quarter and year presets cover inclusive calendar bounds across year changes', () => {
  assert.deepEqual(dashboardPeriod('last-month', new Date(2027, 0, 1)), { preset: 'last-month', from: '2026-12-01', to: '2026-12-31' })
  assert.deepEqual(dashboardPeriod('this-quarter', new Date(2026, 10, 19)), { preset: 'this-quarter', from: '2026-10-01', to: '2026-12-31' })
  assert.deepEqual(dashboardPeriod('this-quarter', new Date(2027, 0, 1)), { preset: 'this-quarter', from: '2027-01-01', to: '2027-03-31' })
  assert.deepEqual(dashboardPeriod('this-year', new Date(2028, 1, 29)), { preset: 'this-year', from: '2028-01-01', to: '2028-12-31' })
})

test('all time omits date bounds so undated quotes remain eligible', () => {
  assert.deepEqual(dashboardPeriod('all-time'), { preset: 'all-time' })
  const params = new URLSearchParams(dashboardQueryString({ ...query, from: undefined, to: undefined }))
  assert.equal(params.has('from'), false)
  assert.equal(params.has('to'), false)
  assert.equal(periodDescription({ preset: 'all-time' }), 'All time')
})

test('custom ranges require both real dates in order and accept a single day', () => {
  assert.equal(validCustomPeriod('2028-02-29', '2028-02-29'), true)
  assert.equal(validCustomPeriod('2028-02-29', '2028-03-01'), true)
  for (const [from, to] of [['', '2028-03-01'], ['2028-02-01', ''], ['2027-02-29', '2027-03-01'], ['2028-03-01', '2028-02-29'], ['2028-2-1', '2028-03-01'], ['invalid', '2028-03-01']])
    assert.equal(validCustomPeriod(from, to), false)
})

test('display labels describe exactly the same period sent to the server', () => {
  const period = dashboardPeriod('last-month', new Date(2027, 0, 1))
  assert.equal(periodDescription(period), 'December 2026')
  assert.match(periodButtonLabel(period), /^Last month · /)
  assert.equal(periodDescription(dashboardPeriod('this-quarter', new Date(2027, 0, 1))), 'Q1 2027')
  assert.equal(periodDescription({ preset: 'custom', from: '2026-11-01', to: '2026-11-03' }), 'Nov 1, 2026 – Nov 3, 2026')
})

test('search, paging and workflow filters are sent to the server with literal characters preserved', () => {
  const params = new URLSearchParams(dashboardQueryString({ ...query, search: '  #4705 / 36+5703 & Aero  ', status: 'RFQ Sent', page: 3 }))
  assert.equal(params.get('search'), '#4705 / 36+5703 & Aero')
  assert.equal(params.get('status'), 'RFQ Sent')
  assert.equal(params.get('page'), '3')
  assert.equal(params.get('pageSize'), '25')
  assert.equal(params.get('scope'), 'all')
  assert.equal(params.get('from'), '2028-02-01')
  assert.equal(params.get('to'), '2028-02-29')
})

test('individual assignee queries keep their independent status and omit dates by default', () => {
  const filter: DashboardQuery = { scope: 'all', estimator: 'CASEY LEE', search: '', status: 'completed', page: 1, pageSize: 25 }
  const params = new URLSearchParams(dashboardQueryString(filter))
  assert.equal(params.get('estimator'), 'CASEY LEE')
  assert.equal(params.get('status'), 'completed')
  assert.equal(params.has('from'), false)
  assert.equal(params.has('to'), false)
  assert.equal(new URLSearchParams(dashboardQueryString({ ...filter, scope: 'mine' })).has('estimator'), false)
})

test('dashboard reads include authentication and cancellation without triggering an ERP refresh', async () => {
  const original = globalThis.fetch
  const signal = new AbortController().signal
  let seen: { url: string; init?: RequestInit } | undefined
  globalThis.fetch = async (input, init) => { seen = { url: String(input), init }; return Response.json(result) }
  try {
    assert.deepEqual(await loadQuoteDashboard(query, signal), result)
    assert.match(seen!.url, /^\/api\/quote-workflow\/dashboard\?/)
    assert.equal(seen!.init?.credentials, 'include')
    assert.equal(seen!.init?.signal, signal)
    assert.equal(seen!.init?.method, undefined)
  } finally { globalThis.fetch = original }
})

test('a denied scope stays an error and is never shown as an empty successful list', async () => {
  const original = globalThis.fetch
  globalThis.fetch = async () => Response.json({ message: 'All quotes access is required.' }, { status: 403 })
  try { await assert.rejects(loadQuoteDashboard(query), /All quotes access is required/) }
  finally { globalThis.fetch = original }
})

test('changed scope, dates or owner immediately hide both stale records and stale performance', () => {
  const snapshot = { key: 'all-feb-page1', statsKey: 'owner-all-feb', data: result, stats: result.stats, loading: false, error: null }
  for (const key of ['mine-feb', 'all-march', 'other-owner-all-feb'])
    assert.deepEqual(dashboardSnapshot(snapshot, key, key), { data: null, stats: null, loading: true, error: null })
})

test('search, status and page changes retain scope-period metrics while replacing the visible rows', () => {
  const snapshot = { key: 'original-list', statsKey: 'all-feb', data: result, stats: result.stats, loading: false, error: 'Old request failure' }
  for (const key of ['new-search', 'new-status', 'page2'])
    assert.deepEqual(dashboardSnapshot(snapshot, key, 'all-feb'), { data: null, stats: result.stats, loading: true, error: null })
})

test('late successful and failed requests cannot overwrite a newer scope after cancellation', async () => {
  for (const fails of [false, true]) {
    let resolve: (value: QuoteDashboardResponse) => void = () => {}
    let reject: (reason: Error) => void = () => {}
    let signal: AbortSignal | undefined
    const received: unknown[] = []
    const stop = startDashboardRequest(query, (_, abortSignal) => {
      signal = abortSignal
      return new Promise((yes, no) => { resolve = yes; reject = no })
    }, (...values) => received.push(values))
    stop()
    assert.equal(signal?.aborted, true)
    if (fails) reject(new Error('Stale scope failure'))
    else resolve(result)
    await Promise.resolve()
    assert.deepEqual(received, [])
  }
})

test('when a refreshed cohort shrinks, the last valid page returns the user to visible records', () => {
  assert.equal(dashboardLastPage(26, 25), 2)
  assert.equal(dashboardLastPage(25, 25), 1)
  assert.equal(Math.min(2, dashboardLastPage(25, 25)), 1)
  assert.equal(dashboardLastPage(0, 25), 1)
  assert.equal(dashboardLastPage(51, 25), 3)
})
