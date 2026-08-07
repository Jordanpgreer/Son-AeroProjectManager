import { defaultProjectTrackerApiUrl, resolveProjectTrackerApiUrl } from './apiUrl'

const configuredTrackerUrl = import.meta.env.VITE_PROJECT_TRACKER_URL?.trim()

export const projectTrackerUrl = new URL(
  configuredTrackerUrl || defaultProjectTrackerApiUrl(window.location),
  window.location.origin,
).toString().replace(/\/$/, '')

export function isAdminHash(hash = window.location.hash) {
  return hash.toLowerCase().startsWith('#/admin')
}

function errorMessage(status: number, statusText: string, payload: unknown, source: string) {
  if (typeof payload === 'string' && payload.trim()) return payload.trim()
  if (payload && typeof payload === 'object') {
    const record = payload as Record<string, unknown>
    for (const key of ['detail', 'message', 'title']) {
      if (typeof record[key] === 'string' && record[key].trim()) return record[key].trim()
    }
    if (record.errors && typeof record.errors === 'object') {
      const messages = Object.values(record.errors as Record<string, unknown>)
        .flatMap((value) => Array.isArray(value) ? value : [value])
        .filter((value): value is string => typeof value === 'string')
      if (messages.length) return messages.join(' ')
    }
  }
  return `${source} responded ${status} ${statusText || ''}`.trim()
}

export class TrackerApiError extends Error {
  readonly status: number

  constructor(
    message: string,
    status: number,
  ) {
    super(message)
    this.name = 'TrackerApiError'
    this.status = status
  }
}

export async function trackerApi<T>(
  path: string,
  init: RequestInit = {},
): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  let response: Response
  try {
    response = await fetch(resolveProjectTrackerApiUrl(projectTrackerUrl, path), {
      ...init,
      headers,
      credentials: 'include',
    })
  } catch {
    throw new TrackerApiError(
      `Could not reach Project Tracker at ${projectTrackerUrl}. Confirm it is running and permits the Hub origin.`,
      0,
    )
  }

  if (!response.ok) {
    const contentType = response.headers.get('content-type') ?? ''
    let payload: unknown
    try {
      payload = contentType.includes('json') ? await response.json() : await response.text()
    } catch {
      payload = null
    }
    throw new TrackerApiError(
      errorMessage(response.status, response.statusText, payload, 'Project Tracker'),
      response.status,
    )
  }

  if (response.status === 204) return undefined as T
  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) {
    throw new TrackerApiError(
      'Project Tracker returned a web page instead of API data. The Hub gateway is not configured for this environment.',
      response.status,
    )
  }
  return await response.json() as T
}

export async function trackerFile(path: string): Promise<{ blob: Blob; fileName: string }> {
  let response: Response
  try {
    response = await fetch(resolveProjectTrackerApiUrl(projectTrackerUrl, path), {
      credentials: 'include',
    })
  } catch {
    throw new TrackerApiError(
      `Could not reach Project Tracker at ${projectTrackerUrl}. Confirm it is running and permits the Hub origin.`,
      0,
    )
  }

  if (!response.ok) {
    const contentType = response.headers.get('content-type') ?? ''
    const payload = contentType.includes('json')
      ? await response.json().catch(() => null)
      : await response.text().catch(() => null)
    throw new TrackerApiError(
      errorMessage(response.status, response.statusText, payload, 'Project Tracker'),
      response.status,
    )
  }

  const disposition = response.headers.get('content-disposition') ?? ''
  const utf8Name = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1]
  const fileName = decodeURIComponent(utf8Name ?? plainName ?? 'Project-Tracker-Import.xlsx')
  return { blob: await response.blob(), fileName }
}

export async function portalApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  const response = await fetch(path, { ...init, headers, credentials: 'include' })
  if (!response.ok) {
    const contentType = response.headers.get('content-type') ?? ''
    const payload = contentType.includes('json')
      ? await response.json().catch(() => null)
      : await response.text().catch(() => null)
    throw new TrackerApiError(
      errorMessage(response.status, response.statusText, payload, 'Hub Admin'),
      response.status,
    )
  }
  if (response.status === 204) return undefined as T

  const body = await response.text()
  if (!body.trim()) return undefined as T

  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) {
    throw new TrackerApiError(
      'Hub Admin returned an unexpected response instead of API data.',
      response.status,
    )
  }

  try {
    return JSON.parse(body) as T
  } catch {
    throw new TrackerApiError(
      'Hub Admin returned invalid API data. Please refresh and try again.',
      response.status,
    )
  }
}

export function toErrorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'An unexpected error occurred.'
}
