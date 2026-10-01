export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    ...init,
    headers: init?.body instanceof FormData
      ? init.headers
      : { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { detail?: string } | null
    throw new ApiError(body?.detail ?? `The request failed (${response.status}).`, response.status)
  }
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

export function queryString(values: Record<string, string | null | undefined>) {
  const search = new URLSearchParams()
  Object.entries(values).forEach(([key, value]) => {
    if (value) search.set(key, value)
  })
  const value = search.toString()
  return value ? `?${value}` : ''
}
