import type { EstimateInput } from './types'

export type QuoteStatus = 'draft' | 'current' | 'past'

export interface QuoteRecord {
  id: string
  ownerAccountName: string
  status: QuoteStatus
  createdAt: string
  updatedAt: string
  estimate: EstimateInput
  selectedQuantity: number
}

const STORAGE_KEY = 'sonaero-estimating-quotes:v1'

function createId() {
  return globalThis.crypto?.randomUUID?.()
    ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function isQuoteRecord(value: unknown): value is QuoteRecord {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<QuoteRecord>
  return (
    typeof candidate.id === 'string'
    && typeof candidate.ownerAccountName === 'string'
    && ['draft', 'current', 'past'].includes(candidate.status ?? '')
    && typeof candidate.createdAt === 'string'
    && typeof candidate.updatedAt === 'string'
    && typeof candidate.selectedQuantity === 'number'
    && Boolean(candidate.estimate)
    && Array.isArray(candidate.estimate?.quantities)
  )
}

function readAllQuotes(): QuoteRecord[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter(isQuoteRecord) : []
  } catch {
    return []
  }
}

function writeAllQuotes(quotes: QuoteRecord[]) {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(quotes))
    return true
  } catch {
    return false
  }
}

export function listQuotes(ownerAccountName: string) {
  return readAllQuotes()
    .filter((quote) => quote.ownerAccountName === ownerAccountName)
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
}

export function findQuote(id: string, ownerAccountName: string) {
  return listQuotes(ownerAccountName).find((quote) => quote.id === id) ?? null
}

export function saveQuote({
  id,
  ownerAccountName,
  status,
  estimate,
  selectedQuantity,
}: {
  id?: string
  ownerAccountName: string
  status: QuoteStatus
  estimate: EstimateInput
  selectedQuantity: number
}): QuoteRecord | null {
  const quotes = readAllQuotes()
  const existing = id ? quotes.find((quote) => quote.id === id) : undefined
  const now = new Date().toISOString()
  const record: QuoteRecord = {
    id: existing?.id ?? createId(),
    ownerAccountName,
    status,
    createdAt: existing?.createdAt ?? now,
    updatedAt: now,
    estimate,
    selectedQuantity,
  }
  const nextQuotes = existing
    ? quotes.map((quote) => quote.id === record.id ? record : quote)
    : [record, ...quotes]
  return writeAllQuotes(nextQuotes) ? record : null
}

export function deleteQuote(id: string, ownerAccountName: string) {
  const quotes = readAllQuotes()
  return writeAllQuotes(
    quotes.filter((quote) => (
      quote.id !== id || quote.ownerAccountName !== ownerAccountName
    )),
  )
}
