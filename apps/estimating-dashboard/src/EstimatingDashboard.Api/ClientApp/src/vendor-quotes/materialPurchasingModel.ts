import type { MaterialRequirement, MaterialStatus } from './materialPurchasingTypes.ts'

export const materialStatusLabels: Record<MaterialStatus, string> = {
  'stock-covered': 'Available stock covers requirement',
  'recent-po': 'Recent PO pricing available',
  'needs-rfq': 'Email needs sending',
  'review-required': 'Review required',
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
  return material.status === 'needs-rfq' && ['waiting on vendor', 'quote received'].includes(material.requestStatus?.trim().toLowerCase() ?? '')
}

export function materialStatusLabel(material: MaterialRequirement): string {
  return materialHasTrackedRequest(material) ? material.requestStatus! : materialStatusLabels[material.status]
}

export function materialGroups(materials: MaterialRequirement[]) {
  return {
    pending: materials.filter(item => (item.status === 'needs-rfq' && !materialHasTrackedRequest(item)) || item.status === 'review-required'),
    tracked: materials.filter(materialHasTrackedRequest),
    covered: materials.filter(item => item.status === 'stock-covered' || item.status === 'recent-po'),
    emailsNeeded: materials.filter(item => item.status === 'needs-rfq' && !materialHasTrackedRequest(item)).length,
    reviewsNeeded: materials.filter(item => item.status === 'review-required').length,
  }
}
