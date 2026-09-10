import type { QuoteActivity, VendorActivity, VendorMessage, VendorRequest } from './types.ts'

export function editableNoteId(activity: QuoteActivity | VendorActivity): string | null {
  if (activity.kind !== 'note') return null
  const id = typeof activity.id === 'string' ? activity.id : activity.activityId
  return id && /^(quote|thread)-[1-9]\d*$/.test(id) ? id : null
}

export function activityDescription(activity: Pick<VendorActivity, 'kind' | 'text'>): string {
  if (['email-removed', 'email-restored', 'email-moved'].includes(activity.kind))
    return activity.text.replace(/^(Email (?:removed from Arda|restored(?: to Arda)?|moved(?: here)?)) \(#\d+\)/, '$1')
  if (['note-edited', 'note-removed', 'note-restored'].includes(activity.kind))
    return activity.text.replace(/^(Internal note (?:edited|removed|restored)) \((?:quote|thread)-\d+\)/, '$1')
  return activity.text
}

export function matchingMoveThreads(message: Pick<VendorMessage, 'vendorEmail'>, threads: VendorRequest[]) {
  const email = message.vendorEmail?.trim().toLowerCase()
  return threads.filter(thread => !thread.vendorEmail || (Boolean(email) && thread.vendorEmail.trim().toLowerCase() === email))
}

export function nextMenuIndex(current: number, key: string, count: number) {
  if (!count) return -1
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  if (key === 'ArrowDown') return (current + 1 + count) % count
  if (key === 'ArrowUp') return current < 0 ? count - 1 : (current - 1 + count) % count
  return current
}
