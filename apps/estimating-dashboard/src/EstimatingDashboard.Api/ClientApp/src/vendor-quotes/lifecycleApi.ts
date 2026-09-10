import type { QuoteStatusDetail } from './types'

async function change(quoteId: number, path: string, body: unknown, method: 'POST' | 'PUT' = 'POST'): Promise<QuoteStatusDetail> {
  const response = await fetch(`/api/quote-status/${quoteId}/${path}`, { method, credentials: 'include', headers: { 'Content-Type': 'application/json', 'X-Arda-Request': 'vendor-quotes' }, body: JSON.stringify(body) })
  if (!response.ok) {
    const error = await response.json().catch(() => null)
    throw new Error(error?.message ?? error?.detail ?? `This change could not be completed (${response.status}).`)
  }
  return response.json() as Promise<QuoteStatusDetail>
}
export const removeEmail = (quoteId: number, id: number, expectedVersion: number) => change(quoteId, `messages/${id}/remove`, { expectedVersion })
export const restoreEmail = (quoteId: number, id: number, expectedVersion: number) => change(quoteId, `messages/${id}/restore`, { expectedVersion })
export const removeNote = (quoteId: number, id: string, expectedVersion: number) => change(quoteId, `notes/${encodeURIComponent(id)}/remove`, { expectedVersion })
export const restoreNote = (quoteId: number, id: string, expectedVersion: number) => change(quoteId, `notes/${encodeURIComponent(id)}/restore`, { expectedVersion })
export const editNote = (quoteId: number, id: string, expectedVersion: number, text: string) => change(quoteId, `notes/${encodeURIComponent(id)}`, { expectedVersion, text }, 'PUT')
export const moveEmail = (quoteId: number, id: number, body: { expectedVersion: number; targetQuoteHistoryId: number; targetExpectedVersion: number; requestId: number | null }) => change(quoteId, `messages/${id}/move`, body)
