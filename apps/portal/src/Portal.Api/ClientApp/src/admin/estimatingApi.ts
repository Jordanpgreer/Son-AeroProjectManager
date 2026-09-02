import { resolveModuleApplicationUrl } from './moduleUrls'

const estimatingUrl = resolveModuleApplicationUrl(window.location, 5160)

export async function estimatingAdminApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')

  let response: Response
  try {
    response = await fetch(`${estimatingUrl}${path}`, {
      ...init,
      headers,
      credentials: 'include',
    })
  } catch {
    throw new Error(`Could not reach Estimating at ${estimatingUrl}. Confirm the module is running.`)
  }

  if (!response.ok) {
    const type = response.headers.get('content-type') ?? ''
    const payload = type.includes('json')
      ? await response.json().catch(() => null) as { message?: string; detail?: string; title?: string } | null
      : null
    throw new Error(payload?.detail ?? payload?.message ?? payload?.title ?? `Estimating responded ${response.status}.`)
  }

  if (response.status === 204) return undefined as T
  return await response.json() as T
}
