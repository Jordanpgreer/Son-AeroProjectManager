import { activityDescription } from './lifecycleModel.ts'
import type { QuoteActivity, VendorActivity, VendorDetail, VendorMessage } from './types.ts'

export type TimelineActivity = QuoteActivity | VendorActivity
export type TimelineEntry = {
  key: string; occurredAt: string; requestId: number | null; threadName: string | null; partNumber: string | null
} & ({ type: 'activity'; activity: TimelineActivity } | { type: 'email'; message: VendorMessage; importActivity?: TimelineActivity })

export function isOutgoingEmail(message: VendorMessage) {
  return ['outbound', 'outgoing', 'sent'].includes(message.direction.toLowerCase())
}

export function timelineSummary(item: TimelineActivity) {
  if (item.kind === 'note') return 'Added a note.'
  if (item.kind === 'status' && item.newValue) return `Status set to ${item.newValue}.`
  if (item.kind === 'created') return item.newValue ? `Created an RFQ. Status set to ${item.newValue}.` : 'Created an RFQ.'
  return activityDescription(item)
}

/** A message replaces only its matching import event; assignment/removal history stays visible. */
export function buildActivityTimeline(activity: TimelineActivity[], messages: VendorMessage[] = [], threads: VendorDetail[] = []): TimelineEntry[] {
  const threadByMessage = new Map(threads.flatMap(thread => thread.messages.map(message => [message.id, thread.request] as const)))
  const emails: TimelineEntry[] = [...new Map(messages.map(message => [message.id, message])).values()].map(message => {
    const request = threadByMessage.get(message.id)
    return { key: `email-${message.id}`, type: 'email', message, occurredAt: isOutgoingEmail(message) ? message.sentAt : message.receivedAt || message.sentAt,
      requestId: request?.id ?? null, threadName: request?.vendorName || request?.title || null, partNumber: request?.partNumber ?? null }
  })
  const matched = new Set<string>()
  const events: TimelineEntry[] = []
  for (const item of activity) {
    const requestId = 'requestId' in item ? item.requestId : null
    // Import events predate message IDs in the activity contract. Match their exact subject,
    // thread, and import instant, consuming each message at most once. Never hide audit edits.
    const isImport = ['email', 'email-unassigned'].includes(item.kind) && /^Email (received from|sent to) /.test(item.text)
    const match = isImport ? emails.filter(entry => entry.type === 'email' && !matched.has(entry.key)
      && item.newValue === entry.message.subject && (!('requestId' in item) || requestId === entry.requestId)
      && Math.abs(Date.parse(item.occurredAt) - Date.parse(entry.message.importedAt)) <= 1000)
      .sort((a, b) => Math.abs(Date.parse(item.occurredAt) - Date.parse(a.type === 'email' ? a.message.importedAt : ''))
        - Math.abs(Date.parse(item.occurredAt) - Date.parse(b.type === 'email' ? b.message.importedAt : '')))[0] : undefined
    if (match?.type === 'email') { matched.add(match.key); match.importActivity = item; continue }
    events.push({ key: `activity-${item.id}`, type: 'activity', activity: item, occurredAt: item.occurredAt, requestId,
      threadName: 'vendorName' in item ? item.vendorName : null, partNumber: 'partNumber' in item ? item.partNumber : null })
  }
  return [...events, ...emails].sort((a, b) => Date.parse(b.occurredAt) - Date.parse(a.occurredAt) || a.key.localeCompare(b.key))
}
