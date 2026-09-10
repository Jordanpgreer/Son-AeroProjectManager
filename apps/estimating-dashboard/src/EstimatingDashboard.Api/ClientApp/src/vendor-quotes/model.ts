import type { QuoteStatusUpdate, VendorMessage, VendorSync } from './types.ts'

export const VENDOR_STATUSES = ['Untouched', 'Rates requested', 'Waiting on vendor', 'Reply received', 'Quote received', 'Under review', 'Accepted', 'Declined', 'Cancelled']
export const QUOTE_STATUSES = ['Untouched', 'In progress', 'RFQ Sent', 'Ready for Review', 'Complete', 'On Hold']
export const CLOSED_STATUSES = new Set(['Accepted', 'Declined', 'Cancelled', 'Complete'])

export function statusTone(status: string) {
  if (status === 'Accepted' || status === 'Complete') return 'success'
  if (['Quote received', 'Reply received', 'In progress', 'Ready for Review'].includes(status)) return 'blue'
  if (['Waiting on vendor', 'Rates requested', 'RFQ Sent', 'On Hold'].includes(status)) return 'amber'
  return 'neutral'
}

export function quoteFromHash(hash: string) {
  const value = new URLSearchParams(hash.split('?')[1] ?? '').get('quote')
  if (!value || !/^\d+$/.test(value)) return null
  const quote = Number(value)
  return Number.isSafeInteger(quote) && quote > 0 ? quote : null
}

export function dateTime(value: string | null) {
  if (!value) return 'Not recorded'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Not recorded' : date.toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' })
}

export function dateOnly(value: string | null) {
  if (!value) return 'No follow-up date'
  const date = new Date(`${value.slice(0, 10)}T00:00:00`)
  return Number.isNaN(date.getTime()) ? 'No follow-up date' : date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
}

export function isOverdue(followUpDate: string | null, status: string, now = new Date()) {
  if (!followUpDate || CLOSED_STATUSES.has(status)) return false
  const due = new Date(`${followUpDate.slice(0, 10)}T00:00:00`)
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  return !Number.isNaN(due.getTime()) && due < today
}

export function relativeTime(value: string, now = new Date()) {
  const elapsed = Math.max(0, now.getTime() - new Date(value).getTime())
  if (!Number.isFinite(elapsed)) return 'Unknown'
  if (elapsed < 60_000) return 'Just now'
  if (elapsed < 3_600_000) return `${Math.floor(elapsed / 60_000)}m ago`
  if (elapsed < 86_400_000) return `${Math.floor(elapsed / 3_600_000)}h ago`
  if (elapsed < 7 * 86_400_000) return `${Math.floor(elapsed / 86_400_000)}d ago`
  return dateOnly(value)
}

export function syncState(sync: VendorSync[], now = new Date()) {
  if (!sync.length) return { tone: 'neutral', label: 'Outlook not connected' }
  if (sync.some(item => item.error)) return { tone: 'amber', label: 'Outlook needs attention' }
  const latest = Math.max(...sync.map(item => new Date(item.lastCheckedAt).getTime()))
  return Number.isFinite(latest) && now.getTime() - latest < 10 * 60_000
    ? { tone: 'success', label: 'Outlook connected' }
    : { tone: 'neutral', label: 'Outlook sync paused' }
}

export function mailtoVendor(email: string, quoteNumber: number) {
  return `mailto:${encodeURIComponent(email)}?subject=${encodeURIComponent(`Quote ${quoteNumber}`)}`
}

export function attachmentSize(bytes: number) {
  return bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${Math.ceil(bytes / 1024)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export interface UpdateDraft { status: string; followUpDate: string; note: string }
export function isUpdateDirty(draft: UpdateDraft, status: string, followUpDate: string | null) {
  return draft.note.trim() !== '' || draft.status !== status || draft.followUpDate !== (followUpDate?.slice(0, 10) || '')
}
export function makeUpdate(draft: UpdateDraft, version: number): QuoteStatusUpdate {
  return { expectedVersion: version, status: draft.status, followUpDate: draft.followUpDate || null, note: draft.note.trim() || null }
}

/** Refresh server fields without losing a separately drafted note or follow-up. */
export function synchronizeActivityDraft(draft: UpdateDraft, previous: Pick<UpdateDraft, 'status' | 'followUpDate'>, next: Pick<UpdateDraft, 'status' | 'followUpDate'>, isOverallQuote: boolean): UpdateDraft {
  return {
    status: isOverallQuote || draft.status === previous.status ? next.status : draft.status,
    followUpDate: draft.followUpDate === previous.followUpDate ? next.followUpDate : draft.followUpDate,
    note: draft.note,
  }
}

export function canAssignMessageToContact(message: Pick<VendorMessage, 'vendorEmail' | 'direction' | 'fromAddress' | 'toAddresses'>, contact: string) {
  const email = contact.trim().toLowerCase()
  if (!email) return true
  if (message.vendorEmail) return email === message.vendorEmail.trim().toLowerCase()
  if (['incoming', 'inbound', 'received'].includes(message.direction.toLowerCase())) return email === message.fromAddress.trim().toLowerCase()
  return message.toAddresses.some(address => email === address.trim().toLowerCase())
}
