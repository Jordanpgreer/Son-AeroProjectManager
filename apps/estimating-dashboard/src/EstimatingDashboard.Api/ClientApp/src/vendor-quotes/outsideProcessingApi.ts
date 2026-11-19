import { QuoteApiError } from './api.ts'
import type { PlanningSource } from './purchasingReviewModel'

export interface OutsideProcessingOperation {
  operationKey: string
  itemId: string
  operationId: string
  partNumber: string
  name: string
  instructions: string | null
  requiredMinimum: number | null
  requiredMaximum: number | null
  unitOfMeasure?: string | null
  quoteQuantities: number[]
  status: 'needs-rfq' | 'review-required'
  vendorId: string | null
  vendorName: string | null
  vendorEmails: string[]
  issues: string[]
  requestId: number | null
  requestStatus: string | null
  sentAt: string | null
}

export interface OutsideProcessingRequirements {
  quoteHistoryId: number
  quoteNumber: number
  checkedAt: string
  operations: OutsideProcessingOperation[]
  warnings: string[]
  planningSource?: PlanningSource
}

async function request(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(`/api/quote-status${path}`, {
    ...init, credentials: 'include', headers: { 'X-Arda-Request': 'vendor-quotes' },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new QuoteApiError(response.status, body?.code ?? 'RequestFailed',
      body?.message ?? `Outside processing review failed (${response.status}).`)
  }
  return response
}

export async function loadOutsideProcessing(id: number, signal?: AbortSignal): Promise<OutsideProcessingRequirements> {
  return (await request(`/${id}/outside-processing`, { signal })).json()
}

export async function createOutsideProcessingDraft(id: number, key: string): Promise<Blob> {
  const response = await request(`/${id}/outside-processing/${encodeURIComponent(key)}/draft`, { method: 'POST' })
  if (response.headers.get('Content-Type')?.split(';')[0].toLowerCase() !== 'message/rfc822')
    throw new Error('The Outlook draft was not returned. Refresh the OP review and try again.')
  return response.blob()
}
