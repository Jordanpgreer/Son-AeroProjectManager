import type { ManualEmailImportResult, ManualEmailPreview } from './types'

async function upload<T>(quoteId: number, operation: string, form: FormData, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`/api/quote-status/${quoteId}/emails/${operation}`, {
    method: 'POST', credentials: 'include', headers: { 'X-Arda-Request': 'vendor-quotes' }, body: form, signal,
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? body?.detail ?? `The email could not be processed (${response.status}).`)
  }
  return response.json() as Promise<T>
}

export function previewManualEmail(quoteId: number, file: File, signal?: AbortSignal) {
  const form = new FormData(); form.append('file', file)
  return upload<ManualEmailPreview>(quoteId, 'preview', form, signal)
}

export function importManualEmail(quoteId: number, file: File, fields: { expectedVersion: number; requestId: number | null; direction: 'incoming' | 'outgoing'; vendorEmail: string; note: string }) {
  const form = new FormData(); form.append('file', file)
  form.append('expectedVersion', String(fields.expectedVersion)); form.append('direction', fields.direction)
  if (fields.requestId !== null) form.append('requestId', String(fields.requestId))
  if (fields.vendorEmail) form.append('vendorEmail', fields.vendorEmail)
  if (fields.note.trim()) form.append('note', fields.note.trim())
  return upload<ManualEmailImportResult>(quoteId, 'import', form)
}
