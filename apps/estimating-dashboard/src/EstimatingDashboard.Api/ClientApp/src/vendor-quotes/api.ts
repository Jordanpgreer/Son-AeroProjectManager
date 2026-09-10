import type { NewVendorRequest, QuoteStatusDetail, QuoteStatusPage, QuoteStatusUpdate, VendorDetail, VendorOptions, VendorPage, VendorSync, VendorUpdate } from './types'

async function request<T>(path: string, init?: RequestInit, base = '/api/vendor-quotes'): Promise<T> {
  const response = await fetch(`${base}${path}`, {
    credentials: 'include', ...init,
    headers: { 'Content-Type': 'application/json', 'X-Arda-Request': 'vendor-quotes', ...init?.headers },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? body?.detail ?? `Request could not be completed (${response.status}).`)
  }
  return response.json() as Promise<T>
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

export function loadQuoteStatuses(search: string, status: string, quote: number | null, page: number, signal?: AbortSignal) {
  const query = new URLSearchParams({ search, status, page: String(page), pageSize: '30' })
  if (quote) query.set('quoteNumber', String(quote))
  return request<QuoteStatusPage>(`?${query}`, { signal }, '/api/quote-status')
}
export const loadQuoteStatusDetail = (id: number, signal?: AbortSignal) => request<QuoteStatusDetail>(`/${id}`, { signal }, '/api/quote-status')
export const loadQuoteStatusOptions = (signal?: AbortSignal) => request<{ statuses: string[]; threadStatuses: string[] }>('/options', { signal }, '/api/quote-status')
export const updateQuoteStatus = (id: number, body: QuoteStatusUpdate) => request<QuoteStatusDetail>(`/${id}`, { method: 'PUT', body: JSON.stringify(body) }, '/api/quote-status')
export const assignQuoteMessage = (id: number, messageId: number, expectedVersion: number, requestId: number) => request<QuoteStatusDetail>(`/${id}/messages/${messageId}/assign`, { method: 'POST', body: JSON.stringify({ expectedVersion, requestId }) }, '/api/quote-status')
