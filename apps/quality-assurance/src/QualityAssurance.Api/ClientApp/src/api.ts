export class QualityApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'QualityApiError'
    this.status = status
  }
}

export async function qualityApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  let response: Response
  try {
    response = await fetch(path, { ...init, headers, credentials: 'include' })
  } catch {
    throw new QualityApiError('Could not reach the Quality Assurance service.', 0)
  }
  if (!response.ok) {
    const type = response.headers.get('content-type') ?? ''
    const payload = type.includes('json')
      ? await response.json().catch(() => null) as { message?: string; detail?: string } | null
      : null
    throw new QualityApiError(
      payload?.message ?? payload?.detail ?? `Quality Assurance request failed (${response.status}).`,
      response.status,
    )
  }
  if (response.status === 204) return undefined as T
  return await response.json() as T
}
