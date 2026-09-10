import { calculateEstimate } from './calculations'
import type { QuoteRevision } from './quoteStore'

export function currency(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 0,
  })
}

export function quoteTitle(version: QuoteRevision | null) {
  return version?.estimate.metadata.quoteLogNumber
    || version?.estimate.metadata.partNumber
    || 'Untitled quote'
}

export function quoteValue(version: QuoteRevision | null) {
  if (!version) return 0
  const result = calculateEstimate(version.estimate)
  return result.ok
    ? result.quantities[version.selectedQuantity]?.extendedValue ?? 0
    : 0
}

export function formatDate(value: string) {
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  }).format(new Date(/^\d{4}-\d{2}-\d{2}$/.test(value) ? `${value}T00:00:00` : value))
}

export function formatOptionalDate(value: string | null) {
  return value ? formatDate(value) : '—'
}

export function linkedSourceFromHash() {
  const id = Number(new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('source'))
  return Number.isSafeInteger(id) && id > 0 ? id : null
}
