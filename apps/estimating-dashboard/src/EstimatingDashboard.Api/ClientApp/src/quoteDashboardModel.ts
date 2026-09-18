import { getLatestPublishedRevision, type QuoteRecord, type QuoteRevision, type QuoteStatus } from './quoteStore.ts'
import type { PersonalQuote } from './quoteWorkflowApi.ts'

export type QuoteDashboardFilter = 'all' | QuoteStatus
export type PersonalQuoteView = 'active' | 'overdue' | 'completed'

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

function dueDateValue(value: string | null) {
  if (!value) return Number.POSITIVE_INFINITY
  const parsed = new Date(value).getTime()
  return Number.isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed
}

function completionDateValue(value: string | null) {
  if (!value) return Number.NEGATIVE_INFINITY
  const parsed = new Date(value).getTime()
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed
}

export function sortActivePersonalQuotes(quotes: PersonalQuote[]) {
  return [...quotes]
    .filter((quote) => !quote.isCompleted)
    .sort((left, right) => dueDateValue(left.estimatingDueDate) - dueDateValue(right.estimatingDueDate)
      || right.quoteNumber - left.quoteNumber)
}

export function sortCompletedPersonalQuotes(quotes: PersonalQuote[]) {
  return [...quotes]
    .filter((quote) => quote.isCompleted)
    .sort((left, right) => completionDateValue(right.estimatingCompletionDate) - completionDateValue(left.estimatingCompletionDate)
      || right.quoteNumber - left.quoteNumber)
}
