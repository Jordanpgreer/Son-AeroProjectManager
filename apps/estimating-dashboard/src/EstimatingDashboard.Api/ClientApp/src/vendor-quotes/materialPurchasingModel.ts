import type { MaterialFollowUp, MaterialRequirement } from './materialPurchasingTypes.ts'

const HOUR_MS = 60 * 60 * 1000
export const MATERIAL_FOLLOW_UP_BODY = 'Hi team,\n\nJust following up on this request.\n\nDo you have this quote available?\n\nThank you!'

export function materialFollowUpDueAt(sentAt: string) {
  return Date.parse(sentAt) + 24 * HOUR_MS
}

export function materialFollowUpAge(sentAt: string, now: number) {
  const sent = Date.parse(sentAt)
  return Number.isFinite(sent) ? `${Math.max(24, Math.floor((now - sent) / HOUR_MS))} hours without a reply` : 'Needs follow-up'
}

export function materialQuantity(value: number | null): string {
  return value !== null && Number.isFinite(value) ? value.toLocaleString('en-US', { maximumFractionDigits: 8 }) : 'Unknown'
}

export function materialQuantityRange(minimum: number | null, maximum: number | null, unit: string): string {
  const values = minimum === maximum ? materialQuantity(minimum) : `${materialQuantity(minimum)} / ${materialQuantity(maximum)}`
  return `${values}${unit ? ` ${unit}` : ''}`
}

export function canDraftMaterial(material: MaterialRequirement): boolean {
  return material.status === 'needs-rfq' && !!material.vendorId && material.vendorEmails.length > 0
    && material.requiredMinimum !== null && material.requiredMaximum !== null
    && Number.isFinite(material.requiredMinimum) && Number.isFinite(material.requiredMaximum)
    && material.requiredMinimum > 0 && material.requiredMaximum >= material.requiredMinimum
    && material.quoteQuantities.length > 0 && material.quoteQuantities.every(value => Number.isFinite(value) && value > 0)
}

export function materialHasTrackedRequest(material: MaterialRequirement): boolean {
  return material.status === 'needs-rfq' && !!material.sentAt
}

export function materialGroups(materials: MaterialRequirement[]) {
  const followUps = materials.filter((item): item is MaterialRequirement & { followUp: MaterialFollowUp } =>
    item.followUp?.state === 'needs-follow-up')
    .sort((a, b) => Date.parse(a.followUp.sentAt) - Date.parse(b.followUp.sentAt))
  const regular = materials.filter(item => item.followUp?.state !== 'needs-follow-up')
  return {
    followUps,
    pending: regular.filter(item => (item.status === 'needs-rfq' && !materialHasTrackedRequest(item)) || item.status === 'review-required'),
    tracked: regular.filter(materialHasTrackedRequest),
    covered: regular.filter(item => item.status === 'stock-covered' || item.status === 'recent-po'),
    emailsNeeded: regular.filter(item => item.status === 'needs-rfq' && !materialHasTrackedRequest(item)).length,
    reviewsNeeded: regular.filter(item => item.status === 'review-required').length,
  }
}
