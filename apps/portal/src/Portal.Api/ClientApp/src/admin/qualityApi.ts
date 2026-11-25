import { resolveModuleApplicationUrl } from './moduleUrls'

const qualityUrl = resolveModuleApplicationUrl(window.location, 5170)

export async function qualityAdminApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  const method = (init.method ?? 'GET').toUpperCase()
  const shippingImport = path === '/api/shipments/import' && method === 'POST' && init.body instanceof FormData
  const teamSave = method === 'POST' && /^\/api\/admin\/teams\/save(?:\/\d+)?$/.test(path) && typeof init.body === 'string'
  if (init.body instanceof FormData) {
    headers.delete('Content-Type')
  } else if (teamSave) {
    headers.set('Content-Type', 'text/plain;charset=UTF-8')
  } else if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (shippingImport || teamSave) {
    // Quality checks the exact Origin and user permissions for these POSTs.
    // A custom header would require an anonymous preflight on Windows-only IIS.
    headers.delete('X-Requested-With')
  } else if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    headers.set('X-Requested-With', 'XMLHttpRequest')
  }
  let response: Response
  try {
    response = await fetch(`${qualityUrl}${path}`, { ...init, headers, credentials: 'include' })
  } catch {
    throw new Error(`The browser could not connect to Quality Assurance at ${qualityUrl}. Open Quality Assurance from Arda to check network access and Windows sign-in.`)
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
