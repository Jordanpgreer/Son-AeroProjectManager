import type { NewVendorRequest, QuoteStatusDetail, QuoteStatusOptions, QuoteStatusPage, QuoteStatusUpdate, VendorDetail, VendorOptions, VendorPage, VendorSync, VendorUpdate } from './types'

async function request<T>(path: string, init?: RequestInit, base = '/api/vendor-quotes'): Promise<T> {
  const headers = new Headers(init?.headers)
  if (!(init?.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  headers.set('X-Arda-Request', 'vendor-quotes')
  const response = await fetch(`${base}${path}`, {
    credentials: 'include', ...init,
    headers,
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new QuoteApiError(response.status, body?.code ?? 'RequestFailed', body?.message ?? body?.detail ?? `Request could not be completed (${response.status}).`)
  }
  return response.json() as Promise<T>
}

export class QuoteApiError extends Error {
  status: number
  code: string
  constructor(status: number, code: string, message: string) { super(message); this.status = status; this.code = code }
}

export function loadVendorRequests(search: string, status: string, quote: number | null, page: number, signal?: AbortSignal) {
  const query = new URLSearchParams({ search, status, page: String(page), pageSize: '30' })
  if (quote) query.set('quoteNumber', String(quote))
  return request<VendorPage>(`?${query}`, { signal })
}
export const loadVendorDetail = (id: number, signal?: AbortSignal) => request<VendorDetail>(`/${id}`, { signal })
export const loadVendorOptions = (search = '', signal?: AbortSignal) => request<VendorOptions>(`/options?search=${encodeURIComponent(search)}`, { signal })
export const loadVendorSync = (signal?: AbortSignal) => request<VendorSync[]>('/sync', { signal })
export const createVendorRequest = (body: NewVendorRequest) => request<VendorDetail>('', { method: 'POST', body: JSON.stringify(body) })
export const updateVendorRequest = (id: number, body: VendorUpdate) => request<VendorDetail>(`/${id}`, { method: 'PUT', body: JSON.stringify(body) })
export const addVendorNote = (id: number, expectedVersion: number, text: string) => request<VendorDetail>(`/${id}/notes`, { method: 'POST', body: JSON.stringify({ expectedVersion, text }) })

export function loadQuoteStatuses(search: string, status: string, fulcrumStatus: string, quote: number | null, page: number, scope: 'mine-active' | 'mine' | 'all', signal?: AbortSignal) {
  const query = new URLSearchParams({ search, status, fulcrumStatus, scope, page: String(page), pageSize: '30' })
  if (quote) query.set('quoteNumber', String(quote))
  return request<QuoteStatusPage>(`?${query}`, { signal }, '/api/quote-status')
}
export const loadQuoteStatusDetail = (id: number, signal?: AbortSignal) => request<QuoteStatusDetail>(`/${id}`, { signal }, '/api/quote-status')
export const loadQuoteStatusOptions = (signal?: AbortSignal) => request<QuoteStatusOptions>('/options', { signal }, '/api/quote-status')
export const updateQuoteStatus = (id: number, body: QuoteStatusUpdate) => request<QuoteStatusDetail>(`/${id}`, { method: 'PUT', body: JSON.stringify(body) }, '/api/quote-status')
export const assignQuoteMessage = (id: number, messageId: number, expectedVersion: number, requestId: number) => request<QuoteStatusDetail>(`/${id}/messages/${messageId}/assign`, { method: 'POST', body: JSON.stringify({ expectedVersion, requestId }) }, '/api/quote-status')
export const saveQuoteFileLocation = (id: number, expectedVersion: number, path: string, previousPath?: string) => request<QuoteStatusDetail>(`/${id}/file-locations`, { method: 'PUT', body: JSON.stringify({ expectedVersion, path, previousPath: previousPath ?? null }) }, '/api/quote-status')
export const removeQuoteFileLocation = (id: number, expectedVersion: number, path: string) => request<QuoteStatusDetail>(`/${id}/file-locations`, { method: 'DELETE', body: JSON.stringify({ expectedVersion, path }) }, '/api/quote-status')
export function copyQuoteFileToLocation(id: number, expectedVersion: number, path: string, file: File, collision: 'reject' | 'overwrite' | 'rename' = 'reject') {
  const body = new FormData()
  body.set('expectedVersion', String(expectedVersion)); body.set('path', path); body.set('collision', collision); body.set('file', file)
  return request<QuoteStatusDetail>(`/${id}/file-locations/upload`, { method: 'POST', body }, '/api/quote-status')
}
