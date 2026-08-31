import { getLatestPublishedRevision, type QuoteRecord, type QuoteRevision, type QuoteStatus } from './quoteStore.ts'

export type QuoteDashboardFilter = 'all' | QuoteStatus

export function quoteDashboardVersion(
  quote: QuoteRecord,
  filter: QuoteDashboardFilter,
): QuoteRevision | null {
  if (filter === 'draft') return quote.draft
  return getLatestPublishedRevision(quote) ?? quote.draft
}

export function quoteDashboardStatus(
  quote: QuoteRecord,
  filter: QuoteDashboardFilter,
): QuoteStatus {
  return filter === 'draft' ? 'draft' : quote.status
}
