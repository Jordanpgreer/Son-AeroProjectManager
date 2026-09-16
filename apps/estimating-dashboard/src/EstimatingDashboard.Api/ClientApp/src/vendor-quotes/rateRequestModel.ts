import { isOutgoingEmail } from './activityTimelineModel.ts'
import type { VendorDetail, VendorMessage } from './types.ts'

/** Pricing requests are explicit labels on sent emails that belong to an RFQ. */
export function rateRequestThread(message: VendorMessage, threads: VendorDetail[]) {
  if (!isOutgoingEmail(message)) return null
  return threads.find(thread => thread.messages.some(item => item.id === message.id))?.request ?? null
}
