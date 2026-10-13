import { QuoteApiError } from './api.ts'
import type { MaterialQuantityOverride, MaterialRequirements } from './materialPurchasingTypes.ts'

async function materialRequest(path: string, init?: RequestInit) {
  const headers = new Headers({ 'X-Arda-Request': 'vendor-quotes' })
  if (init?.body) headers.set('Content-Type', 'application/json')
  const response = await fetch(`/api/quote-status${path}`, {
    credentials: 'include', ...init, headers,
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new QuoteApiError(response.status, body?.code ?? 'RequestFailed', body?.message ?? body?.detail ?? `Material review could not be completed (${response.status}).`)
  }
  return response
}

export async function loadMaterialRequirements(id: number, signal?: AbortSignal): Promise<MaterialRequirements> {
  const response = await materialRequest(`/${id}/materials`, { signal })
  return response.json()
}

export async function createMaterialDraft(id: number, itemId: string, signal?: AbortSignal): Promise<Blob> {
  const response = await materialRequest(`/${id}/materials/${encodeURIComponent(itemId)}/draft`, { method: 'POST', signal })
  const contentType = response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase()
  if (contentType !== 'message/rfc822' && contentType !== 'application/octet-stream') {
    throw new Error('An Outlook draft was not returned. Refresh the material review and try again.')
  }
  return response.blob()
}

export async function saveMaterialQuantities(id: number, expectedVersion: number, quantities: MaterialQuantityOverride[]): Promise<MaterialRequirements> {
  const response = await materialRequest(`/${id}/materials/quantities`, {
    method: 'PUT', body: JSON.stringify({ expectedVersion, quantities }),
  })
  return response.json()
}

export function downloadMaterialDraft(blob: Blob, quoteNumber: number, partNumber: string) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `Quote ${quoteNumber} - ${partNumber.replace(/[<>:"/\\|?*\p{Cc}]/gu, '_').slice(0, 120)}.eml`
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
