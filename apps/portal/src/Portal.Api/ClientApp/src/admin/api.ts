import { resolveProjectTrackerApiUrl } from './apiUrl'

const configuredTrackerUrl = import.meta.env.VITE_PROJECT_TRACKER_URL?.trim()

export const projectTrackerUrl = new URL(
  configuredTrackerUrl || '/project-tracker-api',
  window.location.origin,
).toString().replace(/\/$/, '')

export function isAdminHash(hash = window.location.hash) {
  return hash.toLowerCase().startsWith('#/admin')
}

function errorMessage(status: number, statusText: string, payload: unknown) {
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
  return `Project Tracker responded ${status} ${statusText || ''}`.trim()
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
      errorMessage(response.status, response.statusText, payload),
      response.status,
    )
  }

  if (response.status === 204) return undefined as T
  return await response.json() as T
}

export function toErrorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'An unexpected error occurred.'
}
