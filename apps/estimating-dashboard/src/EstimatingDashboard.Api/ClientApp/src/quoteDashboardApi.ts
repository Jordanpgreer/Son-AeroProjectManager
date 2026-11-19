import type { ArdaStatus, PersonalQuote, PersonalQuoteStats } from './quoteWorkflowApi'

export type DashboardScope = 'mine' | 'all'
export type DashboardStatus = 'all' | 'active' | 'overdue' | 'completed' | ArdaStatus
export interface DashboardQuote extends PersonalQuote {
  partNumbers: string[]
  canOpenQuoteStatus: boolean
  canGenerateEstimate: boolean
}
export interface DashboardStats extends PersonalQuoteStats {
  activeCount: number; overdueCount: number
  onTimeCount: number; onTimeSampleSize: number; onTimePercentage: number | null; onTimeMissingCount: number
}
export interface DashboardQuery {
  scope: DashboardScope; from?: string; to?: string; search: string; status: DashboardStatus; page: number; pageSize: number
}
export interface QuoteDashboardResponse {
  records: DashboardQuote[]; total: number; page: number; pageSize: number; stats: DashboardStats
}

export function dashboardLastPage(total: number, pageSize: number) {
  return Math.max(1, Math.ceil(total / Math.max(1, pageSize)))
}

export function dashboardQueryString(query: DashboardQuery) {
  const params = new URLSearchParams({ scope: query.scope, status: query.status, page: String(query.page), pageSize: String(query.pageSize) })
  if (query.from && query.to) { params.set('from', query.from); params.set('to', query.to) }
  if (query.search.trim()) params.set('search', query.search.trim())
  return params.toString()
}

export async function loadQuoteDashboard(query: DashboardQuery, signal?: AbortSignal): Promise<QuoteDashboardResponse> {
  const response = await fetch(`/api/quote-workflow/dashboard?${dashboardQueryString(query)}`, { credentials: 'include', signal })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? body?.detail ?? `Quotes could not be loaded (${response.status}).`)
  }
  return response.json()
}

export interface DashboardSnapshot {
  key: string; statsKey: string; data: QuoteDashboardResponse | null; stats: DashboardStats | null; loading: boolean; error: string | null
}

export function dashboardSnapshot(snapshot: DashboardSnapshot, key: string, statsKey: string) {
  return {
    data: snapshot.key === key ? snapshot.data : null,
    stats: snapshot.statsKey === statsKey ? snapshot.stats : null,
    loading: snapshot.key !== key || snapshot.loading,
    error: snapshot.key === key ? snapshot.error : null,
  }
}

export function startDashboardRequest(query: DashboardQuery,
  load: (query: DashboardQuery, signal: AbortSignal) => Promise<QuoteDashboardResponse>,
  receive: (data: QuoteDashboardResponse | null, error: string | null) => void) {
  const controller = new AbortController()
  void load(query, controller.signal).then(data => {
    if (!controller.signal.aborted) receive(data, null)
  }, reason => {
    if (!controller.signal.aborted) receive(null, reason instanceof Error ? reason.message : 'Quotes could not be loaded.')
  })
  return () => controller.abort()
}
