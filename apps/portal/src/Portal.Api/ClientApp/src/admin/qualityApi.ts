import { resolveModuleApplicationUrl } from './moduleUrls'

const qualityUrl = resolveModuleApplicationUrl(window.location, 5170)

export async function qualityAdminApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  const method = (init.method ?? 'GET').toUpperCase()
  const shippingImport = path === '/api/shipments/import' && method === 'POST' && init.body instanceof FormData
  if (init.body instanceof FormData) {
    headers.delete('Content-Type')
  } else if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (shippingImport) {
    // Keep this upload CORS-simple for Windows-only IIS. Quality validates the
    // browser's exact Origin and the authenticated user's import permissions.
    headers.delete('X-Requested-With')
  } else if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    headers.set('X-Requested-With', 'XMLHttpRequest')
  }
  let response: Response
  try {
    response = await fetch(`${qualityUrl}${path}`, { ...init, headers, credentials: 'include' })
  } catch {
    throw new Error(`Could not reach Quality Assurance at ${qualityUrl}. Confirm the module is running.`)
  }
  if (!response.ok) {
    const type = response.headers.get('content-type') ?? ''
    const payload = type.includes('json')
      ? await response.json().catch(() => null) as { message?: string; detail?: string } | null
      : null
    throw new Error(payload?.message ?? payload?.detail ?? `Quality Assurance responded ${response.status}.`)
  }
  if (response.status === 204) return undefined as T
  return await response.json() as T
}
