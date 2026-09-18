export const ARDA_STATUS_OPTIONS = [
  'Untouched',
  'In progress',
  'RFQ Sent',
  'Ready for Review',
  'Complete',
  'On Hold',
] as const

export type ArdaStatus = typeof ARDA_STATUS_OPTIONS[number]

export interface PersonalQuote {
  id: number
  quoteNumber: number
  customer: string
  fulcrumQuoteStatus: string
  estimatingRep: string
  totalValue: number
  rfqDueDate: string | null
  automaticEstimatingDueDate: string | null
  estimatingDueDate: string | null
  estimatingDueDateIsOverride: boolean
  estimatingCompletionDate: string | null
  isCompleted: boolean
  isOverdue: boolean
  ardaStatus: ArdaStatus | null
  ardaStatusNotes: string | null
  ardaStatusChangedAt: string | null
  ardaStatusChangedBy: string | null
  version: number
}

export interface QuoteWorkflowUpdate {
  ardaStatus: ArdaStatus | null
  notes: string | null
  estimatingDueDateOverride: string | null
  expectedVersion: number
}

interface ApiErrorBody {
  message?: string
  detail?: string
}

async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    credentials: 'include',
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers,
    },
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as ApiErrorBody | null
    throw new Error(body?.message ?? body?.detail ?? `Request failed (${response.status}).`)
  }
  return response.json() as Promise<T>
}

export function loadPersonalQuotes(signal?: AbortSignal) {
  return api<PersonalQuote[]>('/api/quote-workflow/mine?includeCompleted=true', { signal })
}

export function refreshPersonalQuoteAssignments() {
  return api<PersonalQuote[]>('/api/quote-workflow/refresh?includeCompleted=true', { method: 'POST' })
}

export function updatePersonalQuoteWorkflow(id: number, request: QuoteWorkflowUpdate) {
  return api<PersonalQuote>(`/api/quote-workflow/${id}`, {
    method: 'PUT',
    body: JSON.stringify(request),
  })
}

export function statusSetLabel(changedAt: string | null, timeZone?: string) {
  if (!changedAt) return 'Not set'
  const changed = new Date(changedAt)
  if (Number.isNaN(changed.getTime())) return 'Unknown'
  return new Intl.DateTimeFormat('en-US', {
    dateStyle: 'medium',
    timeStyle: 'short',
    ...(timeZone ? { timeZone } : {}),
  }).format(changed)
}
